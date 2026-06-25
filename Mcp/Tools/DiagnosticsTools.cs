using System.ComponentModel;
using AdminDiscordMcp.Discord;
using ModelContextProtocol.Server;

namespace AdminDiscordMcp.Mcp.Tools;

/// <summary>
/// Minimal connectivity probe — the Phase-1 smoke test for the per-request token
/// pipeline. Proves header → pool → REST login → identity end to end, without any
/// guild or privileged intent.
/// </summary>
[McpServerToolType]
public sealed class DiagnosticsTools
{
    private readonly IDiscordContext _ctx;

    public DiagnosticsTools(IDiscordContext ctx) => _ctx = ctx;

    [McpServerTool, Description(
        "Verify the supplied bot token by logging in via REST and returning the bot's " +
        "own identity. Run this first to confirm connectivity before other tools.")]
    public async Task<object> WhoAmI()
    {
        var client = await _ctx.GetClientAsync();
        var u = client.CurrentUser;
        return new
        {
            ok = true,
            botUserId = u?.Id.ToString(),
            botUsername = u?.Username,
            botGlobalName = u?.GlobalName,
            tokenFingerprint = _ctx.Fingerprint,
        };
    }
}
