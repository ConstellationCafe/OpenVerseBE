using System.Text.Json;
using StackExchange.Redis;

namespace OpenVerseBE;

public enum EnterResult { Ok, NotFound, Full, OwnRoom }

public sealed record Room(
    string RoomId,
    string BattleId,
    string NodeServerUrl,
    int BattleType,
    int BattleRule,
    int DeckFormat,
    int TwoPickType,
    bool CanFriendWatch,
    bool CanGuildWatch,
    PlayerInfo Owner,
    string OwnerUdid,
    PlayerInfo? Visitor,
    string? VisitorUdid)
{
    public RoomResponse ToResponse(string callerUdid) => new(
        RoomId, BattleId, NodeServerUrl, BattleType, BattleRule, DeckFormat, TwoPickType,
        CanFriendWatch, CanGuildWatch, OwnerUdid == callerUdid, Owner, Visitor);
}

public sealed class RoomRepository(IConnectionMultiplexer redis, Keys keys, BeOptions options)
{
    // The check and the write have to be one step, or two visitors entering the same code at once both get in.
    // KEYS[1] room, ARGV: udid, viewer_id, name, ttl seconds. returns 1 ok, -1 no room, -2 taken, -3 own room
    const string EnterScript = """
        if redis.call('EXISTS', KEYS[1]) == 0 then return -1 end
        local owner = redis.call('HGET', KEYS[1], 'owner_udid')
        if owner == ARGV[1] then return -3 end
        local visitor = redis.call('HGET', KEYS[1], 'visitor_udid')
        if visitor and visitor ~= '' and visitor ~= ARGV[1] then return -2 end
        redis.call('HSET', KEYS[1], 'visitor_udid', ARGV[1], 'visitor_viewer', ARGV[2], 'visitor_name', ARGV[3])
        redis.call('EXPIRE', KEYS[1], ARGV[4])
        return 1
        """;

    IDatabase Db => redis.GetDatabase();

