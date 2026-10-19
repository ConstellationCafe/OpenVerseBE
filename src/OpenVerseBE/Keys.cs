namespace OpenVerseBE;

// The key layout is a contract with OpenVerse.Battle (RedisBattleDeckStore / RedisRoomGate): it reads room and bdeck
// keys directly, so a rename here has to be mirrored there
public sealed class Keys(string prefix)
{
    public string ViewerSeq => $"{prefix}seq:viewer";
    public string Player(string udid) => $"{prefix}player:{udid}";
    public string Token(string tokenHash) => $"{prefix}token:{tokenHash}";
    public string Room(string roomId) => $"{prefix}room:{roomId}";
    public string UserRoom(string udid) => $"{prefix}user_room:{udid}";
    public string BattleDeck(string roomId, bool isOwner) => $"{prefix}bdeck:{roomId}:{(isOwner ? "owner" : "visitor")}";
}
