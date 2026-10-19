using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace OpenVerseBE.Tests;

// A server with its own key prefix and room limit, so each test counts only its own rooms
public sealed class LimitedServer : WebApplicationFactory<Program>
{
    public readonly string Prefix = $"ovtest:{Guid.NewGuid():N}:";
    public static readonly string Redis = Environment.GetEnvironmentVariable("OVBE_TEST_REDIS") ?? "127.0.0.1:6379";
    readonly int _max;

    public LimitedServer(int maxRooms)
    {
        _max = maxRooms;
        // shared with BeFixture and identical there; the prefix and the limit are set per server below
        Environment.SetEnvironmentVariable("OVBE_REDIS", Redis);
        Environment.SetEnvironmentVariable("OVBE_NODE_URL", "battle.example:3001");
        Environment.SetEnvironmentVariable("OVBE_CAFE_KEY", "cafe-secret");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(s =>
        {
            s.AddSingleton(new BeOptions
            {
                Redis = Redis, KeyPrefix = Prefix, NodeServerUrl = "battle.example:3001", CafeKey = "cafe-secret",
                MaxRooms = _max,
            });
            s.AddSingleton(new Keys(Prefix));
        });

    public async Task<HttpClient> Player()
    {
        var c = CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/players/register")
        { Content = JsonContent.Create(new { udid = Guid.NewGuid().ToString("N"), name = "p" }) };
        req.Headers.Add("X-OV-Cafe-Key", "cafe-secret");
        var res = await c.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"register {(int)res.StatusCode}: {text}");
        var body = JsonDocument.Parse(text).RootElement;
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        return c;
    }

    public static readonly object Settings = new
    {
        battle_type = 1, battle_rule = 1, deck_format = 1, two_pick_type = 0,
        can_friend_watch = false, can_guild_watch = false,
    };

    public override async ValueTask DisposeAsync()
    {
        try
        {
            using var mux = await ConnectionMultiplexer.ConnectAsync(Redis);
            var db = mux.GetDatabase();
            foreach (var ep in mux.GetEndPoints())
                foreach (var k in mux.GetServer(ep).Keys(pattern: Prefix + "*"))
                    await db.KeyDeleteAsync(k);
        }
        catch { }
        await base.DisposeAsync();
    }
}

public class RoomLimitTests
{
    static async Task<HttpStatusCode> Create(HttpClient c) => (await c.PostAsJsonAsync("/v1/rooms", LimitedServer.Settings)).StatusCode;

    [Fact]
    public async Task TheRoomPastTheLimitIsRefused()
    {
        await using var s = new LimitedServer(3);
        var players = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => s.Player()));
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, await Create(players[i]));

        var res = await players[3].PostAsJsonAsync("/v1/rooms", LimitedServer.Settings);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Equal("room_limit", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    // the client opens a fresh room every time it enters the room screen; that must not count against its own limit
    [Fact]
    public async Task ReopeningYourOwnRoomDoesNotCountTwice()
    {
        await using var s = new LimitedServer(2);
        var a = await s.Player();
        var b = await s.Player();
        Assert.Equal(HttpStatusCode.OK, await Create(a));
        Assert.Equal(HttpStatusCode.OK, await Create(b));
        Assert.Equal(HttpStatusCode.OK, await Create(a));
        Assert.Equal(HttpStatusCode.OK, await Create(a));
    }

    [Fact]
    public async Task ClosingARoomFreesItsSlot()
    {
        await using var s = new LimitedServer(1);
        var a = await s.Player();
        var b = await s.Player();
        Assert.Equal(HttpStatusCode.OK, await Create(a));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await Create(b));

        await a.DeleteAsync("/v1/rooms/mine");
        Assert.Equal(HttpStatusCode.OK, await Create(b));
    }

    // a room that times out leaves its id behind in the index; it must not hold a slot forever
    [Fact]
    public async Task AnExpiredRoomFreesItsSlot()
    {
        await using var s = new LimitedServer(1);
        var a = await s.Player();
        var b = await s.Player();
        var roomId = (await (await a.PostAsJsonAsync("/v1/rooms", LimitedServer.Settings)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("room_id").GetString();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await Create(b));

        using var mux = await ConnectionMultiplexer.ConnectAsync(LimitedServer.Redis);
        await mux.GetDatabase().KeyDeleteAsync($"{s.Prefix}room:{roomId}");   // what the TTL does
        Assert.Equal(HttpStatusCode.OK, await Create(b));
    }

    [Fact]
    public async Task SimultaneousCreatesStopExactlyAtTheLimit()
    {
        await using var s = new LimitedServer(10);
        var players = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => s.Player()));
        var codes = await Task.WhenAll(players.Select(Create));
        Assert.Equal(10, codes.Count(c => c == HttpStatusCode.OK));
        Assert.Equal(10, codes.Count(c => c == HttpStatusCode.ServiceUnavailable));
    }

    [Fact]
    public async Task ZeroMeansNoLimit()
    {
        await using var s = new LimitedServer(0);
        var players = await Task.WhenAll(Enumerable.Range(0, 15).Select(_ => s.Player()));
        var codes = await Task.WhenAll(players.Select(Create));
        Assert.All(codes, c => Assert.Equal(HttpStatusCode.OK, c));
    }

    [Fact]
    public async Task StatusReportsOpenRoomsAndTheLimit()
    {
        await using var s = new LimitedServer(10);
        var a = await s.Player();
        var b = await s.Player();
        await Create(a);
        await Create(b);
        await a.DeleteAsync("/v1/rooms/mine");

        var st = await s.CreateClient().GetFromJsonAsync<JsonElement>("/v1/status");
        Assert.Equal(1, st.GetProperty("open_rooms").GetInt32());
        Assert.Equal(10, st.GetProperty("max_rooms").GetInt32());
    }

    static BeOptions Parse(string? maxRooms) =>
        BeOptions.From(k => k == "OVBE_MAX_ROOMS" ? maxRooms : null);

    [Theory]
    [InlineData("10", 10)]
    [InlineData("0", 0)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void TheLimitComesFromTheEnvironment(string? value, int expected) =>
        Assert.Equal(expected, Parse(value).MaxRooms);

    [Theory]
    [InlineData("ten")]
    [InlineData("-1")]
    public void ANonsenseLimitStopsTheServerFromStarting(string value) =>
        Assert.Throws<InvalidOperationException>(() => Parse(value));
}
