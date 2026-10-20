namespace OpenVerseBE;

// Who may download game files right now. At most MaxActive players at once (the bandwidth budget is shared between
// them, see DownloadRate); anyone else waits in line and is let in first come, first served as a place frees up.
// A player is whoever their OpenVerse says (X-OV-Download-Id, one per launch), or their address for older versions.
//
// A place is held while files keep being asked for; after Idle without a request it goes to the next in line (the
// player finished, closed the game, or lost the connection). Someone in line who stops asking is dropped after the
// same time. The line lives in this process: a restart empties it and players get back in line by asking again.
public sealed class DownloadQueue(BeOptions options, Func<DateTimeOffset>? clock = null)
{
    readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly object _lock = new();
    readonly Dictionary<string, DateTimeOffset> _active = new(StringComparer.Ordinal);
    readonly List<(string Id, DateTimeOffset Seen)> _waiting = [];

    public readonly record struct Decision(bool Go, int Position, int Active, int Waiting);

    int MaxActive => options.AssetMaxActive;
    TimeSpan Idle => options.AssetIdle;

    public Decision Enter(string id)
    {
        lock (_lock)
        {
            var now = _now();
            Prune(now);
            if (_active.ContainsKey(id))
            {
                _active[id] = now;
                return new Decision(true, 0, _active.Count, _waiting.Count);
            }
            var at = _waiting.FindIndex(w => w.Id == id);
            // a free place goes to whoever is first in line; someone new only gets it when nobody is waiting
            if (MaxActive <= 0 || (_active.Count < MaxActive && (at == 0 || (at < 0 && _waiting.Count == 0))))
            {
                if (at >= 0) _waiting.RemoveAt(at);
                _active[id] = now;
                return new Decision(true, 0, _active.Count, _waiting.Count);
            }
            if (at < 0)
            {
                _waiting.Add((id, now));
                at = _waiting.Count - 1;
            }
            else _waiting[at] = (id, now);
            return new Decision(false, at + 1, _active.Count, _waiting.Count);
        }
    }

    // a long transfer keeps its place while it runs
    public void Touch(string id)
    {
        lock (_lock)
            if (_active.ContainsKey(id)) _active[id] = _now();
    }

    public (int Active, int Waiting) Counts()
    {
        lock (_lock)
        {
            Prune(_now());
            return (_active.Count, _waiting.Count);
        }
    }

    void Prune(DateTimeOffset now)
    {
        foreach (var gone in _active.Where(a => now - a.Value > Idle).Select(a => a.Key).ToList())
            _active.Remove(gone);
        _waiting.RemoveAll(w => now - w.Seen > Idle);
    }
}

// One budget for all game-file bytes leaving this server, shared by whoever is downloading: one player alone gets all
// of it, two get about half each. Each chunk books the next free slice of time, so streams interleave in turn
public sealed class DownloadRate(BeOptions options)
{
    readonly object _lock = new();
    readonly double _bytesPerSecond = options.AssetRateMbps * 1_000_000 / 8;
    DateTimeOffset _next = DateTimeOffset.MinValue;

    public bool Unlimited => _bytesPerSecond <= 0;

    // how long to wait before sending this many bytes
    public TimeSpan Reserve(int bytes, DateTimeOffset now)
    {
        if (Unlimited) return TimeSpan.Zero;
        lock (_lock)
        {
            var start = _next > now ? _next : now;
            _next = start + TimeSpan.FromSeconds(bytes / _bytesPerSecond);
            return start - now;
        }
    }
}
