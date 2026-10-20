using System.Net;
using StackExchange.Redis;

namespace OpenVerseBE;

public static class Endpoints
{
    const string PlayerItem = "ov.player";

    public static void MapBeEndpoints(this WebApplication app)
    {
        app.MapGet("/healthz", async (IConnectionMultiplexer redis) =>
        {
            await redis.GetDatabase().PingAsync();
            return Results.Text("ok");
        });

        // how full the server is, for whoever runs it (no player data)
        app.MapGet("/v1/status", async (RoomRepository repo, BeOptions options) =>
            Results.Ok(new { open_rooms = await repo.OpenCount(), max_rooms = options.MaxRooms }));

        app.MapPost("/v1/players/register", async (RegisterRequest req, PlayerRepository players) =>
        {
            if (string.IsNullOrWhiteSpace(req.Udid))
                return Results.BadRequest(new ErrorResponse("udid_required"));
            var name = string.IsNullOrWhiteSpace(req.Name) ? "player_" + req.Udid[^Math.Min(6, req.Udid.Length)..] : req.Name.Trim();
            if (name.Length > 24) name = name[..24];
            var (player, token) = await players.Register(req.Udid, name);
            return Results.Ok(new RegisterResponse(player.ViewerId, token));
        }).AddEndpointFilter(RequireCafeKey);

        var rooms = app.MapGroup("/v1/rooms").AddEndpointFilter(RequirePlayer);

        rooms.MapPost("", async (HttpContext ctx, CreateRoomRequest req, RoomRepository repo) =>
        {
            var me = Me(ctx);
            try
            {
                var room = await repo.Create(me, req);
                return Results.Ok(room.ToResponse(me.Udid));
            }
            catch (RoomLimitReachedException)
            {
                // the caller's own previous room was closed before counting, so the limit is everyone else's rooms
                return Results.Json(new ErrorResponse("room_limit"), statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        rooms.MapGet("/mine", async (HttpContext ctx, RoomRepository repo) =>
        {
            var me = Me(ctx);
            return await repo.Mine(me.Udid) is { } mine
                ? Results.Ok(mine.Room.ToResponse(me.Udid))
                : Results.NotFound(new ErrorResponse("no_room"));
        });

        rooms.MapDelete("/mine", async (HttpContext ctx, RoomRepository repo) =>
        {
            await repo.CloseOwnedBy(Me(ctx).Udid);
            return Results.NoContent();
        });

        rooms.MapPut("/mine/deck", async (HttpContext ctx, DeckRequest req, RoomRepository repo) =>
        {
            var ok = await repo.SaveDeck(Me(ctx).Udid, req, SourceIp(ctx));
            return ok ? Results.NoContent() : Results.NotFound(new ErrorResponse("no_room"));
        });

        rooms.MapGet("/{roomId}", async (HttpContext ctx, string roomId, RoomRepository repo) =>
            await repo.Get(roomId) is { } room
                ? Results.Ok(room.ToResponse(Me(ctx).Udid))
                : Results.NotFound(new ErrorResponse("room_not_found")));

        rooms.MapPost("/{roomId}/enter", async (HttpContext ctx, string roomId, RoomRepository repo) =>
        {
            var me = Me(ctx);
            var (result, room) = await repo.Enter(me, roomId);
            return result switch
            {
                EnterResult.Ok => Results.Ok(room!.ToResponse(me.Udid)),
                EnterResult.NotFound => Results.NotFound(new ErrorResponse("room_not_found")),
                EnterResult.Full => Results.Conflict(new ErrorResponse("room_full")),
                _ => Results.Conflict(new ErrorResponse("own_room")),
            };
        });

        rooms.MapPost("/{roomId}/leave", async (string roomId, RoomRepository repo) =>
        {
            await repo.Leave(roomId);
            return Results.NoContent();
        });

        MapAssetEndpoints(app);
    }

    // Game files and the card master, for players whose own Shadowverse folder lacks them. Their local Api asks for
    // what it is missing; nginx in front limits the rate (deploy/nginx). Same cafe key as registration
    static void MapAssetEndpoints(WebApplication app)
    {
        app.MapGet("/v1/card-master", (AssetStore store) =>
                store.CardMaster is { } path
                    ? Results.File(path, "application/gzip", "card_master_full.csv.gz")
                    : Results.NotFound(new ErrorResponse("card_master_not_configured")))
            .AddEndpointFilter(RequireCafeKey);

        var assets = app.MapGroup("/v1/assets").AddEndpointFilter(RequireCafeKey);

        assets.MapGet("/status", (AssetStore store) => Results.Ok(new
        {
            state = store.State switch
            {
                AssetState.Ready => "ready",
                AssetState.Indexing => "indexing",
                AssetState.Failed => "failed",
                _ => "not_configured",
            },
            files = store.Files,
            mib = store.Bytes / 1048576,
            manifests = store.Manifests.Count,
            hashed_this_start = store.Hashed,
            error = store.Error.Length > 0 ? store.Error : null,
        }));

        assets.MapGet("/manifests", (AssetStore store) => Results.Ok(new
        {
            manifests = store.Manifests
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new
                {
                    name = kv.Key,
                    size = kv.Value.Length,
                    // lets a player's OpenVerse keep its copy and fetch only the manifests that changed
                    md5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(kv.Value)).ToLowerInvariant(),
                }),
        })).AddEndpointFilter(RequireAssetsReady);

        assets.MapGet("/manifests/{name}", (string name, AssetStore store) =>
            store.Manifests.TryGetValue(name, out var bytes)
                ? Results.Bytes(bytes, "application/octet-stream")
                : Results.NotFound(new ErrorResponse("manifest_not_found"))).AddEndpointFilter(RequireAssetsReady);

        assets.MapGet("/files/{md5}", async (HttpContext ctx, string md5, AssetStore store, DownloadQueue queue,
            DownloadRate rate, BeOptions options) =>
        {
            if (md5.Length != 32 || !md5.All(Uri.IsHexDigit) || store.FileFor(md5) is not { } path)
                return Results.NotFound(new ErrorResponse("file_not_found"));

            var who = ctx.Request.Headers["X-OV-Download-Id"].ToString() is { Length: > 0 and <= 64 } id ? "id:" + id : "ip:" + SourceIp(ctx);
            var turn = queue.Enter(who);
            if (!turn.Go)
            {
                ctx.Response.Headers.RetryAfter = "5";
                return Results.Json(new QueuedResponse("queued", turn.Position, turn.Active, turn.Waiting,
                    options.AssetMaxActive), statusCode: StatusCodes.Status429TooManyRequests);
            }

            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.ContentLength = file.Length;
            var buffer = new byte[1 << 16];
            var ct = ctx.RequestAborted;
            int n;
            while ((n = await file.ReadAsync(buffer, ct)) > 0)
            {
                var wait = rate.Reserve(n, DateTimeOffset.UtcNow);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, n), ct);
                queue.Touch(who);
            }
            return Results.Empty;
        }).AddEndpointFilter(RequireAssetsReady);

        assets.MapGet("/queue", (DownloadQueue queue, BeOptions options) =>
        {
            var (active, waiting) = queue.Counts();
            return Results.Ok(new
            {
                active, waiting, max_active = options.AssetMaxActive, rate_mbps = options.AssetRateMbps,
                idle_seconds = (int)options.AssetIdle.TotalSeconds,
            });
        });
    }

