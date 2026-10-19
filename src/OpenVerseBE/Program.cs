using System.Text.Json;
using Microsoft.AspNetCore.HttpOverrides;
using OpenVerseBE;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

var options = BeOptions.FromEnvironment();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(new Keys(options.KeyPrefix));
// Connected once, at startup. Connecting lazily on the first request meant a burst of first requests all blocked on
// the synchronous connect and failed with ConnectTimeout. AbortOnConnectFail=false keeps retrying in the background
// instead of failing for good when Redis is briefly unreachable
var redisConfig = ConfigurationOptions.Parse(options.Redis);
redisConfig.AbortOnConnectFail = false;
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConfig));
builder.Services.AddSingleton<PlayerRepository>();
builder.Services.AddSingleton<RoomRepository>();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

// Caddy terminates TLS and forwards here. The defaults trust loopback only (Caddy on the same host); under docker
// compose Caddy is a container with a fixed address, given in OVBE_TRUSTED_PROXIES. Anyone else's X-Forwarded-For is
// ignored, so a client cannot spoof its address by talking to this port directly
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var proxy in options.TrustedProxies) o.KnownProxies.Add(proxy);
});

var app = builder.Build();
// resolve now, so the connection is made before the first request (the container still owns and disposes it)
app.Services.GetRequiredService<IConnectionMultiplexer>();
app.UseForwardedHeaders();
app.MapBeEndpoints();

Console.WriteLine($"OpenVerseBE: redis={options.Redis.Split(',')[0]} prefix={options.KeyPrefix} node={options.NodeServerUrl} " +
                  $"cafe_key={(options.CafeKey.Length > 0 ? "on" : "off")} " +
                  $"max_rooms={(options.MaxRooms > 0 ? options.MaxRooms.ToString() : "unlimited")} " +
                  $"trusted_proxies={(options.TrustedProxies.Length > 0 ? string.Join(",", options.TrustedProxies.Select(p => p.ToString())) : "loopback")}");
app.Run();

public partial class Program;
