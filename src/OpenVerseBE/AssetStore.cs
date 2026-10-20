using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OpenVerseBE;

public enum AssetState { NotConfigured, Indexing, Ready, Failed }

// A copy of one player's Shadowverse data folder (%UserProfile%\AppData\LocalLow\Cygames\Shadowverse), served to the
// local Apis of players whose own copy is missing files. The client asks for a file by the hash its manifest lists,
// so the manifests are rewritten here to the hashes of the files this server really has, the same way
// OpenVerse.Api's ManifestIndex does it for a player's own copy.
public sealed class AssetStore(BeOptions options)
{
    // the bundle cache folders the client keeps (asset bundles, sound banks, voices, movies, fonts)
    static readonly string[] CacheDirs = ["a", "b", "s", "v", "m", "f"];

    // the client pre-fetches these before letting you past the title, so a row nobody can serve has to leave them
    static readonly HashSet<string> Predownload = new(StringComparer.Ordinal) { "common", "tutorial" };

    // an existing category that isn't pre-fetched, so unservable rows stay well-formed
    const string Deferred = "everytmp";

    const string SidecarName = "asset-index.csv";

    // until the indexer has looked, nothing is known: callers are asked to wait rather than told there is nothing
    volatile AssetState _state = AssetState.Indexing;
    volatile string _error = "";
    IReadOnlyDictionary<string, byte[]> _manifests = new Dictionary<string, byte[]>();
    IReadOnlyDictionary<string, string> _byMd5 = new Dictionary<string, string>();
    int _files;
    int _hashed;
    long _bytes;

    public AssetState State => _state;
    public string Error => _error;
    public int Files => _files;
    public int Hashed => _hashed;
    public long Bytes => _bytes;
    public IReadOnlyDictionary<string, byte[]> Manifests => _manifests;

    public string ManifestDir => Path.Combine(options.AssetDir, "manifest");

    public bool Configured => options.AssetDir.Length > 0 && Directory.Exists(ManifestDir);

    public string? FileFor(string md5) => _byMd5.TryGetValue(md5, out var path) && File.Exists(path) ? path : null;

    public string? CardMaster =>
        options.CardMasterPath.Length > 0 && File.Exists(options.CardMasterPath) ? options.CardMasterPath : null;

