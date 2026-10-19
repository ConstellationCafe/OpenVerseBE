using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using StackExchange.Redis;

namespace OpenVerseBE.Tests;

// Runs against a real Redis (OVBE_TEST_REDIS, default 127.0.0.1:6379) under a throwaway key prefix, because the enter
// path is a Lua script and an in-memory fake would not exercise it
public sealed class BeFixture : WebApplicationFactory<Program>
{
    public readonly string Prefix = $"ovtest:{Guid.NewGuid():N}:";
    public readonly string Redis = Environment.GetEnvironmentVariable("OVBE_TEST_REDIS") ?? "127.0.0.1:6379";

    public BeFixture()
    {
        Environment.SetEnvironmentVariable("OVBE_REDIS", Redis);
        Environment.SetEnvironmentVariable("OVBE_KEY_PREFIX", Prefix);
        Environment.SetEnvironmentVariable("OVBE_NODE_URL", "battle.example:3001");
        Environment.SetEnvironmentVariable("OVBE_CAFE_KEY", "cafe-secret");
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            using var mux = await ConnectionMultiplexer.ConnectAsync(Redis + ",allowAdmin=true");
            var db = mux.GetDatabase();
            foreach (var ep in mux.GetEndPoints())
                foreach (var k in mux.GetServer(ep).Keys(pattern: Prefix + "*"))
                    await db.KeyDeleteAsync(k);
        }
        catch { }
        await base.DisposeAsync();
    }
}

public class RoomFlowTests(BeFixture f) : IClassFixture<BeFixture>
{
    static readonly object Settings = new
    {
        battle_type = 1, battle_rule = 1, deck_format = 1, two_pick_type = 0,
        can_friend_watch = false, can_guild_watch = false,
    };

    async Task<(HttpClient Client, long ViewerId)> Player(string name)
    {
        var c = f.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/players/register")
        {
            Content = JsonContent.Create(new { udid = Guid.NewGuid().ToString("N"), name }),
        };
        req.Headers.Add("X-OV-Cafe-Key", "cafe-secret");
        var res = await c.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        return (c, body.GetProperty("viewer_id").GetInt64());
    }

