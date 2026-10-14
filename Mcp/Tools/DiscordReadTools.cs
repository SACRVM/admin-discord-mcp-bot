using System.ComponentModel;
using System.Globalization;
using AdminDiscordMcp.Discord;
using Discord;
using Discord.Rest;
using ModelContextProtocol.Server;

namespace AdminDiscordMcp.Mcp.Tools;

/// <summary>
/// Read-only Discord lookups over the REST API. Every tool takes an explicit
/// <c>guildId</c> — there is no guild lock and no default guild. The Discord
/// identity comes from the per-request token via <see cref="IDiscordContext"/>.
///
/// Notes on the gateway→REST port:
/// • There is no local cache: collections (members/channels/emotes) are fetched
///   per call. Member listing requires the GUILD_MEMBERS privileged intent.
/// • Presence (<c>Status</c>) and gateway latency/connection state do not exist
///   over REST and are intentionally absent from the output.
/// • <see cref="RestGuildUser"/> exposes <c>RoleIds</c>, not role objects, so role
///   names are resolved against the guild's role list.
/// </summary>
[McpServerToolType]
public sealed class DiscordReadTools
{
    private readonly IDiscordContext _ctx;

    public DiscordReadTools(IDiscordContext ctx) => _ctx = ctx;

    private async Task<RestGuild> GuildAsync(string guildId, bool withCounts = false)
    {
        if (!ulong.TryParse(guildId, out var gid))
            throw new ArgumentException("guildId must be a decimal string");
        var client = await _ctx.GetClientAsync();
        var g = await client.GetGuildAsync(gid, withCounts);
        if (g == null)
            throw new InvalidOperationException($"Guild {guildId} not found, or the bot is not a member of it.");
        return g;
    }

    [McpServerTool, Description(
        "High-level snapshot of one guild: bot identity plus member/channel/role counts. " +
        "REST-only, so no gateway latency or connection state.")]
    public async Task<object> GuildStatus(
        [Description("Target guild (server) ID as decimal string.")] string guildId)
    {
        var g = await GuildAsync(guildId, withCounts: true);
        var client = await _ctx.GetClientAsync();
        var channels = await g.GetChannelsAsync();
        return new
        {
            botUserId = client.CurrentUser?.Id.ToString(),
            botUsername = client.CurrentUser?.Username,
            tokenFingerprint = _ctx.Fingerprint,
            guild = new
            {
                id = g.Id.ToString(),
                name = g.Name,
                approximateMemberCount = g.ApproximateMemberCount,
                channelCount = channels.Count,
                roleCount = g.Roles.Count,
            },
        };
    }

    [McpServerTool, Description(
        "List guild members with optional filters. Returns id, username, nickname, joinedAt, roleIds/roleNames. " +
        "Requires the GUILD_MEMBERS privileged intent. " +
        "Use roleId to filter to one role; roleNameContains for fuzzy role search. " +
        "joinedBeforeIsoUtc / joinedAfterIsoUtc accept ISO 8601 (e.g. 2025-01-01T00:00:00Z). " +
        "noRoles=true filters to members with only @everyone. limit caps the result (default 100, max 1000).")]
    public async Task<object> ListMembers(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Filter to members holding this role ID (decimal string). Optional.")] string? roleId = null,
        [Description("Filter to members holding any role whose name contains this substring (case-insensitive). Optional.")] string? roleNameContains = null,
        [Description("Only members who joined BEFORE this ISO 8601 UTC timestamp. Optional.")] string? joinedBeforeIsoUtc = null,
        [Description("Only members who joined AFTER this ISO 8601 UTC timestamp. Optional.")] string? joinedAfterIsoUtc = null,
        [Description("Filter to members with only @everyone (no extra roles).")] bool noRoles = false,
        [Description("Filter to bots only.")] bool botsOnly = false,
        [Description("Exclude bots from the result.")] bool excludeBots = false,
        [Description("Max items to return. Default 100, max 1000.")] int limit = 100)
    {
        if (limit <= 0) limit = 100;
        if (limit > 1000) limit = 1000;

        var g = await GuildAsync(guildId);
        var users = await AllUsersAsync(g);
        IEnumerable<RestGuildUser> q = users;

        if (!string.IsNullOrWhiteSpace(roleId) && ulong.TryParse(roleId, out var rid))
            q = q.Where(u => u.RoleIds.Contains(rid));

        if (!string.IsNullOrWhiteSpace(roleNameContains))
            q = q.Where(u => u.RoleIds.Any(id =>
                g.GetRole(id)?.Name.Contains(roleNameContains, StringComparison.OrdinalIgnoreCase) == true));

        if (TryParseUtc(joinedBeforeIsoUtc, out var before))
            q = q.Where(u => u.JoinedAt.HasValue && u.JoinedAt.Value.UtcDateTime < before);

        if (TryParseUtc(joinedAfterIsoUtc, out var after))
            q = q.Where(u => u.JoinedAt.HasValue && u.JoinedAt.Value.UtcDateTime > after);

        if (noRoles)
            q = q.Where(u => CountRealRoles(u, g) == 0);

        if (botsOnly) q = q.Where(u => u.IsBot);
        if (excludeBots) q = q.Where(u => !u.IsBot);

        var items = q
            .OrderBy(u => u.JoinedAt ?? DateTimeOffset.MinValue)
            .Take(limit)
            .Select(u =>
            {
                var (ids, names) = MapRoles(u.RoleIds, g);
                return new
                {
                    id = u.Id.ToString(),
                    username = u.Username,
                    globalName = u.GlobalName,
                    nickname = u.Nickname,
                    isBot = u.IsBot,
                    joinedAtUtc = u.JoinedAt?.UtcDateTime,
                    roleIds = ids,
                    roleNames = names,
                };
            })
            .ToArray();

        return new { count = items.Length, members = items };
    }

