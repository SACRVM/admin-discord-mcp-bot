using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using AdminDiscordMcp.Config;
using AdminDiscordMcp.Discord;
using Discord;
using Discord.Rest;
using ModelContextProtocol.Server;

namespace AdminDiscordMcp.Mcp.Tools;

/// <summary>
/// Mutating Discord operations over the REST API. Every tool takes an explicit
/// <c>guildId</c> (no guild lock) and defaults <c>dryRun: true</c> — callers must
/// opt in to apply with <c>dryRun: false</c>. Every call (dry or applied) is
/// appended to <c>data/admin-audit.log</c> via <see cref="AdminAuditLog"/> with the
/// token fingerprint as actor (never the raw token).
///
/// Gateway→REST notes: there is no local cache, so entity lookups are async REST
/// fetches; <see cref="RestGuildUser"/> exposes <c>RoleIds</c> rather than role
/// objects; channels and threads both resolve via <c>GetChannelAsync</c>.
/// Protected users (refused by destructive member ops) are the bot itself plus
/// <see cref="BotConfig.ProtectedUserIds"/>.
/// </summary>
[McpServerToolType]
public sealed class DiscordWriteTools
{
    private readonly IDiscordContext _ctx;
    private readonly AdminAuditLog _audit;
    private readonly BotConfig _config;

    public DiscordWriteTools(IDiscordContext ctx, AdminAuditLog audit, BotConfig config)
    {
        _ctx = ctx;
        _audit = audit;
        _config = config;
    }

    private async Task<RestGuild> GuildAsync(string guildId)
    {
        if (!ulong.TryParse(guildId, out var gid))
            throw new ArgumentException("guildId must be a decimal string");
        var client = await _ctx.GetClientAsync();
        var g = await client.GetGuildAsync(gid);
        if (g == null)
            throw new InvalidOperationException($"Guild {guildId} not found, or the bot is not a member of it.");
        return g;
    }

    /// <summary>Resolve any channel or thread by id (REST has no cache and no separate thread getter).</summary>
    private async Task<IChannel?> ChannelByIdAsync(ulong channelId)
    {
        var client = await _ctx.GetClientAsync();
        return await client.GetChannelAsync(channelId);
    }

    private async Task<IUser?> BotUserAsync() => (await _ctx.GetClientAsync()).CurrentUser;

    /// <summary>
    /// True if destructive member ops must refuse this target: the bot itself, or
    /// any id in <see cref="BotConfig.ProtectedUserIds"/>. Replaces the blueprint's
    /// hardcoded Chloe/Umbrella id checks.
    /// </summary>
    private async Task<bool> IsProtectedAsync(ulong userId)
    {
        if (_config.ProtectedUserIds.Contains(userId)) return true;
        var bot = await BotUserAsync();
        return bot?.Id == userId;
    }

    // ── Roles: create / rename / delete / move ─────────────────────────────────

    [McpServerTool, Description(
        "Create a new role in the guild. colorHex accepts 'RRGGBB' or '#RRGGBB' (omit for default). " +
        "hoist=true displays the role separately in the member list. mentionable=true lets anyone @-mention it. " +
        "Defaults to dryRun=true; pass dryRun=false to actually create.")]
    public async Task<object> CreateRole(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Role name (1–100 chars).")] string name,
        [Description("Hex color, 'RRGGBB' or '#RRGGBB'. Optional.")] string? colorHex = null,
        [Description("Display the role separately in the sidebar member list.")] bool hoist = false,
        [Description("Allow @-mentioning the role.")] bool mentionable = false,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("name is required");

        var color = ParseColor(colorHex);
        var g = await GuildAsync(guildId);

        if (dryRun)
            return AuditAndReturn("role_created",
                new { ok = true, dryRun = true, wouldCreate = new { name, colorHex, hoist, mentionable } }, ok: true);

        var role = await g.CreateRoleAsync(name, permissions: null, color: color, isHoisted: hoist, isMentionable: mentionable);
        return AuditAndReturn("role_created",
            new
            {
                ok = true,
                dryRun = false,
                id = role.Id.ToString(),
                name = role.Name,
                colorHex = ToColorHex(role.Colors.PrimaryColor),
                position = role.Position,
                isHoisted = role.IsHoisted,
                isMentionable = role.IsMentionable,
            }, ok: true);
    }

    [McpServerTool, Description(
        "Modify an existing role. Pass only the fields you want to change; null/omitted fields are left alone. " +
        "Returns a before/after diff. Defaults to dryRun=true.")]
    public async Task<object> RenameRole(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Role ID (decimal string).")] string roleId,
        [Description("New name. Optional.")] string? newName = null,
        [Description("New hex color, 'RRGGBB' or '#RRGGBB'. Optional.")] string? newColorHex = null,
        [Description("New hoist flag. Optional.")] bool? newHoist = null,
        [Description("New mentionable flag. Optional.")] bool? newMentionable = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(roleId, out var rid))
            throw new ArgumentException("roleId must be a decimal string");

        var g = await GuildAsync(guildId);
        var role = g.GetRole(rid);
        if (role == null)
            return AuditAndReturn("role_renamed", new { ok = false, dryRun, roleId, reason = "role_not_found" }, ok: false);

        var before = new
        {
            name = role.Name,
            colorHex = ToColorHex(role.Colors.PrimaryColor),
            isHoisted = role.IsHoisted,
            isMentionable = role.IsMentionable,
        };
        var newColor = ParseColor(newColorHex);
        var after = new
        {
            name = newName ?? before.name,
            colorHex = newColor.HasValue ? ToColorHex(newColor.Value) : before.colorHex,
            isHoisted = newHoist ?? before.isHoisted,
            isMentionable = newMentionable ?? before.isMentionable,
        };

        if (dryRun)
            return AuditAndReturn("role_renamed", new { ok = true, dryRun = true, roleId, before, after }, ok: true);

        await role.ModifyAsync(p =>
        {
            if (newName != null) p.Name = newName;
            if (newColor.HasValue) p.Color = newColor.Value;
            if (newHoist.HasValue) p.Hoist = newHoist.Value;
            if (newMentionable.HasValue) p.Mentionable = newMentionable.Value;
        });

