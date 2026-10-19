using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace OpenVerseBE;

public sealed record Player(string Udid, long ViewerId, string Name);

public sealed class PlayerRepository(IConnectionMultiplexer redis, Keys keys, BeOptions options)
{
    // Each Api used to hand out viewer ids from 100001 on its own, so two PCs pointed at one server would both be
    // 100001. Allocating them here keeps them unique across every player of the server
    const long FirstViewerId = 100001;

    IDatabase Db => redis.GetDatabase();

    public async Task<(Player Player, string Token)> Register(string udid, string name)
    {
        var db = Db;
        var key = keys.Player(udid);

        var viewerId = await ExistingViewerId(db, key) ?? await AllocateViewerId(db, key);

        // a new token per registration: the Api re-registers after a restart, and the old token stops working
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var hash = Hash(token);
        var old = await db.HashGetAsync(key, "token_hash");
        if (!old.IsNullOrEmpty) await db.KeyDeleteAsync(keys.Token(old.ToString()));

        await db.HashSetAsync(key, new HashEntry[]
        {
            new("name", name),
            new("token_hash", hash),
            new("last_seen", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
        });
        await db.StringSetAsync(keys.Token(hash), udid, options.TokenTtl);
        return (new Player(udid, viewerId, name), token);
    }

    public async Task<Player?> ByToken(string token)
    {
        var db = Db;
        var udid = await db.StringGetAsync(keys.Token(Hash(token)));
        if (udid.IsNullOrEmpty) return null;
        return await ByUdid(udid.ToString());
    }

    public async Task<Player?> ByUdid(string udid)
    {
        var fields = await Db.HashGetAsync(keys.Player(udid), new RedisValue[] { "viewer_id", "name" });
        if (fields[0].IsNull) return null;
        return new Player(udid, (long)fields[0], fields[1].IsNull ? "" : fields[1].ToString());
    }

    static async Task<long?> ExistingViewerId(IDatabase db, string key)
    {
        var v = await db.HashGetAsync(key, "viewer_id");
        return v.IsNull ? null : (long)v;
    }

    async Task<long> AllocateViewerId(IDatabase db, string key)
    {
        await db.StringSetAsync(keys.ViewerSeq, FirstViewerId - 1, when: When.NotExists);
        var fresh = await db.StringIncrementAsync(keys.ViewerSeq);
        // two registrations of one udid racing: the first write wins and the loser adopts it
        if (await db.HashSetAsync(key, "viewer_id", fresh, When.NotExists)) return fresh;
        return (long)await db.HashGetAsync(key, "viewer_id");
    }

    static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
