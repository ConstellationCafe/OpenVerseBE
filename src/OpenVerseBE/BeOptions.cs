namespace OpenVerseBE;

// Everything comes from environment variables so the systemd unit is the single place a host configures the server
public sealed class BeOptions
{
    // StackExchange.Redis connection string, e.g. "127.0.0.1:6379,password=..."
    public string Redis { get; init; } = "127.0.0.1:6379";

    // Prefix for every key, so tests (or a second instance) can share one Redis without touching live data.
    // OpenVerse.Battle must use the same prefix (OPENVERSE_REDIS_PREFIX)
    public string KeyPrefix { get; init; } = "ov:";

    // What clients are told to connect to for the battle socket: host:port, no scheme (the client prepends ws://)
    public string NodeServerUrl { get; init; } = "127.0.0.1:3001";

    // Optional shared secret a local Api must present to register a player. Empty means anyone may register
    public string CafeKey { get; init; } = "";

    // Reverse proxies whose X-Forwarded-For is believed, besides loopback. Under docker compose Caddy is another
    // container, so its address has to be named here or every player is recorded as Caddy's IP and the battle server
    // can no longer tell the seats apart by address
    public System.Net.IPAddress[] TrustedProxies { get; init; } = [];

    public TimeSpan RoomTtl { get; init; } = TimeSpan.FromHours(3);

    public TimeSpan TokenTtl { get; init; } = TimeSpan.FromDays(30);

    public static BeOptions FromEnvironment()
    {
        static string? Env(string k) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : null;
        var d = new BeOptions();
        return new BeOptions
        {
            Redis = Env("OVBE_REDIS") ?? d.Redis,
            KeyPrefix = Env("OVBE_KEY_PREFIX") ?? d.KeyPrefix,
            NodeServerUrl = Env("OVBE_NODE_URL") ?? d.NodeServerUrl,
            CafeKey = Env("OVBE_CAFE_KEY") ?? d.CafeKey,
            RoomTtl = int.TryParse(Env("OVBE_ROOM_TTL_MINUTES"), out var m) && m > 0 ? TimeSpan.FromMinutes(m) : d.RoomTtl,
            TrustedProxies = (Env("OVBE_TRUSTED_PROXIES") ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(System.Net.IPAddress.Parse)
                .ToArray(),
        };
    }
}
