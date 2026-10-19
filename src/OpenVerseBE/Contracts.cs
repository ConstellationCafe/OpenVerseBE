namespace OpenVerseBE;

// Wire contract with OpenVerse.Api (RemoteRoomBackend). JSON is snake_case on both sides

public sealed record RegisterRequest(string Udid, string Name);

public sealed record RegisterResponse(long ViewerId, string Token);

public sealed record CreateRoomRequest(
    int BattleType,
    int BattleRule,
    int DeckFormat,
    int TwoPickType,
    bool CanFriendWatch,
    bool CanGuildWatch);

public sealed record PlayerInfo(long ViewerId, string Name);

public sealed record RoomResponse(
    string RoomId,
    string BattleId,
    string NodeServerUrl,
    int BattleType,
    int BattleRule,
    int DeckFormat,
    int TwoPickType,
    bool CanFriendWatch,
    bool CanGuildWatch,
    bool IsOwner,
    PlayerInfo Owner,
    PlayerInfo? Visitor);

// The deck the participant's own Api resolved from its local deck store. The room, the seat and the source IP are
// filled in here: the caller cannot choose them
public sealed record DeckRequest(
    int ClassId,
    int SubClassId,
    int CharaId,
    long SleeveId,
    int LeaderSkinId,
    int[] CardIds,
    string UserName);

public sealed record ErrorResponse(string Error);