    [McpServerTool, Description("Fetch one member by user ID. Returns full role list, joined timestamp, premium-since.")]
    public async Task<object> GetMember(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Discord user ID as decimal string.")] string userId)
    {
        if (!ulong.TryParse(userId, out var uid))
            throw new ArgumentException("userId must be a decimal string");

        var g = await GuildAsync(guildId);
        var u = await g.GetUserAsync(uid);
        if (u == null) return new { found = false, userId };

        var (ids, names) = MapRoles(u.RoleIds, g);
        return new
        {
            found = true,
            id = u.Id.ToString(),
            username = u.Username,
            globalName = u.GlobalName,
            nickname = u.Nickname,
            isBot = u.IsBot,
            joinedAtUtc = u.JoinedAt?.UtcDateTime,
            createdAtUtc = u.CreatedAt.UtcDateTime,
            premiumSinceUtc = u.PremiumSince?.UtcDateTime,
            timedOutUntilUtc = u.TimedOutUntil?.UtcDateTime,
            roleIds = ids,
            roleNames = names,
        };
    }

    [McpServerTool, Description("List all roles in the guild with id, name, position, hoist/mentionable/managed flags, and key permission flags.")]
    public async Task<object> ListRoles(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Filter by name substring (case-insensitive). Optional.")] string? nameContains = null)
    {
        var g = await GuildAsync(guildId);
        IEnumerable<RestRole> q = g.Roles;
        if (!string.IsNullOrWhiteSpace(nameContains))
            q = q.Where(r => r.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase));

        // Per-role member count is intentionally omitted: over REST it would require
        // downloading the full member list (GUILD_MEMBERS intent) on every call.
        return q
            .OrderByDescending(r => r.Position)
            .Select(r => new
            {
                id = r.Id.ToString(),
                name = r.Name,
                position = r.Position,
                isHoisted = r.IsHoisted,
                isMentionable = r.IsMentionable,
                isManaged = r.IsManaged,
                isEveryone = r.IsEveryone,
                permissions = new
                {
                    admin = r.Permissions.Administrator,
                    manageRoles = r.Permissions.ManageRoles,
                    manageChannels = r.Permissions.ManageChannels,
                    manageMessages = r.Permissions.ManageMessages,
                    kick = r.Permissions.KickMembers,
                    ban = r.Permissions.BanMembers,
                },
            })
            .ToArray();
    }

    [McpServerTool, Description("List all channels (text/voice/category/forum/stage) with id, name, type, category, position.")]
    public async Task<object> ListChannels(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Filter by channel type: text, voice, category, news, forum, stage. Optional.")] string? type = null,
        [Description("Filter by category ID. Optional.")] string? categoryId = null,
        [Description("Filter by name substring (case-insensitive). Optional.")] string? nameContains = null)
    {
        var g = await GuildAsync(guildId);
        IEnumerable<RestGuildChannel> q = await g.GetChannelsAsync();

        if (!string.IsNullOrWhiteSpace(type))
        {
            var t = type.Trim().ToLowerInvariant();
            q = q.Where(c => MatchesChannelType(c, t));
        }
        if (!string.IsNullOrWhiteSpace(categoryId) && ulong.TryParse(categoryId, out var cid))
            q = q.Where(c => (c as INestedChannel)?.CategoryId == cid);

        if (!string.IsNullOrWhiteSpace(nameContains))
            q = q.Where(c => c.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase));

        return q
            .OrderBy(c => c.Position)
            .Select(c => new
            {
                id = c.Id.ToString(),
                name = c.Name,
                type = c.GetType().Name,
                position = c.Position,
                categoryId = (c as INestedChannel)?.CategoryId?.ToString(),
                topic = (c as ITextChannel)?.Topic,
                isNsfw = (c as ITextChannel)?.IsNsfw,
                slowModeSeconds = (c as ITextChannel)?.SlowModeInterval,
            })
            .ToArray();
    }

