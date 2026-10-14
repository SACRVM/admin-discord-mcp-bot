using System.ComponentModel;
using System.Diagnostics;
using AdminDiscordMcp.Discord;
using ModelContextProtocol.Server;

namespace AdminDiscordMcp.Mcp.Tools;

/// <summary>
/// OAuth2 bot-invite helper. A bot cannot join a guild on its own — a human with
/// Manage Server must approve it on Discord's authorization page. This tool builds
/// that URL from the per-request token's own application id (resolved live via
/// <c>/oauth2/applications/@me</c>, so nothing is configured server-side) and can
/// open it in the host's default browser for the human to complete.
///
/// Opening the browser is the one deliberate host-side action in this server:
/// ShellExecute on a URL assembled exclusively from a fixed discord.com base and
/// validated numeric ids — no caller-controlled string ever reaches the shell.
/// </summary>
[McpServerToolType]
public sealed class BotInviteTools
{
    private readonly IDiscordContext _ctx;
    private readonly AdminAuditLog _audit;

    public BotInviteTools(IDiscordContext ctx, AdminAuditLog audit)
    {
        _ctx = ctx;
        _audit = audit;
    }

    [McpServerTool, Description(
        "Build the OAuth2 authorization URL to invite THIS bot (the one behind the supplied token) into a server, " +
        "and optionally open it in the host machine's default browser so a human can approve it. " +
        "A bot cannot add itself to a guild — a person with Manage Server permission must confirm on the opened page. " +
        "client_id is resolved from the token automatically. permissions defaults to '8' (Administrator); " +
        "pass a smaller bitfield to request less. guildId (optional) preselects the target server in Discord's dialog. " +
        "Not a Discord write — nothing changes until the human approves, so there is no dryRun.")]
    public async Task<object> InviteBot(
        [Description("Permissions bitfield to request, as decimal string. Default '8' = Administrator.")]
        string permissions = "8",
        [Description("Preselect this guild in the authorization dialog (decimal string); the approving user needs Manage Server there. Optional.")]
        string? guildId = null,
        [Description("Open the URL in the host's default browser (default true). Set false to only return the URL.")]
        bool openInBrowser = true)
    {
        if (!ulong.TryParse(permissions, out var perms))
            throw new ArgumentException("permissions must be a decimal permissions bitfield, e.g. '8' for Administrator");

        ulong? gid = null;
        if (!string.IsNullOrWhiteSpace(guildId))
        {
            if (!ulong.TryParse(guildId, out var parsed))
                throw new ArgumentException("guildId must be a decimal string");
            gid = parsed;
        }

        var client = await _ctx.GetClientAsync();
        var app = await client.GetApplicationInfoAsync();

        var url = $"https://discord.com/oauth2/authorize?client_id={app.Id}&scope=bot+applications.commands&permissions={perms}";
        if (gid.HasValue)
            url += $"&guild_id={gid.Value}&disable_guild_select=true";

        var browserOpened = false;
        string? browserError = null;
        if (openInBrowser)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                browserOpened = true;
            }
            catch (Exception ex)
            {
                // Headless/remote host, no default browser, … — the URL is still returned.
                browserError = ex.Message;
            }
        }

        var payload = new
        {
            ok = true,
            url,
            clientId = app.Id.ToString(),
            botUsername = client.CurrentUser?.Username,
            permissions = perms.ToString(),
            guildId = gid?.ToString(),
            browserOpened,
            browserError,
            note = "A human with Manage Server must approve this page; the bot cannot add itself to a guild.",
        };
        _audit.Log("bot_invite_url", payload, ok: true, actor: _ctx.Fingerprint);
        return payload;
    }
}
