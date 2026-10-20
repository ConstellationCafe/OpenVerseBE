using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace OpenVerseBE.Tests;

// A server over a throwaway copy of a Shadowverse data folder
public sealed class AssetServer : WebApplicationFactory<Program>
{
    readonly BeOptions _options;

    public AssetServer(string assetDir, string cardMaster = "", string stateDir = "", string cafeKey = "cafe-secret",
        double rateMbps = 0, int maxActive = 0)
    {
        Environment.SetEnvironmentVariable("OVBE_REDIS", LimitedServer.Redis);
        _options = new BeOptions
        {
            Redis = LimitedServer.Redis, KeyPrefix = $"ovtest:{Guid.NewGuid():N}:", NodeServerUrl = "battle.example:3001",
            CafeKey = cafeKey, AssetDir = assetDir, CardMasterPath = cardMaster, StateDir = stateDir,
            AssetRateMbps = rateMbps, AssetMaxActive = maxActive,
        };
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(s =>
        {
            s.AddSingleton(_options);
            s.AddSingleton(new Keys(_options.KeyPrefix));
        });

    public HttpClient Client(string? key = "cafe-secret")
    {
        var c = CreateClient();
        if (key is not null) c.DefaultRequestHeaders.Add("X-OV-Cafe-Key", key);
        return c;
    }

    public async Task<JsonElement> WaitReady(HttpClient c)
    {
        for (var i = 0; i < 200; i++)
        {
            var status = JsonDocument.Parse(await c.GetStringAsync("/v1/assets/status")).RootElement;
            var state = status.GetProperty("state").GetString();
            if (state is "ready" or "not_configured" or "failed") return status;
            await Task.Delay(50);
        }
        throw new TimeoutException("asset index never finished");
    }
}

public sealed class AssetTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ovbe-assets-" + Guid.NewGuid().ToString("N"));
    string Assets => Path.Combine(_root, "shadowverse");
    string State => Path.Combine(_root, "state");

    static readonly byte[] CardBundle = Encoding.UTF8.GetBytes("card bundle bytes");
    static readonly byte[] Voice = Encoding.UTF8.GetBytes("a voice line");

    static string Md5(byte[] b) => Convert.ToHexString(MD5.HashData(b)).ToLowerInvariant();

    public AssetTests()
    {
        Directory.CreateDirectory(Path.Combine(Assets, "manifest"));
        Directory.CreateDirectory(Path.Combine(Assets, "a"));
        Directory.CreateDirectory(Path.Combine(Assets, "v"));
        Directory.CreateDirectory(State);
        File.WriteAllBytes(Path.Combine(Assets, "a", "card_1001"), CardBundle);
        File.WriteAllBytes(Path.Combine(Assets, "v", "vo_1001.acb"), Voice);
        // as the client saves it: a stale hash, a BOM (twice, as after two saves), a row this copy lacks in a
        // pre-fetched category and one in an ordinary category
        var manifest = "card_1001,00000000000000000000000000000000,card,1,00000000000000000000000000000000,1\n" +
                       "v/vo_1001.acb,11111111111111111111111111111111,sound,1,11111111111111111111111111111111,1\n" +
                       "missing_common,22222222222222222222222222222222,common,2,22222222222222222222222222222222,2\n" +
                       "missing_card,33333333333333333333333333333333,card,3,33333333333333333333333333333333,3";
        File.WriteAllBytes(Path.Combine(Assets, "manifest", "card_assetmanifest"),
            [0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(manifest)]);
        File.WriteAllText(Path.Combine(Assets, "manifest", "moviemanifest_kor"), "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public async Task Manifests_are_rewritten_to_the_files_this_server_has()
    {
        await using var server = new AssetServer(Assets, stateDir: State);
        var c = server.Client();
        var status = await server.WaitReady(c);
        Assert.Equal("ready", status.GetProperty("state").GetString());
        Assert.Equal(2, status.GetProperty("files").GetInt32());

        var list = JsonDocument.Parse(await c.GetStringAsync("/v1/assets/manifests")).RootElement.GetProperty("manifests");
        var names = list.EnumerateArray().Select(m => m.GetProperty("name").GetString()).ToList();
        Assert.Contains("card_assetmanifest", names);
        Assert.Contains("moviemanifest_kor", names);

        var bytes = await c.GetByteArrayAsync("/v1/assets/manifests/card_assetmanifest");
        var listed = list.EnumerateArray().Single(m => m.GetProperty("name").GetString() == "card_assetmanifest");
        Assert.Equal(Md5(bytes), listed.GetProperty("md5").GetString());
        Assert.Equal(bytes.Length, listed.GetProperty("size").GetInt32());
        Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..], "BOM left in");
        var rows = Encoding.UTF8.GetString(bytes).Split('\n');
        Assert.Equal($"card_1001,{Md5(CardBundle)},card,0,{Md5(CardBundle)},0", rows[0]);
        Assert.StartsWith($"v/vo_1001.acb,{Md5(Voice)},sound,", rows[1]);
        Assert.StartsWith("missing_common,22222222222222222222222222222222,everytmp,", rows[2]);
        Assert.StartsWith("missing_card,33333333333333333333333333333333,card,", rows[3]);

        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/v1/assets/manifests/nope_assetmanifest")).StatusCode);
    }

