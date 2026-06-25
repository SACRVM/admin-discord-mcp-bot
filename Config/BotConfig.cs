using Microsoft.Extensions.Configuration;

namespace AdminDiscordMcp.Config;

/// <summary>
/// Process-level defaults. Replaces the blueprint's checked-in <c>Const.cs</c>.
/// There is NO config file: every value has a sane default and is overridable only
/// via command-line args (e.g. <c>--AdminDiscordMcp:Port=50000</c>) or <c>ADMCP_</c>
/// environment variables. The bot token/identity is NOT here — it arrives per
/// request via the <c>X-Discord-Bot-Token</c> header from the MCP client config.
///
/// Note what is deliberately ABSENT: no admin/access token, no IP allowlist, no
/// unlock gate. The security model is "localhost-only + run on demand": each user
/// self-hosts their own instance and supplies their own Discord bot token per
/// request. The only real safety layer is <c>dryRun</c>-by-default on writes.
/// </summary>
public sealed class BotConfig
{
    private const string Section = "AdminDiscordMcp";

    /// <summary>Interface Kestrel binds to. Default localhost — the whole point.</summary>
    public string BindAddress { get; init; } = "127.0.0.1";

    /// <summary>TCP port for the <c>/mcp</c> HTTP endpoint.</summary>
    public int Port { get; init; } = 47823;

    /// <summary>Idle minutes before a cached per-token REST client is evicted.</summary>
    public int ClientIdleMinutes { get; init; } = 30;

    /// <summary>Bulk write previews enumerate targets up to this count; above it, summary only.</summary>
    public int BulkPreviewThreshold { get; init; } = 25;

    /// <summary>User IDs destructive member ops always refuse (the bot itself is always protected too).</summary>
    public IReadOnlyList<ulong> ProtectedUserIds { get; init; } = Array.Empty<ulong>();

    /// <summary>
    /// Bind from configuration. Env vars override the JSON file, e.g.
    /// <c>ADMCP_AdminDiscordMcp__Port=50000</c>.
    /// </summary>
    public static BotConfig Load(IConfiguration configuration)
    {
        var cfg = new BotConfig();
        configuration.GetSection(Section).Bind(cfg);
        return cfg;
    }
}
