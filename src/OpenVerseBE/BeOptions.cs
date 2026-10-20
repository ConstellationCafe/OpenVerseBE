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

    // How many rooms may be open at once across the whole server. 0 means no limit
    public int MaxRooms { get; init; }

    public TimeSpan TokenTtl { get; init; } = TimeSpan.FromDays(30);

    // A copy of a player's Shadowverse data folder (manifest/, a/, b/, s/, v/, m/, f/), for players whose own copy is
    // missing files. Empty means no game files are served
    public string AssetDir { get; init; } = "";

    // card_master_full.csv.gz, for players with no card master of their own. Empty or missing means none is served
    public string CardMasterPath { get; init; } = "";

    // Somewhere writable to keep the asset index between restarts. Empty means it is rebuilt on every start
    public string StateDir { get; init; } = "";

    public static BeOptions FromEnvironment() => From(Environment.GetEnvironmentVariable);

    // Separate from the process environment so the parsing can be tested without changing it under a running server
    public static BeOptions From(Func<string, string?> lookup)
    {
        string? Env(string k) => lookup(k) is { Length: > 0 } v ? v : null;
        var d = new BeOptions();
        return new BeOptions
        {
            Redis = Env("OVBE_REDIS") ?? d.Redis,
            KeyPrefix = Env("OVBE_KEY_PREFIX") ?? d.KeyPrefix,
            NodeServerUrl = Env("OVBE_NODE_URL") ?? d.NodeServerUrl,
            CafeKey = Env("OVBE_CAFE_KEY") ?? d.CafeKey,
            RoomTtl = int.TryParse(Env("OVBE_ROOM_TTL_MINUTES"), out var m) && m > 0 ? TimeSpan.FromMinutes(m) : d.RoomTtl,
            MaxRooms = Env("OVBE_MAX_ROOMS") is { } max
                ? int.TryParse(max, out var n) && n >= 0
                    ? n
                    : throw new InvalidOperationException($"OVBE_MAX_ROOMS must be a whole number >= 0 (0 = no limit), got '{max}'")
                : d.MaxRooms,
            TrustedProxies = (Env("OVBE_TRUSTED_PROXIES") ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(System.Net.IPAddress.Parse)
                .ToArray(),
            AssetDir = Env("OVBE_ASSET_DIR") ?? d.AssetDir,
            CardMasterPath = Env("OVBE_CARD_MASTER") ?? d.CardMasterPath,
            StateDir = Env("OVBE_STATE_DIR") ?? d.StateDir,
        };
    }
}