    [Fact]
    public async Task Files_are_served_by_hash()
    {
        await using var server = new AssetServer(Assets, stateDir: State);
        var c = server.Client();
        await server.WaitReady(c);

        Assert.Equal(CardBundle, await c.GetByteArrayAsync($"/v1/assets/files/{Md5(CardBundle)}"));
        Assert.Equal(Voice, await c.GetByteArrayAsync($"/v1/assets/files/{Md5(Voice).ToUpperInvariant()}"));
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/v1/assets/files/33333333333333333333333333333333")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/v1/assets/files/..%2F..%2Fetc%2Fpasswd")).StatusCode);
    }

    static HttpRequestMessage FileRequest(string md5, string downloadId)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/v1/assets/files/{md5}");
        req.Headers.Add("X-OV-Download-Id", downloadId);
        return req;
    }

    [Fact]
    public async Task A_third_downloader_waits_in_line()
    {
        await using var server = new AssetServer(Assets, stateDir: State, maxActive: 2);
        var c = server.Client();
        await server.WaitReady(c);
        var md5 = Md5(CardBundle);

        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(FileRequest(md5, "pc-a"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(FileRequest(md5, "pc-b"))).StatusCode);

        var third = await c.SendAsync(FileRequest(md5, "pc-c"));
        Assert.Equal((HttpStatusCode)429, third.StatusCode);
        Assert.Equal("5", third.Headers.RetryAfter?.ToString());
        var body = JsonDocument.Parse(await third.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("queued", body.GetProperty("error").GetString());
        Assert.Equal(1, body.GetProperty("position").GetInt32());
        Assert.Equal(2, body.GetProperty("active").GetInt32());

        // the two inside keep downloading; manifests and the card master never queue
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(FileRequest(md5, "pc-a"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/v1/assets/manifests")).StatusCode);

        var queue = JsonDocument.Parse(await c.GetStringAsync("/v1/assets/queue")).RootElement;
        Assert.Equal(2, queue.GetProperty("active").GetInt32());
        Assert.Equal(1, queue.GetProperty("waiting").GetInt32());
    }

    [Fact]
    public async Task Downloads_keep_to_the_bandwidth_budget()
    {
        var big = new byte[256 * 1024];
        Random.Shared.NextBytes(big);
        File.WriteAllBytes(Path.Combine(Assets, "a", "big_bundle"), big);
        File.AppendAllText(Path.Combine(Assets, "manifest", "card_assetmanifest"),
            "\nbig_bundle,44444444444444444444444444444444,card,1,44444444444444444444444444444444,1");

        // 2 Mbit/s = 250 KB/s
        await using var server = new AssetServer(Assets, stateDir: State, rateMbps: 2);
        var c = server.Client();
        await server.WaitReady(c);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var got = await c.GetByteArrayAsync($"/v1/assets/files/{Md5(big)}");
        sw.Stop();
        Assert.Equal(big, got);
        // four 64 KiB chunks: the first goes at once, the other three wait their slice (3 x 65536 / 250000 = 0.79 s)
        Assert.InRange(sw.Elapsed.TotalSeconds, 0.75, 5);
    }

    [Fact]
    public async Task The_index_is_kept_between_starts()
    {
        await using (var first = new AssetServer(Assets, stateDir: State))
        {
            var status = await first.WaitReady(first.Client());
            Assert.Equal(2, status.GetProperty("hashed_this_start").GetInt32());
        }
        Assert.True(File.Exists(Path.Combine(State, "asset-index.csv")));

        await using var second = new AssetServer(Assets, stateDir: State);
        var c = second.Client();
        var again = await second.WaitReady(c);
        Assert.Equal(0, again.GetProperty("hashed_this_start").GetInt32());
        Assert.Equal(CardBundle, await c.GetByteArrayAsync($"/v1/assets/files/{Md5(CardBundle)}"));
    }

    [Fact]
    public async Task The_cafe_key_is_required()
    {
        await using var server = new AssetServer(Assets, stateDir: State);
        await server.WaitReady(server.Client());
        var anon = server.Client(key: null);
        Assert.Equal(HttpStatusCode.Forbidden, (await anon.GetAsync("/v1/assets/manifests")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anon.GetAsync($"/v1/assets/files/{Md5(CardBundle)}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anon.GetAsync("/v1/card-master")).StatusCode);
        var wrong = server.Client(key: "nope");
        Assert.Equal(HttpStatusCode.Forbidden, (await wrong.GetAsync("/v1/assets/status")).StatusCode);
    }

    [Fact]
    public async Task No_asset_folder_means_no_game_files()
    {
        await using var server = new AssetServer(Path.Combine(_root, "not-there"));
        var c = server.Client();
        var status = await server.WaitReady(c);
        Assert.Equal("not_configured", status.GetProperty("state").GetString());
        var res = await c.GetAsync("/v1/assets/manifests");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Contains("assets_not_configured", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_card_master_is_served_when_present()
    {
        var gz = Path.Combine(_root, "card_master_full.csv.gz");
        await using (var fs = File.Create(gz))
        await using (var z = new GZipStream(fs, CompressionMode.Compress))
            z.Write(Encoding.UTF8.GetBytes("card_id,name\n100011010,x\n"));

        await using var server = new AssetServer(Assets, cardMaster: gz, stateDir: State);
        var c = server.Client();
        var res = await c.GetAsync("/v1/card-master");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(await File.ReadAllBytesAsync(gz), await res.Content.ReadAsByteArrayAsync());

        await using var none = new AssetServer(Assets, cardMaster: Path.Combine(_root, "missing.gz"), stateDir: State);
        var r2 = await none.Client().GetAsync("/v1/card-master");
        Assert.Equal(HttpStatusCode.NotFound, r2.StatusCode);
    }
}