    [McpServerTool, Description(
        "List permission overwrites on a channel. Returns each overwrite with target type/id/name " +
        "and the comma-separated allow/deny ChannelPermission flag names. Read current state before " +
        "calling set_channel_permissions / clear_channel_permissions.")]
    public async Task<object> ListChannelPermissions(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Channel ID (decimal string).")] string channelId)
    {
        if (!ulong.TryParse(channelId, out var cid))
            throw new ArgumentException("channelId must be a decimal string");

        var g = await GuildAsync(guildId);
        var ch = await g.GetChannelAsync(cid);
        if (ch is not IGuildChannel gch)
            throw new ArgumentException("channel not found");

        var rows = new List<object>();
        foreach (var ow in gch.PermissionOverwrites)
        {
            string targetType = ow.TargetType == PermissionTarget.Role ? "role" : "user";
            string? targetName = ow.TargetType == PermissionTarget.Role
                ? g.GetRole(ow.TargetId)?.Name
                : (await g.GetUserAsync(ow.TargetId))?.Username;

            rows.Add(new
            {
                targetType,
                targetId = ow.TargetId.ToString(),
                targetName,
                allow = MaskToNames(ow.Permissions.AllowValue),
                deny = MaskToNames(ow.Permissions.DenyValue),
            });
        }

        return new { channelId, channelName = gch.Name, count = rows.Count, overwrites = rows };
    }

    [McpServerTool, Description(
        "List active (non-archived) threads in the guild, optionally limited to one parent channel. " +
        "Returns id, name, parent channel, archived/locked state. Archived threads are not included.")]
    public async Task<object> ListThreads(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Limit threads to this parent channel ID. Optional.")] string? channelId = null)
    {
        var g = await GuildAsync(guildId);
        var channels = await g.GetChannelsAsync();

        // Discord exposes active threads only via a guild-wide endpoint, surfaced on
        // thread-container channels. One call on any container returns them all.
        var container = channels.OfType<IThreadContainerChannel>().FirstOrDefault();
        if (container == null)
            return new { count = 0, threads = Array.Empty<object>() };

        ulong? filter = null;
        if (!string.IsNullOrWhiteSpace(channelId) && ulong.TryParse(channelId, out var cidf))
            filter = cidf;

        var nameById = channels.ToDictionary(c => c.Id, c => c.Name);
        var active = await container.GetActiveThreadsAsync();
        IEnumerable<RestThreadChannel> q = active.OfType<RestThreadChannel>();
        if (filter.HasValue)
            q = q.Where(t => t.ParentChannelId == filter.Value);

        var rows = q.Select(t => new
        {
            id = t.Id.ToString(),
            name = t.Name,
            parentChannelId = t.ParentChannelId.ToString(),
            parentChannelName = nameById.TryGetValue(t.ParentChannelId, out var pn) ? pn : null,
            isArchived = t.IsArchived,
            isLocked = t.IsLocked,
            memberCount = t.MemberCount,
            messageCount = t.MessageCount,
            createdAtUtc = t.CreatedAt.UtcDateTime,
        }).ToArray();

        return new { count = rows.Length, threads = rows };
    }

    [McpServerTool, Description(
        "Read recent messages from a channel or thread. Returns id, author, content, embeds (title+description), " +
        "timestamps. Newest first. Pass beforeMessageId to paginate further back. Max 100 per call.")]
    public async Task<object> ListMessages(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Channel or thread ID (decimal string).")] string channelId,
        [Description("How many messages. Default 50, max 100.")] int limit = 50,
        [Description("Paginate: only return messages older than this message ID. Optional.")] string? beforeMessageId = null)
    {
        if (!ulong.TryParse(channelId, out var cid))
            throw new ArgumentException("channelId must be a decimal string");
        if (limit <= 0) limit = 50;
        if (limit > 100) limit = 100;

        var client = await _ctx.GetClientAsync();
        var ch = await client.GetChannelAsync(cid);
        if (ch is not IMessageChannel mc)
            throw new ArgumentException("channel does not support messages");

        var msgs = new List<IMessage>();
        if (!string.IsNullOrWhiteSpace(beforeMessageId))
        {
            if (!ulong.TryParse(beforeMessageId, out var beforeId))
                throw new ArgumentException("beforeMessageId must be a decimal string");
            await foreach (var batch in mc.GetMessagesAsync(beforeId, Direction.Before, limit))
                msgs.AddRange(batch);
        }
        else
        {
            await foreach (var batch in mc.GetMessagesAsync(limit))
                msgs.AddRange(batch);
        }

        return msgs
            .OrderByDescending(m => m.Timestamp)
            .Select(m => new
            {
                id = m.Id.ToString(),
                authorId = m.Author.Id.ToString(),
                authorName = m.Author.Username,
                isBot = m.Author.IsBot,
                content = m.Content,
                timestampUtc = m.Timestamp.UtcDateTime,
                editedUtc = m.EditedTimestamp?.UtcDateTime,
                attachmentCount = m.Attachments?.Count ?? 0,
                pinned = m.IsPinned,
                reactions = m.Reactions?.Select(r => new
                {
                    emoji = r.Key.ToString(),
                    count = r.Value.ReactionCount,
                    byBot = r.Value.IsMe,
                }).ToArray() ?? Array.Empty<object>(),
                embeds = m.Embeds?.Select(e => new
                {
                    title = e.Title,
                    description = e.Description,
                    url = e.Url,
                    colorHex = e.Color.HasValue ? $"#{e.Color.Value.R:X2}{e.Color.Value.G:X2}{e.Color.Value.B:X2}" : null,
                    footer = e.Footer?.Text,
                    thumbnailUrl = e.Thumbnail?.Url,
                    imageUrl = e.Image?.Url,
                    fieldCount = e.Fields.Length,
                }).ToArray() ?? Array.Empty<object>(),
            })
            .ToArray();
    }

    [McpServerTool, Description(
        "Read the reactions on one message. Without emoji: the summary Discord shows under the message — every emoji " +
        "with its count. With emoji: WHO reacted with it (user id, username, global name, bot flag). emoji is a unicode " +
        "emoji (e.g. '👍') or a custom emote in Discord syntax ('<:name:id>' / '<a:name:id>') — the same syntax " +
        "add_reaction takes and list_messages returns. Super (burst) reactors are included and flagged.")]
    public async Task<object> ListReactions(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Channel or thread ID (decimal string).")] string channelId,
        [Description("Message ID (decimal string).")] string messageId,
        [Description("Optional. Omit for per-emoji counts; pass an emoji to list the users who reacted with it.")] string? emoji = null,
        [Description("Max users to return when emoji is given. Default 100, max 1000 (Discord pages 100 at a time).")] int limit = 100)
    {
        if (!ulong.TryParse(channelId, out var cid))
            throw new ArgumentException("channelId must be a decimal string");
        if (!ulong.TryParse(messageId, out var mid))
            throw new ArgumentException("messageId must be a decimal string");
        if (limit <= 0) limit = 100;
        if (limit > 1000) limit = 1000;

        var client = await _ctx.GetClientAsync();
        var ch = await client.GetChannelAsync(cid);
        if (ch is not IMessageChannel mc)
            throw new ArgumentException("channel does not support messages");

        var msg = await mc.GetMessageAsync(mid);
        if (msg is not IUserMessage userMsg)
            throw new InvalidOperationException($"Message {messageId} not found in channel {channelId}.");

        var meta = userMsg.Reactions ?? new Dictionary<IEmote, ReactionMetadata>();

        if (string.IsNullOrWhiteSpace(emoji))
        {
            var rows = meta
                .Select(r => new
                {
                    emoji = r.Key.ToString(),
                    count = r.Value.ReactionCount,
                    normalCount = r.Value.NormalCount,
                    superCount = r.Value.BurstCount,
                    byBot = r.Value.IsMe,
                })
                .OrderByDescending(r => r.count)
                .ToArray();

            return new
            {
                channelId,
                messageId,
                totalReactions = rows.Sum(r => r.count),
                distinctEmojis = rows.Length,
                reactions = rows,
            };
        }

        var emote = EmoteParser.Parse(emoji);
        if (emote == null)
            throw new ArgumentException("emoji must be a unicode emoji or '<:name:id>' / '<a:name:id>' custom-emote syntax");

        // Match on ID for custom emotes, so the caller may omit the animated flag.
        ReactionMetadata? found = null;
        foreach (var kv in meta)
            if (EmoteParser.Same(kv.Key, emote)) { found = kv.Value; break; }

        // Super (burst) reactions are a separate user list on Discord's side —
        // only worth a second round-trip when the summary says there are any.
        var normal = await ReactorsAsync(userMsg, emote, limit, ReactionType.Normal);
        var super = (found?.BurstCount ?? 0) > 0
            ? await ReactorsAsync(userMsg, emote, limit, ReactionType.Burst)
            : new List<IUser>();

        var superIds = super.Select(u => u.Id).ToHashSet();
        var users = normal.Concat(super)
            .GroupBy(u => u.Id)
            .Select(g => g.First())
            .Select(u => new
            {
                id = u.Id.ToString(),
                username = u.Username,
                globalName = u.GlobalName,
                isBot = u.IsBot,
                super = superIds.Contains(u.Id),
            })
            .ToArray();

        return new
        {
            channelId,
            messageId,
            emoji = emote.ToString(),
            count = found?.ReactionCount ?? users.Length,
            returned = users.Length,
            truncated = (found?.ReactionCount ?? users.Length) > users.Length,
            users,
        };
    }

    private static async Task<List<IUser>> ReactorsAsync(IUserMessage msg, IEmote emote, int limit, ReactionType type)
    {
        var users = new List<IUser>();
        await foreach (var batch in msg.GetReactionUsersAsync(emote, limit, options: null, type))
            users.AddRange(batch);
        return users;
    }

    [McpServerTool, Description("List currently banned users. Returns user id, username, ban reason. Paginated from Discord.")]
    public async Task<object> ListBans(
        [Description("Target guild (server) ID as decimal string.")] string guildId)
    {
        var g = await GuildAsync(guildId);
        var bans = new List<RestBan>();
        await foreach (var batch in g.GetBansAsync())
            bans.AddRange(batch);

        var rows = bans.Select(b => new
        {
            userId = b.User.Id.ToString(),
            username = b.User.Username,
            globalName = b.User.GlobalName,
            reason = b.Reason,
        }).OrderBy(r => r.username).ToArray();

        return new { count = rows.Length, bans = rows };
    }

    [McpServerTool, Description(
        "List members currently under a Discord timeout. Returns user id, username, until-utc. " +
        "Requires the GUILD_MEMBERS privileged intent (scans the member list).")]
    public async Task<object> ListTimeouts(
        [Description("Target guild (server) ID as decimal string.")] string guildId)
    {
        var g = await GuildAsync(guildId);
        var now = DateTimeOffset.UtcNow;
        var users = await AllUsersAsync(g);
        var rows = users
            .Where(u => u.TimedOutUntil.HasValue && u.TimedOutUntil.Value > now)
            .OrderBy(u => u.TimedOutUntil!.Value)
            .Select(u => new
            {
                userId = u.Id.ToString(),
                username = u.Username,
                nickname = u.Nickname,
                untilUtc = u.TimedOutUntil!.Value.UtcDateTime,
                minutesRemaining = (int)Math.Ceiling((u.TimedOutUntil!.Value - now).TotalMinutes),
            })
            .ToArray();

        return new { count = rows.Length, timeouts = rows };
    }

    [McpServerTool, Description("List active invites for the guild. Returns code, channel, uses, max-uses, expiry, inviter.")]
    public async Task<object> ListInvites(
        [Description("Target guild (server) ID as decimal string.")] string guildId)
    {
        var g = await GuildAsync(guildId);
        var invites = await g.GetInvitesAsync();
        var rows = invites.OrderByDescending(i => i.CreatedAt).Select(i => new
        {
            code = i.Code,
            url = i.Url,
            channelId = i.ChannelId.ToString(),
            channelName = i.ChannelName,
            uses = i.Uses,
            maxUses = i.MaxUses,
            maxAgeSeconds = i.MaxAge,
            isTemporary = i.IsTemporary,
            createdAtUtc = i.CreatedAt?.UtcDateTime,
            inviterId = i.Inviter?.Id.ToString(),
            inviterName = i.Inviter?.Username,
        }).ToArray();
        return new { count = rows.Length, invites = rows };
    }

    [McpServerTool, Description("List custom stickers in the guild. Returns id, name, description, tags, format, available flag.")]
    public async Task<object> ListStickers(
        [Description("Target guild (server) ID as decimal string.")] string guildId)
    {
        var g = await GuildAsync(guildId);
        var stickers = await g.GetStickersAsync();
        // GetStickersAsync returns null (not an empty collection) when the guild has none.
        if (stickers == null)
            return new { count = 0, stickers = Array.Empty<object>() };
        var rows = stickers.OrderBy(s => s.Name).Select(s => new
        {
            id = s.Id.ToString(),
            name = s.Name,
            description = s.Description,
            tags = string.Join(",", s.Tags ?? new List<string>()),
            format = s.Format.ToString(),
            available = s.IsAvailable,
        }).ToArray();
        return new { count = rows.Length, stickers = rows };
    }

    [McpServerTool, Description("List custom emojis in the guild. Returns id, name, animated flag, available flag, tag syntax.")]
    public async Task<object> ListEmojis(
        [Description("Target guild (server) ID as decimal string.")] string guildId)
    {
        var g = await GuildAsync(guildId);
        var emotes = await g.GetEmotesAsync();
        var rows = emotes.OrderBy(e => e.Name).Select(e => new
        {
            id = e.Id.ToString(),
            name = e.Name,
            animated = e.Animated,
            available = e.IsAvailable,
            managed = e.IsManaged,
            createdAtUtc = e.CreatedAt.UtcDateTime,
            url = e.Url,
            tagSyntax = e.Animated ? $"<a:{e.Name}:{e.Id}>" : $"<:{e.Name}:{e.Id}>",
        }).ToArray();
        return new { count = rows.Length, emojis = rows };
    }

    [McpServerTool, Description("List guild scheduled events. Returns id, name, type, start/end times, channel/location, status, attendee count.")]
    public async Task<object> ListScheduledEvents(
        [Description("Target guild (server) ID as decimal string.")] string guildId)
    {
        var g = await GuildAsync(guildId);
        var events = await g.GetEventsAsync();
        var rows = events.OrderBy(e => e.StartTime).Select(e => new
        {
            id = e.Id.ToString(),
            name = e.Name,
            description = e.Description,
            type = e.Type.ToString(),
            status = e.Status.ToString(),
            startUtc = e.StartTime.UtcDateTime,
            endUtc = e.EndTime?.UtcDateTime,
            channelId = e.ChannelId?.ToString(),
            location = e.Location,
            userCount = e.UserCount,
            creatorId = e.Creator?.Id.ToString(),
        }).ToArray();
        return new { count = rows.Length, events = rows };
    }

    [McpServerTool, Description(
        "List webhooks. Pass channelId for one channel, or omit for guild-wide. Returns id, name, channel, creator. " +
        "Webhook URLs (containing the secret token) are NOT included to avoid leaking credentials.")]
    public async Task<object> ListWebhooks(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Channel ID (decimal string). Optional — omit for guild-wide.")] string? channelId = null)
    {
        var g = await GuildAsync(guildId);
        IReadOnlyCollection<IWebhook> webhooks;
        if (string.IsNullOrWhiteSpace(channelId))
        {
            webhooks = await g.GetWebhooksAsync();
        }
        else
        {
            if (!ulong.TryParse(channelId, out var cid))
                throw new ArgumentException("channelId must be a decimal string");
            if (await g.GetChannelAsync(cid) is not ITextChannel ch)
                throw new ArgumentException("channel not found or not a text channel");
            webhooks = await ch.GetWebhooksAsync();
        }

        var rows = webhooks.OrderBy(w => w.Name).Select(w => new
        {
            id = w.Id.ToString(),
            name = w.Name,
            channelId = w.ChannelId.ToString(),
            creatorId = w.Creator?.Id.ToString(),
            creatorName = w.Creator?.Username,
            createdAtUtc = w.CreatedAt.UtcDateTime,
        }).ToArray();
        return new { count = rows.Length, webhooks = rows };
    }

    [McpServerTool, Description("Query the guild audit log. Returns who-did-what-when entries from Discord's native audit. Max 100 per call.")]
    public async Task<object> AuditLogQuery(
        [Description("Target guild (server) ID as decimal string.")] string guildId,
        [Description("Discord audit-log action type name (e.g. 'MemberRoleUpdated', 'ChannelDeleted'). Optional.")] string? action = null,
        [Description("Filter to entries by this user ID. Optional.")] string? userId = null,
        [Description("How many entries. Default 50, max 100.")] int limit = 50)
    {
        if (limit <= 0) limit = 50;
        if (limit > 100) limit = 100;

        var g = await GuildAsync(guildId);
        ulong? actorFilter = null;
        if (!string.IsNullOrWhiteSpace(userId) && ulong.TryParse(userId, out var uid))
            actorFilter = uid;

        ActionType? actionFilter = null;
        if (!string.IsNullOrWhiteSpace(action) && Enum.TryParse<ActionType>(action, true, out var at))
            actionFilter = at;

        var rows = new List<object>();
        await foreach (var batch in g.GetAuditLogsAsync(limit, actionType: actionFilter, userId: actorFilter))
        {
            foreach (var entry in batch)
            {
                rows.Add(new
                {
                    id = entry.Id.ToString(),
                    action = entry.Action.ToString(),
                    userId = entry.User?.Id.ToString(),
                    userName = entry.User?.Username,
                    dataType = entry.Data?.GetType().Name,
                    reason = entry.Reason,
                    createdAtUtc = entry.CreatedAt.UtcDateTime,
                });
                if (rows.Count >= limit) break;
            }
            if (rows.Count >= limit) break;
        }
        return new { count = rows.Count, entries = rows };
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task<List<RestGuildUser>> AllUsersAsync(RestGuild g)
    {
        var list = new List<RestGuildUser>();
        await foreach (var batch in g.GetUsersAsync())
            list.AddRange(batch);
        return list;
    }

    /// <summary>Map a member's role IDs to (idStrings, names), excluding @everyone.</summary>
    private static (string[] ids, string[] names) MapRoles(IEnumerable<ulong> roleIds, RestGuild g)
    {
        var ids = new List<string>();
        var names = new List<string>();
        foreach (var rid in roleIds)
        {
            var r = g.GetRole(rid);
            if (r == null || r.IsEveryone) continue;
            ids.Add(rid.ToString());
            names.Add(r.Name);
        }
        return (ids.ToArray(), names.ToArray());
    }

    private static int CountRealRoles(RestGuildUser u, RestGuild g)
        => u.RoleIds.Count(id => g.GetRole(id) is { IsEveryone: false });

    private static string MaskToNames(ulong mask)
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

    private static bool TryParseUtc(string? s, out DateTime result)
    {
        if (string.IsNullOrWhiteSpace(s)) { result = default; return false; }
        return DateTime.TryParse(
            s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out result);
    }

    private static bool MatchesChannelType(IGuildChannel c, string t) => t switch
    {
        "text" => c is ITextChannel and not IThreadChannel and not INewsChannel and not IForumChannel,
        "voice" => c is IVoiceChannel and not IStageChannel,
        "category" => c is ICategoryChannel,
        "news" => c is INewsChannel,
        "forum" => c is IForumChannel,
        "stage" => c is IStageChannel,
        "thread" => c is IThreadChannel,
        _ => true,
    };
}