    static async Task<JsonElement> Json200(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task RegisterNeedsTheCafeKey()
    {
        var res = await f.CreateClient().PostAsJsonAsync("/v1/players/register", new { udid = "x", name = "n" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task RoomsNeedAToken()
    {
        var res = await f.CreateClient().PostAsJsonAsync("/v1/rooms", Settings);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task ViewerIdsAreDistinctAndStableAcrossReRegistration()
    {
        var (_, a) = await Player("a");
        var (_, b) = await Player("b");
        Assert.NotEqual(a, b);
        Assert.True(a > 100000 && b > 100000);

        var udid = Guid.NewGuid().ToString("N");
        async Task<long> Reg()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/v1/players/register")
            { Content = JsonContent.Create(new { udid, name = "same" }) };
            req.Headers.Add("X-OV-Cafe-Key", "cafe-secret");
            var body = await Json200(await f.CreateClient().SendAsync(req));
            return body.GetProperty("viewer_id").GetInt64();
        }
        Assert.Equal(await Reg(), await Reg());
    }

    [Fact]
    public async Task CreateEnterAndBothSeesTheRoom()
    {
        var (owner, ownerVid) = await Player("owner");
        var (visitor, visitorVid) = await Player("visitor");

        var created = await Json200(await owner.PostAsJsonAsync("/v1/rooms", Settings));
        var roomId = created.GetProperty("room_id").GetString()!;
        Assert.Matches(@"^\d{5}$", roomId);
        Assert.Equal("battle.example:3001", created.GetProperty("node_server_url").GetString());
        Assert.True(created.GetProperty("is_owner").GetBoolean());

        var entered = await Json200(await visitor.PostAsync($"/v1/rooms/{roomId}/enter", null));
        Assert.False(entered.GetProperty("is_owner").GetBoolean());
        Assert.Equal(ownerVid, entered.GetProperty("owner").GetProperty("viewer_id").GetInt64());
        Assert.Equal("owner", entered.GetProperty("owner").GetProperty("name").GetString());
        Assert.Equal(created.GetProperty("battle_id").GetString(), entered.GetProperty("battle_id").GetString());

        var ownerMine = await Json200(await owner.GetAsync("/v1/rooms/mine"));
        Assert.True(ownerMine.GetProperty("is_owner").GetBoolean());
        Assert.Equal(visitorVid, ownerMine.GetProperty("visitor").GetProperty("viewer_id").GetInt64());

        var visitorMine = await Json200(await visitor.GetAsync("/v1/rooms/mine"));
        Assert.False(visitorMine.GetProperty("is_owner").GetBoolean());
        Assert.Equal(roomId, visitorMine.GetProperty("room_id").GetString());
    }

    [Fact]
    public async Task EnterMissingRoomIs404()
    {
        var (p, _) = await Player("p");
        var res = await p.PostAsync("/v1/rooms/00000/enter", null);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task ASecondVisitorIsTurnedAway()
    {
        var (owner, _) = await Player("owner");
        var (v1, _) = await Player("v1");
        var (v2, _) = await Player("v2");
        var roomId = (await Json200(await owner.PostAsJsonAsync("/v1/rooms", Settings))).GetProperty("room_id").GetString();

        Assert.Equal(HttpStatusCode.OK, (await v1.PostAsync($"/v1/rooms/{roomId}/enter", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await v2.PostAsync($"/v1/rooms/{roomId}/enter", null)).StatusCode);
        // the same visitor entering again is fine (the client retries)
        Assert.Equal(HttpStatusCode.OK, (await v1.PostAsync($"/v1/rooms/{roomId}/enter", null)).StatusCode);
    }

    [Fact]
    public async Task ConcurrentVisitorsGetExactlyOneSeat()
    {
        var (owner, _) = await Player("owner");
        var roomId = (await Json200(await owner.PostAsJsonAsync("/v1/rooms", Settings))).GetProperty("room_id").GetString();
        var visitors = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Player("v" + i)));

        var codes = await Task.WhenAll(visitors.Select(v => v.Client.PostAsync($"/v1/rooms/{roomId}/enter", null)));
        Assert.Equal(1, codes.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(7, codes.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task OwnerCannotEnterOwnRoom()
    {
        var (owner, _) = await Player("owner");
        var roomId = (await Json200(await owner.PostAsJsonAsync("/v1/rooms", Settings))).GetProperty("room_id").GetString();
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync($"/v1/rooms/{roomId}/enter", null)).StatusCode);
    }

    [Fact]
    public async Task LeaveFreesTheSeatAndCloseRemovesTheRoom()
    {
        var (owner, _) = await Player("owner");
        var (v1, _) = await Player("v1");
        var (v2, _) = await Player("v2");
        var roomId = (await Json200(await owner.PostAsJsonAsync("/v1/rooms", Settings))).GetProperty("room_id").GetString();

        await v1.PostAsync($"/v1/rooms/{roomId}/enter", null);
        Assert.Equal(HttpStatusCode.NoContent, (await v1.PostAsync($"/v1/rooms/{roomId}/leave", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await v1.GetAsync("/v1/rooms/mine")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await v2.PostAsync($"/v1/rooms/{roomId}/enter", null)).StatusCode);

        // a visitor closing is a no-op; the owner closing removes it
        await v2.DeleteAsync("/v1/rooms/mine");
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/v1/rooms/{roomId}")).StatusCode);
        await owner.DeleteAsync("/v1/rooms/mine");
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/v1/rooms/{roomId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await v2.GetAsync("/v1/rooms/mine")).StatusCode);
    }

    [Fact]
    public async Task CreatingAgainAbandonsThePreviousRoom()
    {
        var (owner, _) = await Player("owner");
        var first = (await Json200(await owner.PostAsJsonAsync("/v1/rooms", Settings))).GetProperty("room_id").GetString();
        var second = (await Json200(await owner.PostAsJsonAsync("/v1/rooms", Settings))).GetProperty("room_id").GetString();
        Assert.NotEqual(first, second);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/v1/rooms/{first}")).StatusCode);
    }

    // The deck lands where OpenVerse.Battle reads it, with the seat and source address stamped by the server
    [Fact]
    public async Task DeckIsStoredUnderTheCallersSeat()
    {
        var (owner, _) = await Player("owner");
        var (visitor, _) = await Player("visitor");
        var roomId = (await Json200(await owner.PostAsJsonAsync("/v1/rooms", Settings))).GetProperty("room_id").GetString()!;
        await visitor.PostAsync($"/v1/rooms/{roomId}/enter", null);

        var deck = new
        {
            class_id = 3, sub_class_id = 10, chara_id = 3, sleeve_id = 3000011L, leader_skin_id = 0,
            card_ids = new[] { 100311010, 100311020 }, user_name = "visitor",
        };
        Assert.Equal(HttpStatusCode.NoContent, (await visitor.PutAsJsonAsync("/v1/rooms/mine/deck", deck)).StatusCode);

        using var mux = await ConnectionMultiplexer.ConnectAsync(f.Redis);
        var raw = await mux.GetDatabase().StringGetAsync($"{f.Prefix}bdeck:{roomId}:visitor");
        Assert.False(raw.IsNullOrEmpty);
        var stored = JsonDocument.Parse(raw.ToString()).RootElement;
        Assert.Equal(roomId, stored.GetProperty("room_id").GetString());
        Assert.False(stored.GetProperty("is_owner").GetBoolean());
        Assert.Equal(3, stored.GetProperty("class_id").GetInt32());
        Assert.Equal(2, stored.GetProperty("card_ids").GetArrayLength());
        Assert.Equal("visitor", stored.GetProperty("user_name").GetString());
        Assert.True(stored.TryGetProperty("source_ip", out _));
        Assert.True((await mux.GetDatabase().StringGetAsync($"{f.Prefix}bdeck:{roomId}:owner")).IsNull);
    }

    [Fact]
    public async Task DeckWithoutARoomIs404()
    {
        var (p, _) = await Player("p");
        var res = await p.PutAsJsonAsync("/v1/rooms/mine/deck", new
        {
            class_id = 1, sub_class_id = 10, chara_id = 1, sleeve_id = 3000011L, leader_skin_id = 0,
            card_ids = Array.Empty<int>(), user_name = "p",
        });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task RoomResponsesAreSnakeCase()
    {
        var (owner, _) = await Player("owner");
        var text = await (await owner.PostAsJsonAsync("/v1/rooms", Settings)).Content.ReadAsStringAsync();
        Assert.Contains("\"room_id\"", text);
        Assert.Contains("\"node_server_url\"", text);
        Assert.DoesNotContain("\"RoomId\"", text);
    }
}