    // Hashing a full copy reads ~13 GB, so it runs once in the background and the sidecar in the state folder keeps
    // later starts to the files that changed. Requests are answered with 503 until it is done
    public void Build(Action<string>? log = null)
    {
        if (!Configured)
        {
            _state = AssetState.NotConfigured;
            log?.Invoke(options.AssetDir.Length == 0
                ? "Assets: OVBE_ASSET_DIR not set, serving no game files"
                : $"Assets: no manifest folder in {options.AssetDir}, serving no game files");
            return;
        }
        _state = AssetState.Indexing;
        try
        {
            var sidecar = options.StateDir.Length > 0 ? Path.Combine(options.StateDir, SidecarName) : "";
            var cached = LoadSidecar(sidecar);
            var files = CacheDirs
                .Select(sub => Path.Combine(options.AssetDir, sub))
                .Where(Directory.Exists)
                .SelectMany(Directory.EnumerateFiles)
                .ToList();
            log?.Invoke($"Assets: indexing {files.Count} files in {options.AssetDir}" +
                        (cached.Count > 0 ? $" ({cached.Count} known from the last run)" : " (first run, this takes a while)"));

            var found = new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            var hashed = 0;
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, path =>
            {
                FileInfo info;
                try { info = new FileInfo(path); }
                catch (IOException) { return; }
                var stamp = info.LastWriteTimeUtc.Ticks;
                if (cached.TryGetValue(info.Name, out var hit) && hit.Size == info.Length && hit.Stamp == stamp)
                {
                    found[info.Name] = hit with { Path = path };
                    return;
                }
                try
                {
                    using var fs = File.OpenRead(path);
                    found[info.Name] = new Entry(Convert.ToHexString(MD5.HashData(fs)).ToLowerInvariant(), info.Length, stamp, path);
                    Interlocked.Increment(ref hashed);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            });

            if (hashed > 0 && sidecar.Length > 0)
            {
                try { File.WriteAllLines(sidecar, found.Select(kv => $"{kv.Key},{kv.Value.Size},{kv.Value.Stamp},{kv.Value.Md5}")); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    log?.Invoke($"Assets: could not save the index ({e.Message}), the next start hashes everything again");
                }
            }

            var byName = new Dictionary<string, Entry>(found, StringComparer.OrdinalIgnoreCase);
            var manifests = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            int rewritten = 0, deferred = 0;
            foreach (var mf in Directory.EnumerateFiles(ManifestDir))
            {
                manifests[Path.GetFileName(mf)] = Rewrite(ReadManifest(mf), byName, out var r, out var d);
                rewritten += r;
                deferred += d;
            }

            var byMd5 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in byName.Values) byMd5[e.Md5] = e.Path;

            _manifests = manifests;
            _byMd5 = byMd5;
            _files = byName.Count;
            _hashed = hashed;
            _bytes = byName.Values.Sum(e => e.Size);
            _state = AssetState.Ready;
            log?.Invoke($"Assets: ready, {byName.Count} files ({_bytes / 1048576} MiB, {hashed} hashed now), " +
                        $"{manifests.Count} manifests, {rewritten} rows matched, {deferred} not on this server");
        }
        catch (Exception e)
        {
            _error = e.Message;
            _state = AssetState.Failed;
            log?.Invoke($"Assets: indexing failed: {e}");
        }
    }

    readonly record struct Entry(string Md5, long Size, long Stamp, string Path);

    static Dictionary<string, Entry> LoadSidecar(string path)
    {
        var d = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        if (path.Length == 0 || !File.Exists(path)) return d;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var p = line.Split(',');
                if (p.Length == 4 && long.TryParse(p[1], out var size) && long.TryParse(p[2], out var stamp))
                    d[p[0]] = new Entry(p[3], size, stamp, "");
            }
        }
        catch (IOException) { }
        return d;
    }

    // the client writes a BOM every time it saves a manifest and the parser only eats one
    public static byte[] ReadManifest(string file)
    {
        var b = File.ReadAllBytes(file);
        int i = 0;
        while (i + 3 <= b.Length && b[i] == 0xEF && b[i + 1] == 0xBB && b[i + 2] == 0xBF) i += 3;
        return i == 0 ? b : b[i..];
    }

    static string Mib(long bytes) => (bytes / 1048576.0).ToString("0.###", CultureInfo.InvariantCulture);

    // Name,hash,category,sizeMiB,hash,sizeMiB. Columns 1 and 4 are the normal and small-resource variants, and this
    // server only ever has one copy of a bundle
    static byte[] Rewrite(byte[] manifest, Dictionary<string, Entry> byName, out int rewritten, out int deferred)
    {
        rewritten = 0;
        deferred = 0;
        var outLines = new List<string>();
        foreach (var line in Encoding.UTF8.GetString(manifest).Split('\n'))
        {
            var p = line.Split(',');
            if (p.Length < 6) { outLines.Add(line); continue; }
            // audio rows carry the cache subdir the filename itself lacks
            var name = p[0];
            var slash = name.LastIndexOf('/');
            var leaf = (slash >= 0 ? name[(slash + 1)..] : name).Trim();
            if (byName.TryGetValue(leaf, out var e))
            {
                var mib = Mib(e.Size);
                outLines.Add($"{p[0]},{e.Md5},{p[2]},{mib},{e.Md5},{mib}");
                rewritten++;
            }
            else if (Predownload.Contains(p[2]))
            {
                outLines.Add($"{p[0]},{p[1]},{Deferred},{p[3]},{p[4]},{p[5]}");
                deferred++;
            }
            else outLines.Add(line);
        }
        return Encoding.UTF8.GetBytes(string.Join('\n', outLines));
    }
}

// Starts the index in the background so the server (rooms, registration) is up at once
public sealed class AssetIndexer(AssetStore store, ILogger<AssetIndexer> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Run(() => store.Build(m => logger.LogInformation("{Message}", m)), stoppingToken);
}
