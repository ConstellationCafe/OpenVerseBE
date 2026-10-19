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

        app.MapPost("/v1/players/register", async (HttpContext ctx, RegisterRequest req, PlayerRepository players, BeOptions options) =>
        {
            if (options.CafeKey.Length > 0 && ctx.Request.Headers["X-OV-Cafe-Key"].ToString() != options.CafeKey)
                return Results.Json(new ErrorResponse("bad_cafe_key"), statusCode: StatusCodes.Status403Forbidden);
            if (string.IsNullOrWhiteSpace(req.Udid))
                return Results.BadRequest(new ErrorResponse("udid_required"));
            var name = string.IsNullOrWhiteSpace(req.Name) ? "player_" + req.Udid[^Math.Min(6, req.Udid.Length)..] : req.Name.Trim();
            if (name.Length > 24) name = name[..24];
            var (player, token) = await players.Register(req.Udid, name);
            return Results.Ok(new RegisterResponse(player.ViewerId, token));
        });

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