    static async ValueTask<object?> RequireCafeKey(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var options = ctx.HttpContext.RequestServices.GetRequiredService<BeOptions>();
        if (options.CafeKey.Length > 0 && ctx.HttpContext.Request.Headers["X-OV-Cafe-Key"].ToString() != options.CafeKey)
            return Results.Json(new ErrorResponse("bad_cafe_key"), statusCode: StatusCodes.Status403Forbidden);
        return await next(ctx);
    }

    static async ValueTask<object?> RequireAssetsReady(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var store = ctx.HttpContext.RequestServices.GetRequiredService<AssetStore>();
        return store.State switch
        {
            AssetState.Ready => await next(ctx),
            AssetState.Indexing => Results.Json(new ErrorResponse("assets_indexing"), statusCode: StatusCodes.Status503ServiceUnavailable),
            AssetState.Failed => Results.Json(new ErrorResponse("assets_failed"), statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => Results.NotFound(new ErrorResponse("assets_not_configured")),
        };
    }

    static async ValueTask<object?> RequirePlayer(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var auth = http.Request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        if (!auth.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
            return Results.Json(new ErrorResponse("token_required"), statusCode: StatusCodes.Status401Unauthorized);
        var players = http.RequestServices.GetRequiredService<PlayerRepository>();
        var player = await players.ByToken(auth[bearer.Length..].Trim());
        if (player is null)
            return Results.Json(new ErrorResponse("bad_token"), statusCode: StatusCodes.Status401Unauthorized);
        http.Items[PlayerItem] = player;
        return await next(ctx);
    }

    static Player Me(HttpContext ctx) => (Player)ctx.Items[PlayerItem]!;

    // The battle server sees sockets as plain IPv4 when it listens on 0.0.0.0, so an IPv4 address that reached this
    // server through a dual-stack socket has to be unwrapped or the two never compare equal
    public static string SourceIp(HttpContext ctx) => Normalize(ctx.Connection.RemoteIpAddress);

    public static string Normalize(IPAddress? ip) =>
        ip is null ? "" : (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
}