    public async Task<Room> Create(Player owner, CreateRoomRequest req)
    {
        // the client creates a fresh room each time it opens the room screen; the previous one is abandoned
        await CloseOwnedBy(owner.Udid);

        var db = Db;
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var roomId = Random.Shared.Next(10000, 100000).ToString();
            var key = keys.Room(roomId);
            var tran = db.CreateTransaction();
            tran.AddCondition(Condition.KeyNotExists(key));
            _ = tran.HashSetAsync(key, new HashEntry[]
            {
                new("room_id", roomId),
                new("battle_id", Guid.NewGuid().ToString("N")),
                new("node_url", options.NodeServerUrl),
                new("battle_type", req.BattleType),
                new("battle_rule", req.BattleRule),
                new("deck_format", req.DeckFormat),
                new("two_pick_type", req.TwoPickType),
                new("can_friend_watch", req.CanFriendWatch ? 1 : 0),
                new("can_guild_watch", req.CanGuildWatch ? 1 : 0),
                new("owner_udid", owner.Udid),
                new("owner_viewer", owner.ViewerId),
                new("owner_name", owner.Name),
                new("visitor_udid", ""),
                new("visitor_viewer", 0),
                new("visitor_name", ""),
                new("created_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            });
            _ = tran.KeyExpireAsync(key, options.RoomTtl);
            _ = tran.StringSetAsync(keys.UserRoom(owner.Udid), roomId, options.RoomTtl);
            if (await tran.ExecuteAsync())
                return (await Get(roomId))!;
        }
        throw new InvalidOperationException("failed to allocate a unique room_id");
    }

    public async Task<(EnterResult Result, Room? Room)> Enter(Player visitor, string roomId)
    {
        var db = Db;
        var r = (int)await db.ScriptEvaluateAsync(EnterScript,
            new RedisKey[] { keys.Room(roomId) },
            new RedisValue[] { visitor.Udid, visitor.ViewerId, visitor.Name, (long)options.RoomTtl.TotalSeconds });
        var result = r switch
        {
            1 => EnterResult.Ok,
            -2 => EnterResult.Full,
            -3 => EnterResult.OwnRoom,
            _ => EnterResult.NotFound,
        };
        if (result != EnterResult.Ok) return (result, null);
        await db.StringSetAsync(keys.UserRoom(visitor.Udid), roomId, options.RoomTtl);
        return (result, await Get(roomId));
    }

    // leave_room carries the room id and only ever empties the visitor seat, whoever sends it
    public async Task Leave(string roomId)
    {
        var db = Db;
        var key = keys.Room(roomId);
        var visitor = await db.HashGetAsync(key, "visitor_udid");
        if (!await db.KeyExistsAsync(key)) return;
        await db.HashSetAsync(key, new HashEntry[] { new("visitor_udid", ""), new("visitor_viewer", 0), new("visitor_name", "") });
        await db.KeyDeleteAsync(keys.BattleDeck(roomId, isOwner: false));
        if (!visitor.IsNullOrEmpty) await db.KeyDeleteAsync(keys.UserRoom(visitor.ToString()));
    }

    // close_room / force_release_room: only the owner's room goes away. a visitor calling it is a no-op
    public async Task CloseOwnedBy(string udid)
    {
        var mine = await Mine(udid);
        if (mine is not { IsOwner: true }) return;
        var roomId = mine.Value.Room.RoomId;
        var db = Db;
        await db.KeyDeleteAsync(new RedisKey[]
        {
            keys.Room(roomId),
            keys.BattleDeck(roomId, isOwner: true),
            keys.BattleDeck(roomId, isOwner: false),
            keys.UserRoom(udid),
        });
        if (mine.Value.Room.VisitorUdid is { Length: > 0 } v) await db.KeyDeleteAsync(keys.UserRoom(v));
    }

    // The room a player is sitting in, and which seat. a pointer left behind by a closed room or a seat someone else
    // has taken since counts as no room
    public async Task<(Room Room, bool IsOwner)?> Mine(string udid)
    {
        var roomId = await Db.StringGetAsync(keys.UserRoom(udid));
        if (roomId.IsNullOrEmpty) return null;
        var room = await Get(roomId.ToString());
        if (room is null) return null;
        if (room.OwnerUdid == udid) return (room, true);
        if (room.VisitorUdid == udid) return (room, false);
        return null;
    }

    // The seat and the address are stamped here rather than taken from the caller. sourceIp is the player's public
    // address as this server saw it, which is the address their game will open the battle socket from; the battle
    // server compares the two to tell the seats apart (see desync.md: copied installs share one viewer id)
    public async Task<bool> SaveDeck(string udid, DeckRequest deck, string sourceIp)
    {
        var mine = await Mine(udid);
        if (mine is null) return false;
        var (room, isOwner) = mine.Value;
        var payload = JsonSerializer.Serialize(new
        {
            room_id = room.RoomId,
            is_owner = isOwner,
            class_id = deck.ClassId,
            sub_class_id = deck.SubClassId,
            chara_id = deck.CharaId,
            sleeve_id = deck.SleeveId,
            leader_skin_id = deck.LeaderSkinId,
            card_ids = deck.CardIds,
            user_name = deck.UserName,
            source_ip = sourceIp,
        });
        var db = Db;
        await db.StringSetAsync(keys.BattleDeck(room.RoomId, isOwner), payload, options.RoomTtl);
        await db.KeyExpireAsync(keys.Room(room.RoomId), options.RoomTtl);
        return true;
    }

    public async Task<Room?> Get(string roomId)
    {
        var h = await Db.HashGetAllAsync(keys.Room(roomId));
        if (h.Length == 0) return null;
        var f = h.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
        string S(string k) => f.GetValueOrDefault(k, "");
        int I(string k) => int.TryParse(S(k), out var n) ? n : 0;
        long L(string k) => long.TryParse(S(k), out var n) ? n : 0;
        var visitorUdid = S("visitor_udid");
        return new Room(
            S("room_id"), S("battle_id"), S("node_url"),
            I("battle_type"), I("battle_rule"), I("deck_format"), I("two_pick_type"),
            I("can_friend_watch") == 1, I("can_guild_watch") == 1,
            new PlayerInfo(L("owner_viewer"), S("owner_name")), S("owner_udid"),
            visitorUdid.Length > 0 ? new PlayerInfo(L("visitor_viewer"), S("visitor_name")) : null,
            visitorUdid.Length > 0 ? visitorUdid : null);
    }
}