        return AuditAndReturn("role_renamed", new { ok = true, dryRun = false, roleId, before, after }, ok: true);
    }

    [McpServerTool, Description(
        "Delete a role. Refuses to delete @everyone or any IsManaged (bot/integration) role. " +
        "Defaults to dryRun=true — irreversible once applied.")]
    public async Task<object> DeleteRole(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Role ID (decimal string).")] string roleId,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(roleId, out var rid))
            throw new ArgumentException("roleId must be a decimal string");

        var g = await GuildAsync(guildId);
        var role = g.GetRole(rid);
        if (role == null)
            return AuditAndReturn("role_deleted", new { ok = false, dryRun, roleId, reason = "role_not_found" }, ok: false);
        if (role.IsEveryone)
            return AuditAndReturn("role_deleted", new { ok = false, dryRun, roleId, reason = "cannot_delete_everyone" }, ok: false);
        if (role.IsManaged)
            return AuditAndReturn("role_deleted", new { ok = false, dryRun, roleId, reason = "cannot_delete_managed_role" }, ok: false);

        // memberCount omitted vs. blueprint: over REST it would require a full member download.
        var snapshot = new { id = role.Id.ToString(), name = role.Name };

        if (dryRun)
            return AuditAndReturn("role_deleted", new { ok = true, dryRun = true, wouldDelete = snapshot }, ok: true);

        await role.DeleteAsync();
        return AuditAndReturn("role_deleted", new { ok = true, dryRun = false, deleted = snapshot }, ok: true);
    }

    [McpServerTool, Description(
        "Set a role's primary color. Pass colorHex='RRGGBB' / '#RRGGBB'. Returns before/after diff. Defaults to dryRun=true.")]
    public async Task<object> SetRoleColor(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Role ID (decimal string).")] string roleId,
        [Description("Hex color, 'RRGGBB' or '#RRGGBB'.")] string colorHex,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(roleId, out var rid))
            throw new ArgumentException("roleId must be a decimal string");
        if (string.IsNullOrWhiteSpace(colorHex))
            throw new ArgumentException("colorHex is required");

        var g = await GuildAsync(guildId);
        var role = g.GetRole(rid);
        if (role == null)
            return AuditAndReturn("role_color_set", new { ok = false, dryRun, roleId, reason = "role_not_found" }, ok: false);
        if (role.IsEveryone)
            return AuditAndReturn("role_color_set", new { ok = false, dryRun, roleId, reason = "cannot_color_everyone" }, ok: false);

        var newColor = ParseColor(colorHex);
        if (!newColor.HasValue)
            return AuditAndReturn("role_color_set", new { ok = false, dryRun, roleId, reason = "invalid_colorHex" }, ok: false);

        var before = ToColorHex(role.Colors.PrimaryColor);
        var after = ToColorHex(newColor.Value);

        if (dryRun)
            return AuditAndReturn("role_color_set",
                new { ok = true, dryRun = true, roleId, name = role.Name, before, after }, ok: true);

        await role.ModifyAsync(p => p.Color = newColor.Value);
        return AuditAndReturn("role_color_set",
            new { ok = true, dryRun = false, roleId, name = role.Name, before, after }, ok: true);
    }

    [McpServerTool, Description(
        "Move a role to a new position in the hierarchy. Higher position = higher in the role list. " +
        "Bot can only move roles below its own highest role. Defaults to dryRun=true.")]
    public async Task<object> MoveRolePosition(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Role ID (decimal string).")] string roleId,
        [Description("New position (integer; higher = higher in the hierarchy).")] int newPosition,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(roleId, out var rid))
            throw new ArgumentException("roleId must be a decimal string");

        var g = await GuildAsync(guildId);
        var role = g.GetRole(rid);
        if (role == null)
            return AuditAndReturn("role_moved", new { ok = false, dryRun, roleId, reason = "role_not_found" }, ok: false);

        var oldPosition = role.Position;

        if (dryRun)
            return AuditAndReturn("role_moved",
                new { ok = true, dryRun = true, roleId, name = role.Name, oldPosition, newPosition }, ok: true);

        await role.ModifyAsync(p => p.Position = newPosition);
        return AuditAndReturn("role_moved",
            new { ok = true, dryRun = false, roleId, name = role.Name, oldPosition, newPosition }, ok: true);
    }

    // ── Role membership: assign / remove (single + bulk) ───────────────────────

    [McpServerTool, Description(
        "Add a role to one user. Idempotent — if the user already has the role, returns alreadyHad=true. Defaults to dryRun=true.")]
    public async Task<object> AssignRole(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("User ID (decimal string).")] string userId,
        [Description("Role ID (decimal string).")] string roleId,
        [Description("Audit-log reason shown in Discord's native audit log. Optional.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(userId, out var uid))
            throw new ArgumentException("userId must be a decimal string");
        if (!ulong.TryParse(roleId, out var rid))
            throw new ArgumentException("roleId must be a decimal string");

        var g = await GuildAsync(guildId);
        var user = await g.GetUserAsync(uid);
        if (user == null)
            return AuditAndReturn("role_assigned", new { ok = false, dryRun, userId, roleId, reason = "user_not_in_guild" }, ok: false);
        var role = g.GetRole(rid);
        if (role == null)
            return AuditAndReturn("role_assigned", new { ok = false, dryRun, userId, roleId, reason = "role_not_found" }, ok: false);

        var alreadyHad = user.RoleIds.Contains(rid);
        var snapshot = new { userId, username = user.Username, roleId, roleName = role.Name, alreadyHad };

        if (alreadyHad)
            return AuditAndReturn("role_assigned", new { ok = true, dryRun, noop = true, snapshot }, ok: true);

        if (dryRun)
            return AuditAndReturn("role_assigned", new { ok = true, dryRun = true, wouldAssign = snapshot }, ok: true);

        await user.AddRoleAsync(role, BuildOptions(reason));
        return AuditAndReturn("role_assigned",
            new { ok = true, dryRun = false, assigned = snapshot, auditReason = reason }, ok: true);
    }

    [McpServerTool, Description(
        "Remove a role from one user. Idempotent — if the user does not have the role, returns alreadyMissing=true. Defaults to dryRun=true.")]
    public async Task<object> RemoveRole(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("User ID (decimal string).")] string userId,
        [Description("Role ID (decimal string).")] string roleId,
        [Description("Audit-log reason. Optional.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(userId, out var uid))
            throw new ArgumentException("userId must be a decimal string");
        if (!ulong.TryParse(roleId, out var rid))
            throw new ArgumentException("roleId must be a decimal string");

        var g = await GuildAsync(guildId);
        var user = await g.GetUserAsync(uid);
        if (user == null)
            return AuditAndReturn("role_removed", new { ok = false, dryRun, userId, roleId, reason = "user_not_in_guild" }, ok: false);
        var role = g.GetRole(rid);
        if (role == null)
            return AuditAndReturn("role_removed", new { ok = false, dryRun, userId, roleId, reason = "role_not_found" }, ok: false);

        var hasRole = user.RoleIds.Contains(rid);
        var snapshot = new { userId, username = user.Username, roleId, roleName = role.Name, alreadyMissing = !hasRole };

        if (!hasRole)
            return AuditAndReturn("role_removed", new { ok = true, dryRun, noop = true, snapshot }, ok: true);

        if (dryRun)
            return AuditAndReturn("role_removed", new { ok = true, dryRun = true, wouldRemove = snapshot }, ok: true);

        await user.RemoveRoleAsync(role, BuildOptions(reason));
        return AuditAndReturn("role_removed",
            new { ok = true, dryRun = false, removed = snapshot, auditReason = reason }, ok: true);
    }

    [McpServerTool, Description(
        "Add a role to many users. Each user is processed individually; failures don't abort the batch. " +
        "Returns lists of {applied, skipped, failed}. Defaults to dryRun=true — strongly recommended for any batch >5.")]
    public async Task<object> BulkAssignRole(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Role ID (decimal string).")] string roleId,
        [Description("User IDs (array of decimal strings).")] string[] userIds,
        [Description("Audit-log reason. Optional.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(roleId, out var rid))
            throw new ArgumentException("roleId must be a decimal string");
        if (userIds == null || userIds.Length == 0)
            throw new ArgumentException("userIds must be a non-empty array");

        var g = await GuildAsync(guildId);
        var role = g.GetRole(rid);
        if (role == null)
            return AuditAndReturn("roles_bulk_assigned",
                new { ok = false, dryRun, roleId, reason = "role_not_found", count = userIds.Length }, ok: false);

        var applied = new List<object>();
        var skipped = new List<object>();
        var failed = new List<object>();

        foreach (var raw in userIds)
        {
            if (!ulong.TryParse(raw, out var uid))
            {
                failed.Add(new { userId = raw, reason = "userId_not_decimal" });
                continue;
            }
            var user = await g.GetUserAsync(uid);
            if (user == null)
            {
                skipped.Add(new { userId = raw, reason = "user_not_in_guild" });
                continue;
            }
            if (user.RoleIds.Contains(rid))
            {
                skipped.Add(new { userId = raw, username = user.Username, reason = "already_had" });
                continue;
            }
            if (dryRun)
            {
                applied.Add(new { userId = raw, username = user.Username, dryRun = true });
                continue;
            }
            try
            {
                await user.AddRoleAsync(role, BuildOptions(reason));
                applied.Add(new { userId = raw, username = user.Username });
            }
            catch (Exception ex)
            {
                failed.Add(new { userId = raw, username = user.Username, reason = "discord_error", message = ex.Message });
            }
        }

        var result = new
        {
            ok = true, dryRun, roleId, roleName = role.Name,
            appliedCount = applied.Count, skippedCount = skipped.Count, failedCount = failed.Count,
            applied, skipped, failed,
        };
        _audit.Log("roles_bulk_assigned", result, ok: failed.Count == 0, actor: _ctx.Fingerprint);
        return result;
    }

    [McpServerTool, Description(
        "Remove a role from many users. Mirror of bulk_assign_role. " +
        "Returns lists of {applied, skipped, failed}. Defaults to dryRun=true.")]
    public async Task<object> BulkRemoveRole(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Role ID (decimal string).")] string roleId,
        [Description("User IDs (array of decimal strings).")] string[] userIds,
        [Description("Audit-log reason. Optional.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(roleId, out var rid))
            throw new ArgumentException("roleId must be a decimal string");
        if (userIds == null || userIds.Length == 0)
            throw new ArgumentException("userIds must be a non-empty array");

        var g = await GuildAsync(guildId);
        var role = g.GetRole(rid);
        if (role == null)
            return AuditAndReturn("roles_bulk_removed",
                new { ok = false, dryRun, roleId, reason = "role_not_found", count = userIds.Length }, ok: false);

        var applied = new List<object>();
        var skipped = new List<object>();
        var failed = new List<object>();

        foreach (var raw in userIds)
        {
            if (!ulong.TryParse(raw, out var uid))
            {
                failed.Add(new { userId = raw, reason = "userId_not_decimal" });
                continue;
            }
            var user = await g.GetUserAsync(uid);
            if (user == null)
            {
                skipped.Add(new { userId = raw, reason = "user_not_in_guild" });
                continue;
            }
            if (!user.RoleIds.Contains(rid))
            {
                skipped.Add(new { userId = raw, username = user.Username, reason = "already_missing" });
                continue;
            }
            if (dryRun)
            {
                applied.Add(new { userId = raw, username = user.Username, dryRun = true });
                continue;
            }
            try
            {
                await user.RemoveRoleAsync(role, BuildOptions(reason));
                applied.Add(new { userId = raw, username = user.Username });
            }
            catch (Exception ex)
            {
                failed.Add(new { userId = raw, username = user.Username, reason = "discord_error", message = ex.Message });
            }
        }

        var result = new
        {
            ok = true, dryRun, roleId, roleName = role.Name,
            appliedCount = applied.Count, skippedCount = skipped.Count, failedCount = failed.Count,
            applied, skipped, failed,
        };
        _audit.Log("roles_bulk_removed", result, ok: failed.Count == 0, actor: _ctx.Fingerprint);
        return result;
    }

    // ── Membership: kick / ban ─────────────────────────────────────────────────

    [McpServerTool, Description(
        "Kick a user from the guild. They can rejoin via invite. Refuses to target the bot itself or any configured protected user. Defaults to dryRun=true.")]
    public async Task<object> KickMember(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("User ID (decimal string).")] string userId,
        [Description("Audit-log reason. Optional but strongly recommended.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(userId, out var uid))
            throw new ArgumentException("userId must be a decimal string");
        if (await IsProtectedAsync(uid))
            return AuditAndReturn("member_kicked", new { ok = false, dryRun, userId, reason = "protected_target" }, ok: false);

        var g = await GuildAsync(guildId);
        var user = await g.GetUserAsync(uid);
        if (user == null)
            return AuditAndReturn("member_kicked", new { ok = false, dryRun, userId, reason = "user_not_in_guild" }, ok: false);

        var snapshot = new { userId, username = user.Username, globalName = user.GlobalName };

        if (dryRun)
            return AuditAndReturn("member_kicked", new { ok = true, dryRun = true, wouldKick = snapshot }, ok: true);

        await user.KickAsync(reason);
        return AuditAndReturn("member_kicked",
            new { ok = true, dryRun = false, kicked = snapshot, auditReason = reason }, ok: true);
    }

    [McpServerTool, Description(
        "Ban a user from the guild. Refuses to target the bot itself or any configured protected user. " +
        "pruneDays deletes the banned user's messages from the last N days (0–7, default 0). Defaults to dryRun=true — high blast radius.")]
    public async Task<object> BanMember(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("User ID (decimal string).")] string userId,
        [Description("Audit-log reason. Optional but strongly recommended.")] string? reason = null,
        [Description("Days of message history to prune (0–7). Default 0.")] int pruneDays = 0,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(userId, out var uid))
            throw new ArgumentException("userId must be a decimal string");
        if (await IsProtectedAsync(uid))
            return AuditAndReturn("member_banned", new { ok = false, dryRun, userId, reason = "protected_target" }, ok: false);
        if (pruneDays < 0) pruneDays = 0;
        if (pruneDays > 7) pruneDays = 7;

        var g = await GuildAsync(guildId);
        var user = await g.GetUserAsync(uid);
        var snapshot = new
        {
            userId,
            username = user?.Username,
            globalName = user?.GlobalName,
            inGuild = user != null,
            pruneDays,
        };

        if (dryRun)
            return AuditAndReturn("member_banned", new { ok = true, dryRun = true, wouldBan = snapshot }, ok: true);

        await g.AddBanAsync(uid, pruneDays, reason);
        return AuditAndReturn("member_banned",
            new { ok = true, dryRun = false, banned = snapshot, auditReason = reason }, ok: true);
    }

    // ── Messages: bulk delete / pin / reactions ────────────────────────────────

    [McpServerTool, Description(
        "Bulk delete messages in a channel or thread by ID list. Discord's batch endpoint only accepts messages younger " +
        "than 14 days; older ones are deleted one at a time as a fallback. Refuses to delete messages from protected users. " +
        "Returns counts of deleted/skipped/failed. Defaults to dryRun=true — irreversible once applied.")]
    public async Task<object> BulkDeleteMessages(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Channel or thread ID (decimal string) the messages live in.")] string channelId,
        [Description("Comma-separated message IDs (decimal strings).")] string messageIds,
        [Description("Audit-log reason. Optional but recommended.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(channelId, out var cid))
            throw new ArgumentException("channelId must be a decimal string");
        if (string.IsNullOrWhiteSpace(messageIds))
            throw new ArgumentException("messageIds is required");

        var ids = new List<ulong>();
        foreach (var raw in messageIds.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!ulong.TryParse(raw.Trim(), out var mid))
                throw new ArgumentException($"messageId '{raw}' is not a decimal string");
            ids.Add(mid);
        }
        if (ids.Count == 0)
            throw new ArgumentException("messageIds parsed to empty list");

        var ch = await ChannelByIdAsync(cid);
        if (ch is not IMessageChannel mc)
            return AuditAndReturn("messages_bulk_deleted",
                new { ok = false, dryRun, channelId, reason = "channel_not_messageable" }, ok: false);

        var resolved = new List<IMessage>();
        var notFound = new List<string>();
        var protectedIds = new List<string>();
        foreach (var id in ids)
        {
            var msg = await mc.GetMessageAsync(id);
            if (msg == null) { notFound.Add(id.ToString()); continue; }
            if (await IsProtectedAsync(msg.Author.Id)) { protectedIds.Add(id.ToString()); continue; }
            resolved.Add(msg);
        }

        if (dryRun)
            return AuditAndReturn("messages_bulk_deleted",
                new
                {
                    ok = true,
                    dryRun = true,
                    channelId,
                    channelName = ch.Name,
                    wouldDelete = resolved.Select(m => new { id = m.Id.ToString(), authorId = m.Author.Id.ToString(), authorName = m.Author.Username, ageDays = (int)(DateTime.UtcNow - m.Timestamp.UtcDateTime).TotalDays }).ToArray(),
                    notFound,
                    skippedProtected = protectedIds,
                    auditReason = reason,
                }, ok: true);

        var opts = BuildOptions(reason);
        var threshold = DateTimeOffset.UtcNow.AddDays(-14).AddMinutes(5);
        var fresh = resolved.Where(m => m.Timestamp > threshold).ToList();
        var stale = resolved.Where(m => m.Timestamp <= threshold).ToList();

        int deleted = 0;
        var failed = new List<object>();

        if (fresh.Count > 0 && ch is ITextChannel txt)
        {
            try
            {
                await txt.DeleteMessagesAsync(fresh, opts);
                deleted += fresh.Count;
            }
            catch (Exception ex)
            {
                foreach (var m in fresh)
                    failed.Add(new { id = m.Id.ToString(), error = "bulk_failed:" + ex.Message });
            }
        }
        else
        {
            stale = stale.Concat(fresh).ToList();
            fresh.Clear();
        }

        foreach (var m in stale)
        {
            try { await m.DeleteAsync(opts); deleted++; }
            catch (Exception ex) { failed.Add(new { id = m.Id.ToString(), error = ex.Message }); }
        }

        return AuditAndReturn("messages_bulk_deleted",
            new
            {
                ok = true,
                dryRun = false,
                channelId,
                channelName = ch.Name,
                deleted,
                notFound,
                skippedProtected = protectedIds,
                failed,
                auditReason = reason,
            }, ok: true);
    }

    [McpServerTool, Description(
        "Pin a message in its channel. Idempotent (alreadyPinned=true if already pinned). Defaults to dryRun=true.")]
    public async Task<object> PinMessage(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Channel or thread ID (decimal string).")] string channelId,
        [Description("Message ID (decimal string).")] string messageId,
        [Description("Audit-log reason. Optional.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(channelId, out var cid))
            throw new ArgumentException("channelId must be a decimal string");
        if (!ulong.TryParse(messageId, out var mid))
            throw new ArgumentException("messageId must be a decimal string");

        var ch = await ChannelByIdAsync(cid);
        if (ch is not IMessageChannel mc)
            return AuditAndReturn("message_pinned", new { ok = false, dryRun, channelId, reason = "channel_not_messageable" }, ok: false);

        var msg = await mc.GetMessageAsync(mid);
        if (msg == null)
            return AuditAndReturn("message_pinned", new { ok = false, dryRun, channelId, messageId, reason = "message_not_found" }, ok: false);
        if (msg is not IUserMessage userMsg)
            return AuditAndReturn("message_pinned", new { ok = false, dryRun, channelId, messageId, reason = "not_a_user_message" }, ok: false);
        if (msg.IsPinned)
            return AuditAndReturn("message_pinned", new { ok = true, dryRun, channelId, messageId, alreadyPinned = true }, ok: true);

        if (dryRun)
            return AuditAndReturn("message_pinned", new { ok = true, dryRun = true, channelId, messageId, auditReason = reason }, ok: true);

        await userMsg.PinAsync(BuildOptions(reason));
        return AuditAndReturn("message_pinned", new { ok = true, dryRun = false, channelId, messageId, auditReason = reason }, ok: true);
    }

    [McpServerTool, Description(
        "Unpin a message in its channel. Idempotent (alreadyUnpinned=true if not pinned). Defaults to dryRun=true.")]
    public async Task<object> UnpinMessage(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Channel or thread ID (decimal string).")] string channelId,
        [Description("Message ID (decimal string).")] string messageId,
        [Description("Audit-log reason. Optional.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(channelId, out var cid))
            throw new ArgumentException("channelId must be a decimal string");
        if (!ulong.TryParse(messageId, out var mid))
            throw new ArgumentException("messageId must be a decimal string");

        var ch = await ChannelByIdAsync(cid);
        if (ch is not IMessageChannel mc)
            return AuditAndReturn("message_unpinned", new { ok = false, dryRun, channelId, reason = "channel_not_messageable" }, ok: false);

        var msg = await mc.GetMessageAsync(mid);
        if (msg == null)
            return AuditAndReturn("message_unpinned", new { ok = false, dryRun, channelId, messageId, reason = "message_not_found" }, ok: false);
        if (msg is not IUserMessage userMsg)
            return AuditAndReturn("message_unpinned", new { ok = false, dryRun, channelId, messageId, reason = "not_a_user_message" }, ok: false);
        if (!msg.IsPinned)
            return AuditAndReturn("message_unpinned", new { ok = true, dryRun, channelId, messageId, alreadyUnpinned = true }, ok: true);

        if (dryRun)
            return AuditAndReturn("message_unpinned", new { ok = true, dryRun = true, channelId, messageId, auditReason = reason }, ok: true);

        await userMsg.UnpinAsync(BuildOptions(reason));
        return AuditAndReturn("message_unpinned", new { ok = true, dryRun = false, channelId, messageId, auditReason = reason }, ok: true);
    }

    [McpServerTool, Description(
        "Add a reaction to a message. emoji can be a unicode emoji (e.g. '👍') or a custom emote in Discord syntax " +
        "(e.g. '<:name:123456789>' or '<a:name:123456789>'). Defaults to dryRun=true.")]
    public async Task<object> AddReaction(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Channel or thread ID (decimal string).")] string channelId,
        [Description("Message ID (decimal string).")] string messageId,
        [Description("Unicode emoji or '<:name:id>' / '<a:name:id>' custom emote.")] string emoji,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(channelId, out var cid))
            throw new ArgumentException("channelId must be a decimal string");
        if (!ulong.TryParse(messageId, out var mid))
            throw new ArgumentException("messageId must be a decimal string");
        if (string.IsNullOrWhiteSpace(emoji))
            throw new ArgumentException("emoji is required");

        var emote = ParseEmote(emoji);
        if (emote == null)
            return AuditAndReturn("reaction_added", new { ok = false, dryRun, channelId, messageId, reason = "invalid_emoji" }, ok: false);

        var ch = await ChannelByIdAsync(cid);
        if (ch is not IMessageChannel mc)
            return AuditAndReturn("reaction_added", new { ok = false, dryRun, channelId, reason = "channel_not_messageable" }, ok: false);

        var msg = await mc.GetMessageAsync(mid);
        if (msg is not IUserMessage userMsg)
            return AuditAndReturn("reaction_added", new { ok = false, dryRun, channelId, messageId, reason = "message_not_found" }, ok: false);

        if (dryRun)
            return AuditAndReturn("reaction_added", new { ok = true, dryRun = true, channelId, messageId, emoji }, ok: true);

        await userMsg.AddReactionAsync(emote);
        return AuditAndReturn("reaction_added", new { ok = true, dryRun = false, channelId, messageId, emoji }, ok: true);
    }

    [McpServerTool, Description(
        "Remove the bot's own reaction from a message. emoji can be unicode or custom-emote syntax. Defaults to dryRun=true.")]
    public async Task<object> RemoveReaction(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Channel or thread ID (decimal string).")] string channelId,
        [Description("Message ID (decimal string).")] string messageId,
        [Description("Unicode emoji or '<:name:id>' / '<a:name:id>' custom emote.")] string emoji,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(channelId, out var cid))
            throw new ArgumentException("channelId must be a decimal string");
        if (!ulong.TryParse(messageId, out var mid))
            throw new ArgumentException("messageId must be a decimal string");
        if (string.IsNullOrWhiteSpace(emoji))
            throw new ArgumentException("emoji is required");

        var emote = ParseEmote(emoji);
        if (emote == null)
            return AuditAndReturn("reaction_removed", new { ok = false, dryRun, channelId, messageId, reason = "invalid_emoji" }, ok: false);

        var ch = await ChannelByIdAsync(cid);
        if (ch is not IMessageChannel mc)
            return AuditAndReturn("reaction_removed", new { ok = false, dryRun, channelId, reason = "channel_not_messageable" }, ok: false);

        var msg = await mc.GetMessageAsync(mid);
        if (msg is not IUserMessage userMsg)
            return AuditAndReturn("reaction_removed", new { ok = false, dryRun, channelId, messageId, reason = "message_not_found" }, ok: false);

        if (dryRun)
            return AuditAndReturn("reaction_removed", new { ok = true, dryRun = true, channelId, messageId, emoji }, ok: true);

        var bot = await BotUserAsync();
        await userMsg.RemoveReactionAsync(emote, bot);
        return AuditAndReturn("reaction_removed", new { ok = true, dryRun = false, channelId, messageId, emoji }, ok: true);
    }

    // ── Channel permissions: per-role / per-user overwrites ────────────────────

    [McpServerTool, Description(
        "Set permission overwrites for a role or user on a channel. allow / deny are comma-separated ChannelPermission names " +
        "(e.g. 'ViewChannel,SendMessages'). This REPLACES the current overwrite for the target. targetType is 'role' or 'user'. " +
        "Returns before/after diff. Defaults to dryRun=true. Common flags: ViewChannel, ManageChannels, ManageRoles, " +
        "ManageWebhooks, CreateInstantInvite, SendMessages, SendMessagesInThreads, CreatePublicThreads, CreatePrivateThreads, " +
        "EmbedLinks, AttachFiles, AddReactions, UseExternalEmojis, MentionEveryone, ManageMessages, ManageThreads, " +
        "ReadMessageHistory, SendTtsMessages, UseApplicationCommands, Connect, Speak, Stream, MuteMembers, DeafenMembers, " +
        "MoveMembers, UseVad, PrioritySpeaker.")]
    public async Task<object> SetChannelPermissions(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Channel ID (decimal string).")] string channelId,
        [Description("'role' or 'user'.")] string targetType,
        [Description("Role ID or user ID (decimal string).")] string targetId,
        [Description("Comma-separated permission flags to allow. Empty for none.")] string allow = "",
        [Description("Comma-separated permission flags to deny. Empty for none.")] string deny = "",
        [Description("Audit-log reason. Optional.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(channelId, out var cid))
            throw new ArgumentException("channelId must be a decimal string");
        if (!ulong.TryParse(targetId, out var tid))
            throw new ArgumentException("targetId must be a decimal string");

        var g = await GuildAsync(guildId);
        if (await ChannelByIdAsync(cid) is not IGuildChannel gch)
            return AuditAndReturn("channel_perms_set", new { ok = false, dryRun, channelId, reason = "channel_not_found" }, ok: false);

        ulong allowMask, denyMask;
        try
        {
            allowMask = ParsePermissionFlags(allow);
            denyMask = ParsePermissionFlags(deny);
        }
        catch (ArgumentException ex)
        {
            return AuditAndReturn("channel_perms_set",
                new { ok = false, dryRun, channelId, reason = "invalid_permission_flag", detail = ex.Message }, ok: false);
        }

        if ((allowMask & denyMask) != 0)
            return AuditAndReturn("channel_perms_set", new { ok = false, dryRun, channelId, reason = "allow_and_deny_overlap" }, ok: false);

        string? targetKind = targetType?.Trim().ToLowerInvariant();
        string targetName;
        IRole? roleObj = null;
        IGuildUser? userObj = null;
        OverwritePermissions? before;
        switch (targetKind)
        {
            case "role":
                roleObj = g.GetRole(tid);
                if (roleObj == null)
                    return AuditAndReturn("channel_perms_set",
                        new { ok = false, dryRun, channelId, targetType, targetId, reason = "role_not_found" }, ok: false);
                targetName = roleObj.Name;
                before = gch.GetPermissionOverwrite(roleObj);
                break;
            case "user":
                userObj = await g.GetUserAsync(tid);
                if (userObj == null)
                    return AuditAndReturn("channel_perms_set",
                        new { ok = false, dryRun, channelId, targetType, targetId, reason = "user_not_in_guild" }, ok: false);
                targetName = userObj.Username;
                before = gch.GetPermissionOverwrite(userObj);
                break;
            default:
                return AuditAndReturn("channel_perms_set", new { ok = false, dryRun, channelId, reason = "invalid_targetType" }, ok: false);
        }

        var beforeDiag = before.HasValue
            ? new { allow = PermissionMaskToString(before.Value.AllowValue), deny = PermissionMaskToString(before.Value.DenyValue) }
            : new { allow = "", deny = "" };
        var afterDiag = new { allow = PermissionMaskToString(allowMask), deny = PermissionMaskToString(denyMask) };

        if (dryRun)
            return AuditAndReturn("channel_perms_set",
                new { ok = true, dryRun = true, channelId, channelName = gch.Name, targetType, targetId, targetName, before = beforeDiag, after = afterDiag, auditReason = reason }, ok: true);

        var perms = new OverwritePermissions(allowMask, denyMask);
        var opts = BuildOptions(reason);
        if (roleObj != null) await gch.AddPermissionOverwriteAsync(roleObj, perms, opts);
        else if (userObj != null) await gch.AddPermissionOverwriteAsync(userObj, perms, opts);

        return AuditAndReturn("channel_perms_set",
            new { ok = true, dryRun = false, channelId, channelName = gch.Name, targetType, targetId, targetName, before = beforeDiag, after = afterDiag, auditReason = reason }, ok: true);
    }

    [McpServerTool, Description(
        "Remove a permission overwrite for a role or user on a channel — the target falls back to inherited permissions. " +
        "Idempotent (no-op if overwrite already absent). targetType is 'role' or 'user'. Defaults to dryRun=true.")]
    public async Task<object> ClearChannelPermissions(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Channel ID (decimal string).")] string channelId,
        [Description("'role' or 'user'.")] string targetType,
        [Description("Role ID or user ID (decimal string).")] string targetId,
        [Description("Audit-log reason. Optional.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(channelId, out var cid))
            throw new ArgumentException("channelId must be a decimal string");
        if (!ulong.TryParse(targetId, out var tid))
            throw new ArgumentException("targetId must be a decimal string");

        var g = await GuildAsync(guildId);
        if (await ChannelByIdAsync(cid) is not IGuildChannel gch)
            return AuditAndReturn("channel_perms_cleared", new { ok = false, dryRun, channelId, reason = "channel_not_found" }, ok: false);

        string? targetKind = targetType?.Trim().ToLowerInvariant();
        IRole? roleObj = null;
        IGuildUser? userObj = null;
        OverwritePermissions? existing;
        string targetName;
        switch (targetKind)
        {
            case "role":
                roleObj = g.GetRole(tid);
                if (roleObj == null)
                    return AuditAndReturn("channel_perms_cleared",
                        new { ok = false, dryRun, channelId, targetType, targetId, reason = "role_not_found" }, ok: false);
                targetName = roleObj.Name;
                existing = gch.GetPermissionOverwrite(roleObj);
                break;
            case "user":
                userObj = await g.GetUserAsync(tid);
                if (userObj == null)
                    return AuditAndReturn("channel_perms_cleared",
                        new { ok = false, dryRun, channelId, targetType, targetId, reason = "user_not_in_guild" }, ok: false);
                targetName = userObj.Username;
                existing = gch.GetPermissionOverwrite(userObj);
                break;
            default:
                return AuditAndReturn("channel_perms_cleared", new { ok = false, dryRun, channelId, reason = "invalid_targetType" }, ok: false);
        }

        if (!existing.HasValue)
            return AuditAndReturn("channel_perms_cleared",
                new { ok = true, dryRun, channelId, channelName = gch.Name, targetType, targetId, targetName, alreadyAbsent = true }, ok: true);

        if (dryRun)
            return AuditAndReturn("channel_perms_cleared",
                new { ok = true, dryRun = true, channelId, channelName = gch.Name, targetType, targetId, targetName, removed = new { allow = PermissionMaskToString(existing.Value.AllowValue), deny = PermissionMaskToString(existing.Value.DenyValue) }, auditReason = reason }, ok: true);

        var opts = BuildOptions(reason);
        if (roleObj != null) await gch.RemovePermissionOverwriteAsync(roleObj, opts);
        else if (userObj != null) await gch.RemovePermissionOverwriteAsync(userObj, opts);

        return AuditAndReturn("channel_perms_cleared",
            new { ok = true, dryRun = false, channelId, channelName = gch.Name, targetType, targetId, targetName, removed = new { allow = PermissionMaskToString(existing.Value.AllowValue), deny = PermissionMaskToString(existing.Value.DenyValue) }, auditReason = reason }, ok: true);
    }

    // ── Threads: create / modify ───────────────────────────────────────────────

    [McpServerTool, Description(
        "Create a thread on a text channel. If parentMessageId is provided the thread is anchored to that message. " +
        "autoArchiveMinutes accepts 60, 1440, 4320, 10080. isPrivate=true creates a private thread. " +
        "Returns the new thread id. Defaults to dryRun=true.")]
    public async Task<object> CreateThread(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Parent text channel ID (decimal string).")] string channelId,
        [Description("Thread name (1–100 chars).")] string name,
        [Description("Auto-archive duration in minutes: 60, 1440, 4320, 10080. Default 1440.")] int autoArchiveMinutes = 1440,
        [Description("Anchor the thread to this message ID. Optional.")] string? parentMessageId = null,
        [Description("Create as a private thread.")] bool isPrivate = false,
        [Description("Audit-log reason. Optional.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(channelId, out var cid))
            throw new ArgumentException("channelId must be a decimal string");
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("name is required");

        if (await ChannelByIdAsync(cid) is not ITextChannel txt)
            return AuditAndReturn("thread_created", new { ok = false, dryRun, channelId, reason = "channel_not_text" }, ok: false);

        ThreadArchiveDuration archive;
        switch (autoArchiveMinutes)
        {
            case 60: archive = ThreadArchiveDuration.OneHour; break;
            case 1440: archive = ThreadArchiveDuration.OneDay; break;
            case 4320: archive = ThreadArchiveDuration.ThreeDays; break;
            case 10080: archive = ThreadArchiveDuration.OneWeek; break;
            default:
                return AuditAndReturn("thread_created", new { ok = false, dryRun, channelId, reason = "invalid_autoArchiveMinutes" }, ok: false);
        }

        IMessage? anchor = null;
        if (!string.IsNullOrWhiteSpace(parentMessageId))
        {
            if (!ulong.TryParse(parentMessageId, out var pmid))
                throw new ArgumentException("parentMessageId must be a decimal string");
            anchor = await txt.GetMessageAsync(pmid);
            if (anchor == null)
                return AuditAndReturn("thread_created", new { ok = false, dryRun, channelId, parentMessageId, reason = "parent_message_not_found" }, ok: false);
        }

        var preview = new { channelId, channelName = txt.Name, threadName = name, autoArchiveMinutes, isPrivate, parentMessageId };

        if (dryRun)
            return AuditAndReturn("thread_created", new { ok = true, dryRun = true, wouldCreate = preview, auditReason = reason }, ok: true);

        var threadType = isPrivate ? ThreadType.PrivateThread : ThreadType.PublicThread;
        var thread = await txt.CreateThreadAsync(name, threadType, archive, anchor, options: BuildOptions(reason));

        return AuditAndReturn("thread_created",
            new { ok = true, dryRun = false, threadId = thread.Id.ToString(), threadName = thread.Name, channelId, channelName = txt.Name, isPrivate, autoArchiveMinutes, auditReason = reason }, ok: true);
    }

    [McpServerTool, Description(
        "Modify a thread: rename, archive/unarchive, lock/unlock, change auto-archive duration. Pass only the fields you " +
        "want to change. autoArchiveMinutes accepts 60, 1440, 4320, 10080. Returns before/after diff. Defaults to dryRun=true.")]
    public async Task<object> ModifyThread(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Thread ID (decimal string).")] string threadId,
        [Description("New name. Optional.")] string? newName = null,
        [Description("Set true to archive, false to unarchive. Null = leave alone.")] bool? archived = null,
        [Description("Set true to lock, false to unlock. Null = leave alone.")] bool? locked = null,
        [Description("New auto-archive duration in minutes (60/1440/4320/10080). Null = leave alone.")] int? autoArchiveMinutes = null,
        [Description("Audit-log reason. Optional.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(threadId, out var tid))
            throw new ArgumentException("threadId must be a decimal string");

        if (await ChannelByIdAsync(tid) is not RestThreadChannel thread)
            return AuditAndReturn("thread_modified", new { ok = false, dryRun, threadId, reason = "thread_not_found" }, ok: false);

        ThreadArchiveDuration? newArchive = null;
        if (autoArchiveMinutes.HasValue)
        {
            switch (autoArchiveMinutes.Value)
            {
                case 60: newArchive = ThreadArchiveDuration.OneHour; break;
                case 1440: newArchive = ThreadArchiveDuration.OneDay; break;
                case 4320: newArchive = ThreadArchiveDuration.ThreeDays; break;
                case 10080: newArchive = ThreadArchiveDuration.OneWeek; break;
                default:
                    return AuditAndReturn("thread_modified", new { ok = false, dryRun, threadId, reason = "invalid_autoArchiveMinutes" }, ok: false);
            }
        }

        var before = new
        {
            name = thread.Name,
            archived = thread.IsArchived,
            locked = thread.IsLocked,
            autoArchiveMinutes = (int)thread.AutoArchiveDuration,
        };
        var after = new
        {
            name = newName ?? before.name,
            archived = archived ?? before.archived,
            locked = locked ?? before.locked,
            autoArchiveMinutes = autoArchiveMinutes ?? before.autoArchiveMinutes,
        };

        if (dryRun)
            return AuditAndReturn("thread_modified", new { ok = true, dryRun = true, threadId, before, after, auditReason = reason }, ok: true);

        await thread.ModifyAsync(p =>
        {
            if (newName != null) p.Name = newName;
            if (archived.HasValue) p.Archived = archived.Value;
            if (locked.HasValue) p.Locked = locked.Value;
            if (newArchive.HasValue) p.AutoArchiveDuration = newArchive.Value;
        }, BuildOptions(reason));

        return AuditAndReturn("thread_modified", new { ok = true, dryRun = false, threadId, before, after, auditReason = reason }, ok: true);
    }

    // ── Messages: send / dm / edit / delete ────────────────────────────────────

    [McpServerTool, Description(
        "Send a message into a channel or thread. Either content or an embed (or both) must be provided. " +
        "Returns the new message id and jump url. Defaults to dryRun=true. " +
        "Embed fields are independently optional: embedTitle / embedDescription / embedColorHex / embedFooter / embedImageUrl / embedThumbnailUrl.")]
    public async Task<object> SendMessage(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Channel or thread ID (decimal string) to post into.")] string channelId,
        [Description("Plain message content. Optional if an embed is provided.")] string? content = null,
        [Description("Embed title. Optional.")] string? embedTitle = null,
        [Description("Embed description. Optional.")] string? embedDescription = null,
        [Description("Embed color hex 'RRGGBB' or '#RRGGBB'. Optional.")] string? embedColorHex = null,
        [Description("Embed footer text. Optional.")] string? embedFooter = null,
        [Description("Embed image URL. Optional.")] string? embedImageUrl = null,
        [Description("Embed thumbnail URL. Optional.")] string? embedThumbnailUrl = null,
        [Description("Suppress @-mentions in this message. Default true — safer for bot posts.")] bool suppressMentions = true,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(channelId, out var cid))
            throw new ArgumentException("channelId must be a decimal string");

        bool hasEmbed = !string.IsNullOrWhiteSpace(embedTitle)
                        || !string.IsNullOrWhiteSpace(embedDescription)
                        || !string.IsNullOrWhiteSpace(embedImageUrl)
                        || !string.IsNullOrWhiteSpace(embedThumbnailUrl);
        if (string.IsNullOrWhiteSpace(content) && !hasEmbed)
            throw new ArgumentException("either content or at least one embed field must be provided");

        var ch = await ChannelByIdAsync(cid);
        if (ch is not IMessageChannel mc)
            return AuditAndReturn("message_sent", new { ok = false, dryRun, channelId, reason = "channel_not_messageable" }, ok: false);

        Embed? embed = hasEmbed ? BuildEmbed(embedTitle, embedDescription, embedColorHex, embedFooter, embedImageUrl, embedThumbnailUrl) : null;

        int chunkCount = string.IsNullOrEmpty(content) ? (hasEmbed ? 1 : 0) : MessageChunker.Chunk(content).Count();
        var preview = new { channelId, channelName = ch.Name, contentLength = content?.Length ?? 0, chunkCount, hasEmbed, embedTitle, suppressMentions };

        if (dryRun)
            return AuditAndReturn("message_sent", new { ok = true, dryRun = true, wouldSend = preview }, ok: true);

        var allowed = suppressMentions ? AllowedMentions.None : AllowedMentions.All;
        var (posted, sent) = await SendChunkedAsync(mc, content, embed, allowed);

        return AuditAndReturn("message_sent",
            new { ok = true, dryRun = false, messageId = posted.Id.ToString(), jumpUrl = posted.GetJumpUrl(), chunkCount = sent, channelId, channelName = ch.Name }, ok: true);
    }

    [McpServerTool, Description(
        "Send a direct message (DM) to a user by user ID. Either content or an embed (or both) must be provided. " +
        "Returns the new message id and dm channel id. Defaults to dryRun=true. " +
        "Fails with reason='dm_blocked_or_disabled' if the user has DMs from this bot disabled, " +
        "or reason='user_not_found' if the userId can't be resolved.")]
    public async Task<object> DmUser(
        [Description("Target user ID (decimal string).")] string userId,
        [Description("Plain message content. Optional if an embed is provided.")] string? content = null,
        [Description("Embed title. Optional.")] string? embedTitle = null,
        [Description("Embed description. Optional.")] string? embedDescription = null,
        [Description("Embed color hex 'RRGGBB' or '#RRGGBB'. Optional.")] string? embedColorHex = null,
        [Description("Embed footer text. Optional.")] string? embedFooter = null,
        [Description("Embed image URL. Optional.")] string? embedImageUrl = null,
        [Description("Embed thumbnail URL. Optional.")] string? embedThumbnailUrl = null,
        [Description("Suppress @-mentions in this message. Default true — safer for bot posts.")] bool suppressMentions = true,
        [Description("If true (default), simulate without sending.")] bool dryRun = true)
    {
        if (!ulong.TryParse(userId, out var uid))
            throw new ArgumentException("userId must be a decimal string");

        bool hasEmbed = !string.IsNullOrWhiteSpace(embedTitle)
                        || !string.IsNullOrWhiteSpace(embedDescription)
                        || !string.IsNullOrWhiteSpace(embedImageUrl)
                        || !string.IsNullOrWhiteSpace(embedThumbnailUrl);
        if (string.IsNullOrWhiteSpace(content) && !hasEmbed)
            throw new ArgumentException("either content or at least one embed field must be provided");

        var client = await _ctx.GetClientAsync();
        IUser? user = await client.GetUserAsync(uid);
        if (user == null)
            return AuditAndReturn("user_dm_sent", new { ok = false, dryRun, userId, reason = "user_not_found" }, ok: false);

        Embed? embed = hasEmbed ? BuildEmbed(embedTitle, embedDescription, embedColorHex, embedFooter, embedImageUrl, embedThumbnailUrl) : null;

        int chunkCount = string.IsNullOrEmpty(content) ? (hasEmbed ? 1 : 0) : MessageChunker.Chunk(content).Count();
        var preview = new { userId, username = user.Username, contentLength = content?.Length ?? 0, chunkCount, hasEmbed, embedTitle, suppressMentions };

        if (dryRun)
            return AuditAndReturn("user_dm_sent", new { ok = true, dryRun = true, wouldSend = preview }, ok: true);

        try
        {
            var dm = await user.CreateDMChannelAsync();
            var allowed = suppressMentions ? AllowedMentions.None : AllowedMentions.All;
            var (posted, sent) = await SendChunkedAsync(dm, content, embed, allowed);
            return AuditAndReturn("user_dm_sent",
                new { ok = true, dryRun = false, userId, username = user.Username, dmChannelId = dm.Id.ToString(), messageId = posted.Id.ToString(), chunkCount = sent }, ok: true);
        }
        catch (global::Discord.Net.HttpException ex) when ((int?)ex.DiscordCode == 50007 || (int?)ex.DiscordCode == 50278)
        {
            // 50007 = recipient has DMs disabled or blocked the bot.
            // 50278 = "no mutual guilds" / per-server DM privacy off. Same outcome.
            return AuditAndReturn("user_dm_sent",
                new { ok = false, dryRun = false, userId, username = user.Username, reason = "dm_blocked_or_disabled" }, ok: false);
        }
    }

    [McpServerTool, Description(
        "Edit the plain-text content of a message previously posted by the bot. Discord only allows editing messages " +
        "authored by this bot. Returns before/after content lengths. Defaults to dryRun=true.")]
    public async Task<object> EditMessageContent(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Channel or thread ID (decimal string) the message is in.")] string channelId,
        [Description("Message ID (decimal string).")] string messageId,
        [Description("New plain-text content. Empty string clears the content (only allowed if the message has an embed or attachment).")] string newContent,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(channelId, out var cid))
            throw new ArgumentException("channelId must be a decimal string");
        if (!ulong.TryParse(messageId, out var mid))
            throw new ArgumentException("messageId must be a decimal string");
        if (newContent == null)
            throw new ArgumentException("newContent is required (use empty string to clear)");

        var ch = await ChannelByIdAsync(cid);
        if (ch is not IMessageChannel mc)
            return AuditAndReturn("message_content_edited", new { ok = false, dryRun, channelId, messageId, reason = "channel_not_messageable" }, ok: false);

        var msg = await mc.GetMessageAsync(mid);
        if (msg == null)
            return AuditAndReturn("message_content_edited", new { ok = false, dryRun, channelId, messageId, reason = "message_not_found" }, ok: false);

        ulong botId = (await BotUserAsync())?.Id ?? 0;
        if (msg.Author.Id != botId)
            return AuditAndReturn("message_content_edited", new { ok = false, dryRun, channelId, messageId, reason = "not_bot_authored", authorId = msg.Author.Id.ToString() }, ok: false);
        if (msg is not IUserMessage userMsg)
            return AuditAndReturn("message_content_edited", new { ok = false, dryRun, channelId, messageId, reason = "not_a_user_message" }, ok: false);

        string before = msg.Content ?? "";
        if (string.Equals(before, newContent, StringComparison.Ordinal))
            return AuditAndReturn("message_content_edited", new { ok = true, dryRun, channelId, messageId, reason = "no_change", contentLength = before.Length }, ok: true);

        if (dryRun)
            return AuditAndReturn("message_content_edited", new { ok = true, dryRun = true, channelId, messageId, beforeLength = before.Length, afterLength = newContent.Length }, ok: true);

        await userMsg.ModifyAsync(p => p.Content = newContent);
        return AuditAndReturn("message_content_edited", new { ok = true, dryRun = false, channelId, messageId, beforeLength = before.Length, afterLength = newContent.Length }, ok: true);
    }

    [McpServerTool, Description(
        "Delete a message in a channel or thread. Works on bot AND user messages (mod cleanup). Refuses to delete " +
        "messages from protected users. Returns a snapshot of what was deleted. Defaults to dryRun=true — irreversible once applied.")]
    public async Task<object> DeleteMessage(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Channel or thread ID (decimal string) the message is in.")] string channelId,
        [Description("Message ID (decimal string).")] string messageId,
        [Description("Audit-log reason. Optional but recommended for moderation.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(channelId, out var cid))
            throw new ArgumentException("channelId must be a decimal string");
        if (!ulong.TryParse(messageId, out var mid))
            throw new ArgumentException("messageId must be a decimal string");

        var ch = await ChannelByIdAsync(cid);
        if (ch is not IMessageChannel mc)
            return AuditAndReturn("message_deleted", new { ok = false, dryRun, channelId, messageId, reason = "channel_not_messageable" }, ok: false);

        var msg = await mc.GetMessageAsync(mid);
        if (msg == null)
            return AuditAndReturn("message_deleted", new { ok = false, dryRun, channelId, messageId, reason = "message_not_found" }, ok: false);
        if (await IsProtectedAsync(msg.Author.Id))
            return AuditAndReturn("message_deleted", new { ok = false, dryRun, channelId, messageId, reason = "protected_author", authorId = msg.Author.Id.ToString() }, ok: false);

        var snapshot = new
        {
            channelId,
            channelName = ch.Name,
            messageId,
            authorId = msg.Author.Id.ToString(),
            authorName = msg.Author.Username,
            contentLength = msg.Content?.Length ?? 0,
            contentPreview = msg.Content == null ? null : (msg.Content.Length > 200 ? msg.Content.Substring(0, 200) + "…" : msg.Content),
            embedCount = msg.Embeds?.Count ?? 0,
            attachmentCount = msg.Attachments?.Count ?? 0,
            postedAtUtc = msg.Timestamp.UtcDateTime,
        };

        if (dryRun)
            return AuditAndReturn("message_deleted", new { ok = true, dryRun = true, wouldDelete = snapshot, auditReason = reason }, ok: true);

        await msg.DeleteAsync(BuildOptions(reason));
        return AuditAndReturn("message_deleted", new { ok = true, dryRun = false, deleted = snapshot, auditReason = reason }, ok: true);
    }

    [McpServerTool, Description(
        "Edit the embed on a message previously posted by the bot. Pass newTitle / newDescription to overwrite those " +
        "fields; null/omitted are left alone. Other embed properties are preserved. Returns a before/after diff. Defaults to dryRun=true.")]
    public async Task<object> EditMessageEmbed(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Channel or thread ID (decimal string) the message is in.")] string channelId,
        [Description("Message ID (decimal string).")] string messageId,
        [Description("Index of the embed to edit (0-based). Default 0.")] int embedIndex = 0,
        [Description("New embed title. Pass null to leave unchanged.")] string? newTitle = null,
        [Description("New embed description. Pass null to leave unchanged.")] string? newDescription = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(channelId, out var cid))
            throw new ArgumentException("channelId must be a decimal string");
        if (!ulong.TryParse(messageId, out var mid))
            throw new ArgumentException("messageId must be a decimal string");
        if (newTitle == null && newDescription == null)
            throw new ArgumentException("at least one of newTitle / newDescription must be provided");

        var ch = await ChannelByIdAsync(cid);
        if (ch is not IMessageChannel mc)
            return AuditAndReturn("message_embed_edited", new { ok = false, dryRun, channelId, messageId, reason = "channel_not_messageable" }, ok: false);

        var msg = await mc.GetMessageAsync(mid);
        if (msg == null)
            return AuditAndReturn("message_embed_edited", new { ok = false, dryRun, channelId, messageId, reason = "message_not_found" }, ok: false);

        ulong botId = (await BotUserAsync())?.Id ?? 0;
        if (msg.Author.Id != botId)
            return AuditAndReturn("message_embed_edited", new { ok = false, dryRun, channelId, messageId, reason = "not_bot_authored", authorId = msg.Author.Id.ToString() }, ok: false);
        if (msg is not IUserMessage userMsg)
            return AuditAndReturn("message_embed_edited", new { ok = false, dryRun, channelId, messageId, reason = "not_a_user_message" }, ok: false);

        var embeds = msg.Embeds?.ToList() ?? new List<IEmbed>();
        if (embedIndex < 0 || embedIndex >= embeds.Count)
            return AuditAndReturn("message_embed_edited", new { ok = false, dryRun, channelId, messageId, reason = "embed_index_out_of_range", embedCount = embeds.Count }, ok: false);

        var src = embeds[embedIndex];
        string? finalTitle = newTitle ?? src.Title;
        string? finalDescription = newDescription ?? src.Description;

        var before = new { title = src.Title, description = src.Description };
        var after = new { title = finalTitle, description = finalDescription };

        if (dryRun)
            return AuditAndReturn("message_embed_edited", new { ok = true, dryRun = true, channelId, messageId, embedIndex, before, after }, ok: true);

        var rebuilt = RebuildEmbed(src, finalTitle, finalDescription);
        await userMsg.ModifyAsync(p => p.Embed = rebuilt);

        return AuditAndReturn("message_embed_edited", new { ok = true, dryRun = false, channelId, messageId, embedIndex, before, after }, ok: true);
    }

    // ── Invites: create / revoke ───────────────────────────────────────────────

    [McpServerTool, Description(
        "Create an invite for a channel. maxAgeSeconds=0 means never expires; maxUses=0 means unlimited. " +
        "isTemporary=true grants only temporary membership. isUnique=true forces a fresh invite. " +
        "Returns invite code + URL. Defaults to dryRun=true.")]
    public async Task<object> CreateInvite(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Channel ID (decimal string).")] string channelId,
        [Description("Max age in seconds (0 = never expires). Default 86400 (24h).")] int maxAgeSeconds = 86400,
        [Description("Max uses (0 = unlimited). Default 0.")] int maxUses = 0,
        [Description("Grant only temporary membership.")] bool isTemporary = false,
        [Description("Force a fresh invite even if a duplicate exists.")] bool isUnique = false,
        [Description("Audit-log reason. Optional.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(channelId, out var cid))
            throw new ArgumentException("channelId must be a decimal string");
        if (maxAgeSeconds < 0) throw new ArgumentException("maxAgeSeconds must be >= 0");
        if (maxUses < 0) throw new ArgumentException("maxUses must be >= 0");

        if (await ChannelByIdAsync(cid) is not INestedChannel nested)
            return AuditAndReturn("invite_created", new { ok = false, dryRun, channelId, reason = "channel_not_invitable" }, ok: false);

        var preview = new { channelId, channelName = nested.Name, maxAgeSeconds, maxUses, isTemporary, isUnique };
        if (dryRun)
            return AuditAndReturn("invite_created", new { ok = true, dryRun = true, wouldCreate = preview, auditReason = reason }, ok: true);

        var inv = await nested.CreateInviteAsync(maxAge: maxAgeSeconds, maxUses: maxUses == 0 ? (int?)null : maxUses,
            isTemporary: isTemporary, isUnique: isUnique, options: BuildOptions(reason));
        return AuditAndReturn("invite_created",
            new { ok = true, dryRun = false, code = inv.Code, url = inv.Url, channelId, channelName = nested.Name, maxAgeSeconds, maxUses, isTemporary, auditReason = reason }, ok: true);
    }

    [McpServerTool, Description("Revoke an invite by its code. Returns the revoked invite's metadata. Defaults to dryRun=true.")]
    public async Task<object> RevokeInvite(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Invite code (the part after discord.gg/).")] string code,
        [Description("Audit-log reason. Optional.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("code is required");

        var g = await GuildAsync(guildId);
        var invites = await g.GetInvitesAsync();
        var inv = invites.FirstOrDefault(i => string.Equals(i.Code, code, StringComparison.OrdinalIgnoreCase));
        if (inv == null)
            return AuditAndReturn("invite_revoked", new { ok = false, dryRun, code, reason = "invite_not_found" }, ok: false);

        var snapshot = new
        {
            code = inv.Code,
            channelId = inv.ChannelId.ToString(),
            channelName = inv.ChannelName,
            uses = inv.Uses,
            maxUses = inv.MaxUses,
            inviterId = inv.Inviter?.Id.ToString(),
            inviterName = inv.Inviter?.Username,
        };

        if (dryRun)
            return AuditAndReturn("invite_revoked", new { ok = true, dryRun = true, wouldRevoke = snapshot, auditReason = reason }, ok: true);

        await inv.DeleteAsync(BuildOptions(reason));
        return AuditAndReturn("invite_revoked", new { ok = true, dryRun = false, revoked = snapshot, auditReason = reason }, ok: true);
    }

    // ── Emojis: create / rename / delete ───────────────────────────────────────

    [McpServerTool, Description(
        "Upload a custom emoji to the guild. imageUrl must point to a PNG/GIF/JPG ≤256 KB. name must be 2–32 chars, " +
        "alphanumerics + underscore. Defaults to dryRun=true.")]
    public async Task<object> CreateEmoji(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Emoji name (2–32 chars, [a-zA-Z0-9_]).")] string name,
        [Description("HTTP(S) URL of the image.")] string imageUrl,
        [Description("Audit-log reason. Optional.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("name is required");
        if (string.IsNullOrWhiteSpace(imageUrl))
            throw new ArgumentException("imageUrl is required");
        if (!Regex.IsMatch(name, "^[A-Za-z0-9_]{2,32}$"))
            return AuditAndReturn("emoji_created", new { ok = false, dryRun, name, reason = "invalid_name" }, ok: false);

        var g = await GuildAsync(guildId);

        if (dryRun)
            return AuditAndReturn("emoji_created", new { ok = true, dryRun = true, wouldCreate = new { name, imageUrl }, auditReason = reason }, ok: true);

        byte[] bytes;
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
            bytes = await http.GetByteArrayAsync(imageUrl);
        if (bytes.Length > 256 * 1024)
            return AuditAndReturn("emoji_created", new { ok = false, dryRun = false, name, reason = "image_too_large", sizeBytes = bytes.Length }, ok: false);

        using var ms = new MemoryStream(bytes);
        var image = new Image(ms);
        var emote = await g.CreateEmoteAsync(name, image, options: BuildOptions(reason));
        return AuditAndReturn("emoji_created",
            new { ok = true, dryRun = false, emojiId = emote.Id.ToString(), name = emote.Name, animated = emote.Animated, auditReason = reason }, ok: true);
    }

    [McpServerTool, Description("Rename an existing custom emoji. Defaults to dryRun=true.")]
    public async Task<object> RenameEmoji(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Emoji ID (decimal string).")] string emojiId,
        [Description("New name (2–32 chars, [a-zA-Z0-9_]).")] string newName,
        [Description("Audit-log reason. Optional.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(emojiId, out var eid))
            throw new ArgumentException("emojiId must be a decimal string");
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("newName is required");
        if (!Regex.IsMatch(newName, "^[A-Za-z0-9_]{2,32}$"))
            return AuditAndReturn("emoji_renamed", new { ok = false, dryRun, emojiId, reason = "invalid_name" }, ok: false);

        var g = await GuildAsync(guildId);
        var emotes = await g.GetEmotesAsync();
        var emote = emotes.FirstOrDefault(e => e.Id == eid);
        if (emote == null)
            return AuditAndReturn("emoji_renamed", new { ok = false, dryRun, emojiId, reason = "emoji_not_found" }, ok: false);

        string before = emote.Name;
        if (string.Equals(before, newName, StringComparison.Ordinal))
            return AuditAndReturn("emoji_renamed", new { ok = true, dryRun, emojiId, before, after = newName, noChange = true }, ok: true);

        if (dryRun)
            return AuditAndReturn("emoji_renamed", new { ok = true, dryRun = true, emojiId, before, after = newName, auditReason = reason }, ok: true);

        await g.ModifyEmoteAsync(emote, p => p.Name = newName, BuildOptions(reason));
        return AuditAndReturn("emoji_renamed", new { ok = true, dryRun = false, emojiId, before, after = newName, auditReason = reason }, ok: true);
    }

    [McpServerTool, Description("Delete a custom emoji from the guild. Defaults to dryRun=true — irreversible once applied.")]
    public async Task<object> DeleteEmoji(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Emoji ID (decimal string).")] string emojiId,
        [Description("Audit-log reason. Optional.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(emojiId, out var eid))
            throw new ArgumentException("emojiId must be a decimal string");

        var g = await GuildAsync(guildId);
        var emotes = await g.GetEmotesAsync();
        var emote = emotes.FirstOrDefault(e => e.Id == eid);
        if (emote == null)
            return AuditAndReturn("emoji_deleted", new { ok = false, dryRun, emojiId, reason = "emoji_not_found" }, ok: false);

        var snapshot = new { emojiId, name = emote.Name, animated = emote.Animated };

        if (dryRun)
            return AuditAndReturn("emoji_deleted", new { ok = true, dryRun = true, wouldDelete = snapshot, auditReason = reason }, ok: true);

        await g.DeleteEmoteAsync(emote, BuildOptions(reason));
        return AuditAndReturn("emoji_deleted", new { ok = true, dryRun = false, deleted = snapshot, auditReason = reason }, ok: true);
    }

    // ── Scheduled events: create / modify / cancel ─────────────────────────────

    [McpServerTool, Description(
        "Create a guild scheduled event. type accepts 'voice', 'stage', or 'external'. For 'external', endUtc and location " +
        "are REQUIRED. For 'voice'/'stage', channelId is REQUIRED and location must be empty. Times are ISO 8601 UTC. Defaults to dryRun=true.")]
    public async Task<object> CreateScheduledEvent(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Event name (1–100 chars).")] string name,
        [Description("'voice', 'stage', or 'external'.")] string type,
        [Description("Start time as ISO 8601 UTC.")] string startUtc,
        [Description("Description / details. Optional.")] string? description = null,
        [Description("End time as ISO 8601 UTC. Required for external events.")] string? endUtc = null,
        [Description("Voice/stage channel ID. Required for voice/stage.")] string? channelId = null,
        [Description("Location string. Required for external.")] string? location = null,
        [Description("Audit-log reason. Optional.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("name is required");
        if (!DateTimeOffset.TryParse(startUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start))
            throw new ArgumentException("startUtc must be ISO 8601");
        DateTimeOffset? end = null;
        if (!string.IsNullOrWhiteSpace(endUtc))
        {
            if (!DateTimeOffset.TryParse(endUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var e))
                throw new ArgumentException("endUtc must be ISO 8601");
            end = e;
        }

        ulong? cid = null;
        if (!string.IsNullOrWhiteSpace(channelId))
        {
            if (!ulong.TryParse(channelId, out var c))
                throw new ArgumentException("channelId must be a decimal string");
            cid = c;
        }

        string? kind = type?.Trim().ToLowerInvariant();
        GuildScheduledEventType eventType;
        switch (kind)
        {
            case "voice":
                eventType = GuildScheduledEventType.Voice;
                if (!cid.HasValue) return AuditAndReturn("scheduled_event_created", new { ok = false, dryRun, reason = "channelId_required_for_voice" }, ok: false);
                if (!string.IsNullOrWhiteSpace(location)) return AuditAndReturn("scheduled_event_created", new { ok = false, dryRun, reason = "location_not_allowed_for_voice" }, ok: false);
                break;
            case "stage":
                eventType = GuildScheduledEventType.Stage;
                if (!cid.HasValue) return AuditAndReturn("scheduled_event_created", new { ok = false, dryRun, reason = "channelId_required_for_stage" }, ok: false);
                if (!string.IsNullOrWhiteSpace(location)) return AuditAndReturn("scheduled_event_created", new { ok = false, dryRun, reason = "location_not_allowed_for_stage" }, ok: false);
                break;
            case "external":
                eventType = GuildScheduledEventType.External;
                if (!end.HasValue) return AuditAndReturn("scheduled_event_created", new { ok = false, dryRun, reason = "endUtc_required_for_external" }, ok: false);
                if (string.IsNullOrWhiteSpace(location)) return AuditAndReturn("scheduled_event_created", new { ok = false, dryRun, reason = "location_required_for_external" }, ok: false);
                if (cid.HasValue) return AuditAndReturn("scheduled_event_created", new { ok = false, dryRun, reason = "channelId_not_allowed_for_external" }, ok: false);
                break;
            default:
                return AuditAndReturn("scheduled_event_created", new { ok = false, dryRun, reason = "invalid_type" }, ok: false);
        }

        var preview = new { name, type = kind, startUtc = start.UtcDateTime, endUtc = end?.UtcDateTime, channelId, location, description };
        if (dryRun)
            return AuditAndReturn("scheduled_event_created", new { ok = true, dryRun = true, wouldCreate = preview, auditReason = reason }, ok: true);

        var g = await GuildAsync(guildId);
        var ev = await g.CreateEventAsync(
            name: name,
            startTime: start,
            type: eventType,
            privacyLevel: GuildScheduledEventPrivacyLevel.Private,
            description: description,
            endTime: end,
            channelId: cid,
            location: location,
            options: BuildOptions(reason));

        return AuditAndReturn("scheduled_event_created",
            new { ok = true, dryRun = false, eventId = ev.Id.ToString(), name = ev.Name, type = kind, startUtc = ev.StartTime.UtcDateTime, endUtc = ev.EndTime?.UtcDateTime, auditReason = reason }, ok: true);
    }

    [McpServerTool, Description(
        "Modify a guild scheduled event. Pass only the fields you want to change. newStatus accepts 'scheduled', 'active', " +
        "'completed', 'cancelled'. Discord enforces valid transitions. Times are ISO 8601 UTC. Defaults to dryRun=true.")]
    public async Task<object> ModifyScheduledEvent(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Event ID (decimal string).")] string eventId,
        [Description("New name. Optional.")] string? newName = null,
        [Description("New description. Pass empty string to clear. Optional.")] string? newDescription = null,
        [Description("New start time as ISO 8601 UTC. Optional.")] string? newStartUtc = null,
        [Description("New end time as ISO 8601 UTC. Optional.")] string? newEndUtc = null,
        [Description("New location string (external events). Optional.")] string? newLocation = null,
        [Description("New voice/stage channel ID (decimal string). Optional.")] string? newChannelId = null,
        [Description("New status: 'scheduled', 'active', 'completed', 'cancelled'. Optional.")] string? newStatus = null,
        [Description("Audit-log reason. Optional.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(eventId, out var eid))
            throw new ArgumentException("eventId must be a decimal string");

        DateTimeOffset? start = null;
        if (!string.IsNullOrWhiteSpace(newStartUtc))
        {
            if (!DateTimeOffset.TryParse(newStartUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var s))
                throw new ArgumentException("newStartUtc must be ISO 8601");
            start = s;
        }
        DateTimeOffset? end = null;
        if (!string.IsNullOrWhiteSpace(newEndUtc))
        {
            if (!DateTimeOffset.TryParse(newEndUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var e))
                throw new ArgumentException("newEndUtc must be ISO 8601");
            end = e;
        }
        ulong? cid = null;
        if (!string.IsNullOrWhiteSpace(newChannelId))
        {
            if (!ulong.TryParse(newChannelId, out var c))
                throw new ArgumentException("newChannelId must be a decimal string");
            cid = c;
        }

        GuildScheduledEventStatus? status = null;
        if (!string.IsNullOrWhiteSpace(newStatus))
        {
            switch (newStatus.Trim().ToLowerInvariant())
            {
                case "scheduled": status = GuildScheduledEventStatus.Scheduled; break;
                case "active": status = GuildScheduledEventStatus.Active; break;
                case "completed": status = GuildScheduledEventStatus.Completed; break;
                case "cancelled": case "canceled": status = GuildScheduledEventStatus.Cancelled; break;
                default:
                    return AuditAndReturn("scheduled_event_modified", new { ok = false, dryRun, eventId, reason = "invalid_status" }, ok: false);
            }
        }

        var g = await GuildAsync(guildId);
        var ev = await g.GetEventAsync(eid);
        if (ev == null)
            return AuditAndReturn("scheduled_event_modified", new { ok = false, dryRun, eventId, reason = "event_not_found" }, ok: false);

        var before = new
        {
            name = ev.Name,
            description = ev.Description,
            startUtc = ev.StartTime.UtcDateTime,
            endUtc = ev.EndTime?.UtcDateTime,
            location = ev.Location,
            channelId = ev.ChannelId?.ToString(),
            status = ev.Status.ToString(),
        };
        var after = new
        {
            name = newName ?? before.name,
            description = newDescription ?? before.description,
            startUtc = start?.UtcDateTime ?? before.startUtc,
            endUtc = end?.UtcDateTime ?? before.endUtc,
            location = newLocation ?? before.location,
            channelId = cid?.ToString() ?? before.channelId,
            status = (status?.ToString()) ?? before.status,
        };

        if (dryRun)
            return AuditAndReturn("scheduled_event_modified", new { ok = true, dryRun = true, eventId, before, after, auditReason = reason }, ok: true);

        await ev.ModifyAsync(p =>
        {
            if (newName != null) p.Name = newName;
            if (newDescription != null) p.Description = newDescription;
            if (start.HasValue) p.StartTime = start.Value;
            if (end.HasValue) p.EndTime = end.Value;
            if (newLocation != null) p.Location = newLocation;
            if (cid.HasValue) p.ChannelId = cid.Value;
            if (status.HasValue) p.Status = status.Value;
        }, BuildOptions(reason));

        return AuditAndReturn("scheduled_event_modified", new { ok = true, dryRun = false, eventId, before, after, auditReason = reason }, ok: true);
    }

    [McpServerTool, Description("Cancel (delete) a guild scheduled event. Defaults to dryRun=true.")]
    public async Task<object> CancelScheduledEvent(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Event ID (decimal string).")] string eventId,
        [Description("Audit-log reason. Optional.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(eventId, out var eid))
            throw new ArgumentException("eventId must be a decimal string");

        var g = await GuildAsync(guildId);
        var ev = await g.GetEventAsync(eid);
        if (ev == null)
            return AuditAndReturn("scheduled_event_cancelled", new { ok = false, dryRun, eventId, reason = "event_not_found" }, ok: false);

        var snapshot = new { eventId, name = ev.Name, startUtc = ev.StartTime.UtcDateTime, status = ev.Status.ToString() };
        if (dryRun)
            return AuditAndReturn("scheduled_event_cancelled", new { ok = true, dryRun = true, wouldCancel = snapshot, auditReason = reason }, ok: true);

        await ev.DeleteAsync(BuildOptions(reason));
        return AuditAndReturn("scheduled_event_cancelled", new { ok = true, dryRun = false, cancelled = snapshot, auditReason = reason }, ok: true);
    }

    // ── Stickers: create / modify / delete ─────────────────────────────────────

    [McpServerTool, Description(
        "Upload a custom sticker to the guild. imageUrl must point to PNG/APNG/Lottie ≤512 KB. name 2–30 chars, " +
        "description 2–100 chars, tags 1–200 chars. Defaults to dryRun=true.")]
    public async Task<object> CreateSticker(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Sticker name (2–30 chars).")] string name,
        [Description("Sticker description (2–100 chars).")] string description,
        [Description("Sticker tags (1–200 chars, used for search).")] string tags,
        [Description("HTTP(S) URL of the image (PNG/APNG/Lottie ≤512 KB).")] string imageUrl,
        [Description("Audit-log reason. Optional.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length < 2 || name.Length > 30)
            return AuditAndReturn("sticker_created", new { ok = false, dryRun, reason = "invalid_name" }, ok: false);
        if (string.IsNullOrWhiteSpace(description) || description.Length < 2 || description.Length > 100)
            return AuditAndReturn("sticker_created", new { ok = false, dryRun, reason = "invalid_description" }, ok: false);
        if (string.IsNullOrWhiteSpace(tags) || tags.Length > 200)
            return AuditAndReturn("sticker_created", new { ok = false, dryRun, reason = "invalid_tags" }, ok: false);
        if (string.IsNullOrWhiteSpace(imageUrl))
            throw new ArgumentException("imageUrl is required");

        var g = await GuildAsync(guildId);

        if (dryRun)
            return AuditAndReturn("sticker_created", new { ok = true, dryRun = true, wouldCreate = new { name, description, tags, imageUrl }, auditReason = reason }, ok: true);

        byte[] bytes;
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
            bytes = await http.GetByteArrayAsync(imageUrl);
        if (bytes.Length > 512 * 1024)
            return AuditAndReturn("sticker_created", new { ok = false, dryRun = false, name, reason = "image_too_large", sizeBytes = bytes.Length }, ok: false);

        using var ms = new MemoryStream(bytes);
        string fileName = "sticker" + GuessImageExtension(bytes);
        var tagList = tags.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).Where(t => t.Length > 0).ToArray();
        var sticker = await g.CreateStickerAsync(name, ms, fileName, tagList, description, options: BuildOptions(reason));
        return AuditAndReturn("sticker_created",
            new { ok = true, dryRun = false, stickerId = sticker.Id.ToString(), name = sticker.Name, auditReason = reason }, ok: true);
    }

    [McpServerTool, Description(
        "Modify a custom sticker (rename / change description / change tags). Pass only the fields you want to change. " +
        "Returns before/after diff. Defaults to dryRun=true.")]
    public async Task<object> ModifySticker(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Sticker ID (decimal string).")] string stickerId,
        [Description("New name (2–30 chars). Optional.")] string? newName = null,
        [Description("New description (2–100 chars). Optional.")] string? newDescription = null,
        [Description("New tags (1–200 chars). Optional.")] string? newTags = null,
        [Description("Audit-log reason. Optional.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(stickerId, out var sid))
            throw new ArgumentException("stickerId must be a decimal string");
        if (newName == null && newDescription == null && newTags == null)
            throw new ArgumentException("at least one of newName / newDescription / newTags must be provided");

        var g = await GuildAsync(guildId);
        var sticker = await g.GetStickerAsync(sid);
        if (sticker == null)
            return AuditAndReturn("sticker_modified", new { ok = false, dryRun, stickerId, reason = "sticker_not_found" }, ok: false);

        var before = new { name = sticker.Name, description = sticker.Description, tags = string.Join(",", sticker.Tags ?? new List<string>()) };
        var after = new
        {
            name = newName ?? before.name,
            description = newDescription ?? before.description,
            tags = newTags ?? before.tags,
        };

        if (dryRun)
            return AuditAndReturn("sticker_modified", new { ok = true, dryRun = true, stickerId, before, after, auditReason = reason }, ok: true);

        await sticker.ModifyAsync(p =>
        {
            if (newName != null) p.Name = newName;
            if (newDescription != null) p.Description = newDescription;
            if (newTags != null)
                p.Tags = newTags.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).Where(t => t.Length > 0).ToArray();
        }, BuildOptions(reason));

        return AuditAndReturn("sticker_modified", new { ok = true, dryRun = false, stickerId, before, after, auditReason = reason }, ok: true);
    }

    [McpServerTool, Description("Delete a custom sticker. Defaults to dryRun=true — irreversible once applied.")]
    public async Task<object> DeleteSticker(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Sticker ID (decimal string).")] string stickerId,
        [Description("Audit-log reason. Optional.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(stickerId, out var sid))
            throw new ArgumentException("stickerId must be a decimal string");

        var g = await GuildAsync(guildId);
        var sticker = await g.GetStickerAsync(sid);
        if (sticker == null)
            return AuditAndReturn("sticker_deleted", new { ok = false, dryRun, stickerId, reason = "sticker_not_found" }, ok: false);

        var snapshot = new { stickerId, name = sticker.Name };
        if (dryRun)
            return AuditAndReturn("sticker_deleted", new { ok = true, dryRun = true, wouldDelete = snapshot, auditReason = reason }, ok: true);

        await sticker.DeleteAsync(BuildOptions(reason));
        return AuditAndReturn("sticker_deleted", new { ok = true, dryRun = false, deleted = snapshot, auditReason = reason }, ok: true);
    }

    // ── Webhooks: create / delete ──────────────────────────────────────────────

    [McpServerTool, Description(
        "Create a webhook on a text channel. Returns webhook id and url (the url contains the secret token — treat as a " +
        "credential). Defaults to dryRun=true.")]
    public async Task<object> CreateWebhook(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Channel ID (decimal string).")] string channelId,
        [Description("Webhook name.")] string name,
        [Description("Audit-log reason. Optional.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(channelId, out var cid))
            throw new ArgumentException("channelId must be a decimal string");
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("name is required");

        if (await ChannelByIdAsync(cid) is not ITextChannel ch)
            return AuditAndReturn("webhook_created", new { ok = false, dryRun, channelId, reason = "channel_not_text" }, ok: false);

        if (dryRun)
            return AuditAndReturn("webhook_created", new { ok = true, dryRun = true, channelId, channelName = ch.Name, name, auditReason = reason }, ok: true);

        var wh = await ch.CreateWebhookAsync(name, options: BuildOptions(reason));
        // Note: the url contains the secret token — log with care.
        string url = $"https://discord.com/api/webhooks/{wh.Id}/{wh.Token}";
        return AuditAndReturn("webhook_created",
            new { ok = true, dryRun = false, webhookId = wh.Id.ToString(), name = wh.Name, channelId, channelName = ch.Name, url, auditReason = reason }, ok: true);
    }

    [McpServerTool, Description("Delete a webhook by id. Defaults to dryRun=true — irreversible once applied.")]
    public async Task<object> DeleteWebhook(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Webhook ID (decimal string).")] string webhookId,
        [Description("Audit-log reason. Optional.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(webhookId, out var wid))
            throw new ArgumentException("webhookId must be a decimal string");

        var g = await GuildAsync(guildId);
        var wh = await g.GetWebhookAsync(wid);
        if (wh == null)
            return AuditAndReturn("webhook_deleted", new { ok = false, dryRun, webhookId, reason = "webhook_not_found" }, ok: false);

        var snapshot = new { webhookId, name = wh.Name, channelId = wh.ChannelId.ToString() };
        if (dryRun)
            return AuditAndReturn("webhook_deleted", new { ok = true, dryRun = true, wouldDelete = snapshot, auditReason = reason }, ok: true);

        await wh.DeleteAsync(BuildOptions(reason));
        return AuditAndReturn("webhook_deleted", new { ok = true, dryRun = false, deleted = snapshot, auditReason = reason }, ok: true);
    }

    // ── Channels: create / modify / delete ─────────────────────────────────────

    [McpServerTool, Description(
        "Create a channel in the guild. type accepts 'text', 'voice', 'category', 'forum', 'news'. categoryId optional " +
        "(ignored when type=category). topic / slowModeSeconds / isNsfw apply only to text/news/forum. Returns the new " +
        "channel id. Defaults to dryRun=true.")]
    public async Task<object> CreateChannel(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Channel name (1–100 chars).")] string name,
        [Description("'text', 'voice', 'category', 'forum', or 'news'.")] string type,
        [Description("Category ID to place the channel under. Optional.")] string? categoryId = null,
        [Description("Channel topic (text/news/forum only). Optional.")] string? topic = null,
        [Description("Slowmode in seconds, 0–21600 (text/forum only). Default 0.")] int slowModeSeconds = 0,
        [Description("Mark as NSFW (text/news/forum only).")] bool isNsfw = false,
        [Description("Audit-log reason. Optional.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("name is required");

        ulong? catId = null;
        if (!string.IsNullOrWhiteSpace(categoryId))
        {
            if (!ulong.TryParse(categoryId, out var c))
                throw new ArgumentException("categoryId must be a decimal string");
            catId = c;
        }
        if (slowModeSeconds < 0 || slowModeSeconds > 21600)
            return AuditAndReturn("channel_created", new { ok = false, dryRun, reason = "invalid_slowModeSeconds" }, ok: false);

        var g = await GuildAsync(guildId);
        string? kind = type?.Trim().ToLowerInvariant();
        if (kind != "text" && kind != "voice" && kind != "category" && kind != "forum" && kind != "news")
            return AuditAndReturn("channel_created", new { ok = false, dryRun, reason = "invalid_type" }, ok: false);

        var preview = new { name, type = kind, categoryId, topic, slowModeSeconds, isNsfw };
        if (dryRun)
            return AuditAndReturn("channel_created", new { ok = true, dryRun = true, wouldCreate = preview, auditReason = reason }, ok: true);

        var opts = BuildOptions(reason);
        ulong newId;
        string newName;
        switch (kind)
        {
            case "text":
                var t = await g.CreateTextChannelAsync(name, p =>
                {
                    if (catId.HasValue) p.CategoryId = catId.Value;
                    if (topic != null) p.Topic = topic;
                    if (slowModeSeconds > 0) p.SlowModeInterval = slowModeSeconds;
                    p.IsNsfw = isNsfw;
                }, opts);
                newId = t.Id; newName = t.Name; break;
            case "voice":
                var v = await g.CreateVoiceChannelAsync(name, p =>
                {
                    if (catId.HasValue) p.CategoryId = catId.Value;
                }, opts);
                newId = v.Id; newName = v.Name; break;
            case "category":
                var c = await g.CreateCategoryChannelAsync(name, options: opts);
                newId = c.Id; newName = c.Name; break;
            case "forum":
                var f = await g.CreateForumChannelAsync(name, p =>
                {
                    if (catId.HasValue) p.CategoryId = catId.Value;
                    if (topic != null) p.Topic = topic;
                    if (slowModeSeconds > 0) p.ThreadCreationInterval = slowModeSeconds;
                    p.IsNsfw = isNsfw;
                }, opts);
                newId = f.Id; newName = f.Name; break;
            case "news":
                var n = await g.CreateNewsChannelAsync(name, p =>
                {
                    if (catId.HasValue) p.CategoryId = catId.Value;
                    if (topic != null) p.Topic = topic;
                    p.IsNsfw = isNsfw;
                }, opts);
                newId = n.Id; newName = n.Name; break;
            default:
                return AuditAndReturn("channel_created", new { ok = false, dryRun, reason = "invalid_type" }, ok: false);
        }

        return AuditAndReturn("channel_created",
            new { ok = true, dryRun = false, channelId = newId.ToString(), name = newName, type = kind, auditReason = reason }, ok: true);
    }

    [McpServerTool, Description(
        "Modify a channel: rename, change topic, slowmode, NSFW flag, parent category, or position. Pass only the fields " +
        "you want to change. Returns before/after diff. Defaults to dryRun=true.")]
    public async Task<object> ModifyChannel(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Channel ID (decimal string).")] string channelId,
        [Description("New name. Optional.")] string? newName = null,
        [Description("New topic (text/news/forum channels). Pass empty string to clear. Optional.")] string? newTopic = null,
        [Description("New slowmode in seconds (text/forum), 0–21600. Optional.")] int? newSlowModeSeconds = null,
        [Description("New NSFW flag. Optional.")] bool? newIsNsfw = null,
        [Description("New parent category ID (decimal string), or empty string to clear. Optional.")] string? newCategoryId = null,
        [Description("New position (integer). Optional.")] int? newPosition = null,
        [Description("Audit-log reason. Optional.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(channelId, out var cid))
            throw new ArgumentException("channelId must be a decimal string");
        if (newSlowModeSeconds.HasValue && (newSlowModeSeconds.Value < 0 || newSlowModeSeconds.Value > 21600))
            return AuditAndReturn("channel_modified", new { ok = false, dryRun, channelId, reason = "invalid_slowModeSeconds" }, ok: false);

        ulong? targetCategoryId = null;
        bool clearCategory = false;
        if (newCategoryId != null)
        {
            if (newCategoryId.Length == 0) clearCategory = true;
            else
            {
                if (!ulong.TryParse(newCategoryId, out var ncid))
                    throw new ArgumentException("newCategoryId must be a decimal string or empty");
                targetCategoryId = ncid;
            }
        }

        if (await ChannelByIdAsync(cid) is not IGuildChannel gch)
            return AuditAndReturn("channel_modified", new { ok = false, dryRun, channelId, reason = "channel_not_found" }, ok: false);

        var asText = gch as ITextChannel;
        var asNested = gch as INestedChannel;

        var before = new
        {
            name = gch.Name,
            topic = asText?.Topic,
            slowModeSeconds = asText?.SlowModeInterval ?? 0,
            isNsfw = asText?.IsNsfw ?? false,
            categoryId = asNested?.CategoryId?.ToString(),
            position = gch.Position,
        };
        var after = new
        {
            name = newName ?? before.name,
            topic = newTopic ?? before.topic,
            slowModeSeconds = newSlowModeSeconds ?? before.slowModeSeconds,
            isNsfw = newIsNsfw ?? before.isNsfw,
            categoryId = clearCategory ? null : (targetCategoryId?.ToString() ?? before.categoryId),
            position = newPosition ?? before.position,
        };

        if (dryRun)
            return AuditAndReturn("channel_modified", new { ok = true, dryRun = true, channelId, before, after, auditReason = reason }, ok: true);

        var opts = BuildOptions(reason);
        await gch.ModifyAsync(p =>
        {
            if (newName != null) p.Name = newName;
            if (newPosition.HasValue) p.Position = newPosition.Value;
            if (clearCategory) p.CategoryId = null;
            else if (targetCategoryId.HasValue) p.CategoryId = targetCategoryId.Value;
        }, opts);

        // Topic / slowmode / NSFW only apply to text-like channels.
        if (asText != null && (newTopic != null || newSlowModeSeconds.HasValue || newIsNsfw.HasValue))
        {
            await asText.ModifyAsync(p =>
            {
                if (newTopic != null) p.Topic = newTopic;
                if (newSlowModeSeconds.HasValue) p.SlowModeInterval = newSlowModeSeconds.Value;
                if (newIsNsfw.HasValue) p.IsNsfw = newIsNsfw.Value;
            }, opts);
        }

        return AuditAndReturn("channel_modified", new { ok = true, dryRun = false, channelId, before, after, auditReason = reason }, ok: true);
    }

    [McpServerTool, Description(
        "Delete a channel. Returns a snapshot of what was deleted. Defaults to dryRun=true — irreversible once applied.")]
    public async Task<object> DeleteChannel(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Channel ID (decimal string).")] string channelId,
        [Description("Audit-log reason. Optional but recommended.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(channelId, out var cid))
            throw new ArgumentException("channelId must be a decimal string");

        if (await ChannelByIdAsync(cid) is not IGuildChannel gch)
            return AuditAndReturn("channel_deleted", new { ok = false, dryRun, channelId, reason = "channel_not_found" }, ok: false);

        // memberCount omitted vs. blueprint (socket-cache only). No protected-channel list:
        // dryRun is the safety net (the blueprint's Const-based list was Umbrella-specific).
        var snapshot = new
        {
            channelId,
            name = gch.Name,
            type = gch.GetType().Name,
            position = gch.Position,
        };

        if (dryRun)
            return AuditAndReturn("channel_deleted", new { ok = true, dryRun = true, wouldDelete = snapshot, auditReason = reason }, ok: true);

        await gch.DeleteAsync(BuildOptions(reason));
        return AuditAndReturn("channel_deleted", new { ok = true, dryRun = false, deleted = snapshot, auditReason = reason }, ok: true);
    }

    // ── Members: timeout / nickname / unban ────────────────────────────────────

    [McpServerTool, Description(
        "Apply a Discord timeout (communication disable) to a member. Duration is in minutes; Discord caps timeouts at " +
        "28 days (40320 minutes). Refuses to target the bot itself or any configured protected user. Defaults to dryRun=true.")]
    public async Task<object> TimeoutMember(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("User ID (decimal string).")] string userId,
        [Description("Timeout duration in minutes (1–40320).")] int durationMinutes,
        [Description("Audit-log reason. Optional but recommended.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(userId, out var uid))
            throw new ArgumentException("userId must be a decimal string");
        if (await IsProtectedAsync(uid))
            return AuditAndReturn("member_timeout_set", new { ok = false, dryRun, userId, reason = "protected_target" }, ok: false);
        if (durationMinutes < 1 || durationMinutes > 40320)
            return AuditAndReturn("member_timeout_set", new { ok = false, dryRun, userId, reason = "invalid_durationMinutes" }, ok: false);

        var g = await GuildAsync(guildId);
        var user = await g.GetUserAsync(uid);
        if (user == null)
            return AuditAndReturn("member_timeout_set", new { ok = false, dryRun, userId, reason = "user_not_in_guild" }, ok: false);

        var snapshot = new
        {
            userId,
            username = user.Username,
            globalName = user.GlobalName,
            durationMinutes,
            untilUtc = DateTime.UtcNow.AddMinutes(durationMinutes),
            previouslyTimedOutUntilUtc = user.TimedOutUntil?.UtcDateTime,
        };

        if (dryRun)
            return AuditAndReturn("member_timeout_set", new { ok = true, dryRun = true, wouldTimeout = snapshot, auditReason = reason }, ok: true);

        await user.SetTimeOutAsync(TimeSpan.FromMinutes(durationMinutes), BuildOptions(reason));
        return AuditAndReturn("member_timeout_set", new { ok = true, dryRun = false, applied = snapshot, auditReason = reason }, ok: true);
    }

    [McpServerTool, Description(
        "Clear an active timeout on a member. Idempotent — returns alreadyClear=true if no timeout was active. Defaults to dryRun=true.")]
    public async Task<object> ClearMemberTimeout(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("User ID (decimal string).")] string userId,
        [Description("Audit-log reason. Optional.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(userId, out var uid))
            throw new ArgumentException("userId must be a decimal string");

        var g = await GuildAsync(guildId);
        var user = await g.GetUserAsync(uid);
        if (user == null)
            return AuditAndReturn("member_timeout_cleared", new { ok = false, dryRun, userId, reason = "user_not_in_guild" }, ok: false);

        var until = user.TimedOutUntil;
        bool active = until.HasValue && until.Value > DateTimeOffset.UtcNow;
        if (!active)
            return AuditAndReturn("member_timeout_cleared", new { ok = true, dryRun, userId, username = user.Username, alreadyClear = true }, ok: true);

        if (dryRun)
            return AuditAndReturn("member_timeout_cleared", new { ok = true, dryRun = true, userId, username = user.Username, wasUntilUtc = until?.UtcDateTime, auditReason = reason }, ok: true);

        await user.RemoveTimeOutAsync(BuildOptions(reason));
        return AuditAndReturn("member_timeout_cleared", new { ok = true, dryRun = false, userId, username = user.Username, wasUntilUtc = until?.UtcDateTime, auditReason = reason }, ok: true);
    }

    [McpServerTool, Description(
        "Set or clear a member's server nickname. Pass nickname='' (empty string) to clear it. Refuses to target the bot " +
        "itself or any configured protected user. Returns before/after. Defaults to dryRun=true.")]
    public async Task<object> SetMemberNickname(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("User ID (decimal string).")] string userId,
        [Description("New nickname (1–32 chars), or empty string to clear.")] string nickname,
        [Description("Audit-log reason. Optional.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(userId, out var uid))
            throw new ArgumentException("userId must be a decimal string");
        if (await IsProtectedAsync(uid))
            return AuditAndReturn("member_nickname_set", new { ok = false, dryRun, userId, reason = "protected_target" }, ok: false);
        if (nickname == null)
            throw new ArgumentException("nickname is required (use empty string to clear)");
        if (nickname.Length > 32)
            return AuditAndReturn("member_nickname_set", new { ok = false, dryRun, userId, reason = "nickname_too_long" }, ok: false);

        var g = await GuildAsync(guildId);
        var user = await g.GetUserAsync(uid);
        if (user == null)
            return AuditAndReturn("member_nickname_set", new { ok = false, dryRun, userId, reason = "user_not_in_guild" }, ok: false);

        string? before = user.Nickname;
        string? after = nickname.Length == 0 ? null : nickname;
        bool noChange = string.Equals(before ?? "", after ?? "", StringComparison.Ordinal);
        if (noChange)
            return AuditAndReturn("member_nickname_set", new { ok = true, dryRun, userId, username = user.Username, before, after, noChange = true }, ok: true);

        if (dryRun)
            return AuditAndReturn("member_nickname_set", new { ok = true, dryRun = true, userId, username = user.Username, before, after, auditReason = reason }, ok: true);

        await user.ModifyAsync(p => p.Nickname = after, BuildOptions(reason));
        return AuditAndReturn("member_nickname_set", new { ok = true, dryRun = false, userId, username = user.Username, before, after, auditReason = reason }, ok: true);
    }

    [McpServerTool, Description(
        "Lift a ban — the user can rejoin via invite. Idempotent; returns alreadyAbsent=true if no ban exists. Defaults to dryRun=true.")]
    public async Task<object> UnbanMember(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("User ID (decimal string).")] string userId,
        [Description("Audit-log reason. Optional.")] string? reason = null,
        [Description("If true (default), simulate without changing Discord.")] bool dryRun = true)
    {
        if (!ulong.TryParse(userId, out var uid))
            throw new ArgumentException("userId must be a decimal string");

        var g = await GuildAsync(guildId);
        IBan? ban = null;
        try { ban = await g.GetBanAsync(uid); }
        catch { ban = null; }

        if (ban == null)
            return AuditAndReturn("member_unbanned", new { ok = true, dryRun, userId, alreadyAbsent = true }, ok: true);

        var snapshot = new { userId, username = ban.User?.Username, bannedReason = ban.Reason };

        if (dryRun)
            return AuditAndReturn("member_unbanned", new { ok = true, dryRun = true, wouldUnban = snapshot, auditReason = reason }, ok: true);

        await g.RemoveBanAsync(uid, BuildOptions(reason));
        return AuditAndReturn("member_unbanned", new { ok = true, dryRun = false, unbanned = snapshot, auditReason = reason }, ok: true);
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private object AuditAndReturn(string action, object payload, bool ok)
    {
        _audit.Log(action, payload, ok: ok, actor: _ctx.Fingerprint);
        return payload;
    }

    private static RequestOptions? BuildOptions(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? null : new RequestOptions { AuditLogReason = reason };

    /// <summary>
    /// Send content that may exceed Discord's 2000-char limit, splitting it into
    /// multiple messages. The embed is attached to the final message only, so it
    /// renders once at the end. Returns the last posted message and the chunk count.
    /// </summary>
    private static async Task<(IUserMessage last, int count)> SendChunkedAsync(
        IMessageChannel mc, string? content, Embed? embed, AllowedMentions allowed)
    {
        if (string.IsNullOrEmpty(content))
        {
            var only = await mc.SendMessageAsync(embed: embed, allowedMentions: allowed);
            return (only, 1);
        }

        var chunks = MessageChunker.Chunk(content).ToList();
        IUserMessage? last = null;
        for (int i = 0; i < chunks.Count; i++)
        {
            var e = i == chunks.Count - 1 ? embed : null; // embed on the last chunk only
            last = await mc.SendMessageAsync(text: chunks[i], embed: e, allowedMentions: allowed);
        }
        return (last!, chunks.Count);
    }

    private static Embed BuildEmbed(string? title, string? description, string? colorHex, string? footer, string? imageUrl, string? thumbnailUrl)
    {
        var eb = new EmbedBuilder();
        if (!string.IsNullOrWhiteSpace(title)) eb.WithTitle(title);
        if (!string.IsNullOrWhiteSpace(description)) eb.WithDescription(description);
        var color = ParseColor(colorHex);
        if (color.HasValue) eb.WithColor(color.Value);
        if (!string.IsNullOrWhiteSpace(footer)) eb.WithFooter(footer);
        if (!string.IsNullOrWhiteSpace(imageUrl)) eb.WithImageUrl(imageUrl);
        if (!string.IsNullOrWhiteSpace(thumbnailUrl)) eb.WithThumbnailUrl(thumbnailUrl);
        return eb.Build();
    }

    private static Embed RebuildEmbed(IEmbed src, string? title, string? description)
    {
        var eb = new EmbedBuilder()
            .WithTitle(title)
            .WithDescription(description)
            .WithUrl(src.Url);

        if (src.Color.HasValue) eb.WithColor(src.Color.Value);
        if (src.Timestamp.HasValue) eb.WithTimestamp(src.Timestamp.Value);
        if (src.Footer.HasValue)
            eb.WithFooter(src.Footer.Value.Text, src.Footer.Value.IconUrl);
        if (src.Author.HasValue)
            eb.WithAuthor(src.Author.Value.Name, src.Author.Value.IconUrl, src.Author.Value.Url);
        if (src.Thumbnail.HasValue && !string.IsNullOrEmpty(src.Thumbnail.Value.Url))
            eb.WithThumbnailUrl(src.Thumbnail.Value.Url);
        if (src.Image.HasValue && !string.IsNullOrEmpty(src.Image.Value.Url))
            eb.WithImageUrl(src.Image.Value.Url);
        foreach (var f in src.Fields)
            eb.AddField(f.Name, f.Value, f.Inline);

        return eb.Build();
    }

    private static Color? ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var s = hex.Trim().TrimStart('#');
        if (uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw))
            return new Color(raw);
        throw new ArgumentException($"colorHex '{hex}' must be RRGGBB or #RRGGBB");
    }

    private static string ToColorHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static ulong ParsePermissionFlags(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return 0UL;
        ulong mask = 0UL;
        foreach (var raw in csv.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = raw.Trim();
            if (name.Length == 0) continue;
            if (!Enum.TryParse<ChannelPermission>(name, ignoreCase: true, out var flag))
                throw new ArgumentException($"unknown ChannelPermission '{name}'");
            mask |= (ulong)flag;
        }
        return mask;
    }

    private static IEmote? ParseEmote(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var trimmed = s.Trim();
        if (Emote.TryParse(trimmed, out var custom)) return custom;
        try { return new Emoji(trimmed); }
        catch { return null; }
    }

    private static string PermissionMaskToString(ulong mask)
    {
        if (mask == 0UL) return "";
        var names = new List<string>();
        foreach (ChannelPermission flag in Enum.GetValues(typeof(ChannelPermission)))
        {
            ulong f = (ulong)flag;
            if (f == 0UL) continue;
            if ((mask & f) == f) names.Add(flag.ToString());
        }
        return string.Join(",", names);
    }

    private static string GuessImageExtension(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return ".png";
        if (bytes.Length >= 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) return ".gif";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ".jpg";
        if (bytes.Length >= 1 && bytes[0] == (byte)'{') return ".json";
        return ".png";
    }
}
