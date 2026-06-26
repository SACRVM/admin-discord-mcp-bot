# Admin Discord MCP

A standalone [Model Context Protocol](https://modelcontextprotocol.io) server that gives an
AI agent **full administrative control over Discord servers (guilds)** through the Discord
**REST API** — create and configure channels, roles, members, scheduled events, messages,
webhooks, emojis, and more.

It is **general-purpose and self-hosted**: you bring your own Discord bot, the agent drives it.
The server holds no credentials of its own — the bot identity is supplied per request, so one
running instance can administer different servers as different bots without any reconfiguration.

> **Scope: Discord guilds only.** This is the agent's remote hands for Discord. It does **not**
> touch the host machine (no shell, no filesystem access).

---

## Why another Discord MCP?

Plenty of Discord MCP servers exist. This one is the specific combination none of them quite had:

- **Dynamic, per-request bot identity** — the token arrives in an HTTP header, never baked into
  code or config. Use a different bot by pointing your MCP client at a different token.
- **`dryRun` by default on every write** — the agent first reports "would delete/ban/create X";
  you confirm; only then does `dryRun: false` apply it. This is the real safety layer.
- **No security theatre** — no auth gate, no IP allowlist, no fail-ban. It binds to `127.0.0.1`
  and runs on demand. Each user self-hosts their own instance. The bot token *is* the auth: a
  request without a valid one can do nothing.
- **REST-only, on-demand** — uses Discord.Net's `DiscordRestClient`, not the gateway socket. No
  persistent WebSocket, no "online" presence, no event cache. Spin up → act → spin down.

---

## Step 1 — One-time Discord setup

1. Create an application at the [Discord Developer Portal](https://discord.com/developers/applications).
2. Under **Bot**, click **Reset Token** and copy the **bot token** (the long `MTxxxx.yyyy.zzzz`
   value — *not* the Application ID, Public Key, or Client Secret). This is the only credential
   the server ever uses, and it is supplied by you at connect time, never stored on the server.
3. Invite the bot to your target guild(s) with the **Administrator** permission.
4. **Move the bot's role to the top** of the guild's role list. Even with Administrator, Discord's
   role hierarchy still applies — the bot cannot manage roles or members above its own highest role.
5. Enable the privileged intents you need under **Bot → Privileged Gateway Intents**:
   - **Server Members Intent** — required for `list_members` / `list_timeouts`.
   - **Message Content Intent** — governs reading message text.

## Step 2 — Install (Windows, recommended)

1. Download `AdminDiscordMcpSetup-<version>-win-x64.exe` from the
   [Releases](https://github.com/chloe-dream/admin-discord-mcp-bot/releases) page (or build it
   yourself — see [Build from source](#build-from-source)).
2. Run the installer. No admin rights needed. It will:
   - Optionally prompt for your **bot token** (paste it, or leave blank to add later).
   - Install to `%LOCALAPPDATA%\Programs\admin-discord-mcp\` (no UAC, self-contained — no .NET
     runtime to install separately).
   - Register a **Scheduled Task** that starts the server at logon and auto-restarts on crash.
   - Register the MCP with **Claude Code** (`user` scope) if the `claude` CLI is on PATH —
     wiring your token in as the `X-Discord-Bot-Token` header.
3. Open any Claude Code session — `/mcp` should show **`discord-admin`** connected. Try:
   *"List the roles in guild `<your-guild-id>`."*

The token lives only in your Claude MCP registration (never on the server, never in the repo).
Re-running the installer is safe.

## Step 2 (alternative) — Run / connect manually (any OS)

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet run --project .          # listens on http://127.0.0.1:47823/mcp  (GET /healthz for liveness)
```

Then point your MCP client at it, supplying the bot token **per connection** as the
`X-Discord-Bot-Token` header. With the Claude Code CLI:

```bash
claude mcp add discord-admin --scope user --transport http http://127.0.0.1:47823/mcp \
  --header "X-Discord-Bot-Token: YOUR_DISCORD_BOT_TOKEN"
```

…or via a client `.mcp.json`:

```json
{
  "mcpServers": {
    "discord-admin": {
      "type": "http",
      "url": "http://127.0.0.1:47823/mcp",
      "headers": { "X-Discord-Bot-Token": "YOUR_DISCORD_BOT_TOKEN" }
    }
  }
}
```

To act as a different bot, point a second connection at a second token. Every tool takes an
explicit `guildId` argument, so one connection can target multiple servers that bot is in.

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). To build the Windows installer
you also need [Inno Setup 6](https://jrsoftware.org/isdl.php).

```powershell
# Single-file exe only:
dotnet publish AdminDiscordMcp.csproj -c Release -o bin\win-x64

# Full installer (exe + Scheduled Task + Claude wiring):
powershell.exe -ExecutionPolicy Bypass -File installer\build-installer.ps1
#   -> bin\installer\AdminDiscordMcpSetup-<version>-win-x64.exe
```

Pushing a `v*` tag builds the installer in CI and attaches it to a GitHub Release automatically.

## Configuration

There is **no config file**. Everything has a sane default; override via command-line args or
`ADMCP_`-prefixed environment variables only if you need to.

| Setting | Default | Override examples |
| --- | --- | --- |
| `BindAddress` | `127.0.0.1` | `--AdminDiscordMcp:BindAddress=0.0.0.0` · `ADMCP_AdminDiscordMcp__BindAddress=0.0.0.0` |
| `Port` | `47823` | `--AdminDiscordMcp:Port=50000` |
| `ClientIdleMinutes` | `30` | idle TTL before a cached per-token REST client is evicted |
| `BulkPreviewThreshold` | `25` | bulk-op preview enumeration cap |
| `ProtectedUserIds` | `[]` | user IDs that destructive member ops always refuse (the bot itself is always protected) |

> Binding to anything other than `127.0.0.1` exposes the server. Only do so if you knowingly
> front it with your own TLS/firewall — there is no built-in authentication beyond the bot token.

## Tool surface

All write tools default to `dryRun: true`. Pass `dryRun: false` to apply.

**Read** (16): `guild_status`, `list_members`, `get_member`, `list_roles`, `list_channels`,
`list_channel_permissions`, `list_threads`, `list_messages`, `list_bans`, `list_timeouts`,
`list_invites`, `list_stickers`, `list_emojis`, `list_scheduled_events`, `list_webhooks`,
`audit_log_query`.

**Write** (45): roles (create/rename/delete/color/move/assign/remove + bulk), members
(kick/ban/unban/timeout/clear-timeout/nickname), messages (send/dm/edit/delete/bulk-delete/pin/
reactions), channels (create/modify/delete + permission overwrites), threads (create/modify),
invites, emojis, stickers, scheduled events, and webhooks.

Plus `who_am_i` — verifies the supplied token by logging in and returning the bot's identity.

## Security model

- **Localhost + on-demand.** Default bind is loopback; run the process only when the agent needs
  it. That is the attack-surface reduction.
- **The token is the auth.** The server stores no secret; without a valid bot token in the
  header, no Discord operation is possible.
- **`dryRun` by default.** Destructive operations are simulated until explicitly applied.
- **Audit log.** Every tool call (dry or applied) is appended as one JSON line to
  `%LOCALAPPDATA%\admin-discord-mcp\admin-audit.log` (rolling logs sit beside it under `logs\`).
  The raw bot token is **never** logged — only a short SHA-256 fingerprint.

## Limitations

- **Guild creation is intentionally not supported.** Creating a guild (`POST /guilds`) makes the
  bot the owner — a deliberate non-goal. This server only administers guilds it has been invited to.
- **Role hierarchy applies** even with Administrator — keep the bot's role at the top.
- **Member listing** requires the Server Members privileged intent and is paginated (max 1000/call).
- **No presence over REST** — member online status and gateway latency are not available.
- **Per-route + global (~50 req/s) rate limits** apply; Discord.Net handles 429s automatically.

## Roadmap

- **Done:** the full read/write admin surface over REST (62 tools), multi-tenant per-request
  token, `dryRun` by default, token-scrubbed audit log.
- **Possible future (optional):** opt-in gateway mode for live event subscription.

By design there is **no "blueprint" mega-tool** and **no guild-creation tool** — the agent shapes
servers directly from natural-language requests using the atomic tools above, and the bot must
never own a guild.

## License

[MIT](LICENSE).
