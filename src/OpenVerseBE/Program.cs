using System.Text.Json;
using Microsoft.AspNetCore.HttpOverrides;
using OpenVerseBE;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

var options = BeOptions.FromEnvironment();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(new Keys(options.KeyPrefix));
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(options.Redis));
builder.Services.AddSingleton<PlayerRepository>();
builder.Services.AddSingleton<RoomRepository>();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

// Caddy terminates TLS on the same machine and forwards over loopback. The defaults already trust loopback only, so a
// client cannot spoof X-Forwarded-For when it talks to this port directly
builder.Services.Configure<ForwardedHeadersOptions>(o =>
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

var app = builder.Build();
app.UseForwardedHeaders();
app.MapBeEndpoints();

Console.WriteLine($"OpenVerseBE: redis={options.Redis.Split(',')[0]} prefix={options.KeyPrefix} node={options.NodeServerUrl} " +
                  $"cafe_key={(options.CafeKey.Length > 0 ? "on" : "off")}");
app.Run();

public partial class Program;
