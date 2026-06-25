using System.Net;
using AdminDiscordMcp.Config;
using AdminDiscordMcp.Discord;
using AdminDiscordMcp.Mcp;
using NReco.Logging.File;

var builder = WebApplication.CreateBuilder(args);

// No config file. The bot token/identity arrives per request via the MCP client's
// header — nothing is configured server-side. The only process-level knobs (bind
// address, port, idle TTL) have sane defaults and, if ever needed, are overridable
// via command-line args (e.g. --AdminDiscordMcp:Port=50000) or ADMCP_ env vars.
builder.Configuration.AddEnvironmentVariables(prefix: "ADMCP_");

var config = BotConfig.Load(builder.Configuration);

builder.Logging.AddFile("data/logs/admcp.log", o => o.Append = true);

// ---- Services ----
builder.Services.AddSingleton(config);
builder.Services.AddSingleton<AdminAuditLog>();
builder.Services.AddSingleton<DiscordClientPool>();
builder.Services.AddHttpContextAccessor();

// Per-request Discord identity, resolved from the request header.
builder.Services.AddScoped<IDiscordContext, DiscordContext>();

// MCP server: stateless HTTP transport at /mcp; tools auto-discovered from this
// assembly (any [McpServerToolType]). Tools are scoped so each call binds to the
// caller's token via IDiscordContext.
builder.Services
    .AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithToolsFromAssembly();

builder.WebHost.ConfigureKestrel(opts =>
{
    var ip = IPAddress.TryParse(config.BindAddress, out var parsed) ? parsed : IPAddress.Loopback;
    opts.Listen(ip, config.Port);
});

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new { ok = true, ts = DateTimeOffset.UtcNow }));
app.MapMcp("/mcp");

app.Logger.LogInformation(
    "Admin Discord MCP listening on {Bind}:{Port}/mcp — bot token is supplied per request via the '{Header}' header.",
    config.BindAddress, config.Port, DiscordContext.TokenHeader);

app.Run();
