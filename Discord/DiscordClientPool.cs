using System.Collections.Concurrent;
using AdminDiscordMcp.Config;
using Discord;
using Discord.Rest;
using Microsoft.Extensions.Logging;

namespace AdminDiscordMcp.Discord;

/// <summary>
/// Singleton pool of logged-in <see cref="DiscordRestClient"/> instances, keyed by
/// a SHA-256 fingerprint of the bot token. This is the multi-tenant core: the
/// server holds NO credentials of its own — each request carries its bot token in
/// a header, and we get-or-create a REST client for that identity on demand.
///
/// It is a connection cache, NOT a registry: entries are derivable purely from the
/// incoming token, hold no human-meaningful names, and are evicted after idle.
/// </summary>
public sealed class DiscordClientPool : IAsyncDisposable
{
    private sealed class Entry
    {
        public required DiscordRestClient Client { get; init; }
        public DateTime LastUsedUtc { get; set; }
        public void Touch() => LastUsedUtc = DateTime.UtcNow;
    }

    private readonly ConcurrentDictionary<string, Lazy<Task<Entry>>> _clients = new();
    private readonly TimeSpan _idleTtl;
    private readonly ILogger<DiscordClientPool> _log;
    private readonly Timer _sweeper;

    public DiscordClientPool(BotConfig config, ILogger<DiscordClientPool> log)
    {
        _log = log;
        _idleTtl = TimeSpan.FromMinutes(Math.Max(1, config.ClientIdleMinutes));
        _sweeper = new Timer(_ => Sweep(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// Return a logged-in REST client for <paramref name="token"/>, creating and
    /// caching one on first use. Concurrent callers for the same token share a
    /// single login via <see cref="Lazy{T}"/>. A failed login is not cached.
    /// </summary>
    public async Task<DiscordRestClient> GetOrCreateAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                $"No Discord bot token supplied. Send it in the '{DiscordContext.TokenHeader}' header.");

        var key = TokenFingerprint.Key(token);
        var lazy = _clients.GetOrAdd(key, _ => new Lazy<Task<Entry>>(() => LoginAsync(token)));
        try
        {
            var entry = await lazy.Value.ConfigureAwait(false);
            entry.Touch();
            return entry.Client;
        }
        catch
        {
            // Don't let a failed login poison the cache — drop it so the next
            // call (e.g. after fixing the token) can retry cleanly.
            _clients.TryRemove(key, out _);
            throw;
        }
    }

    private async Task<Entry> LoginAsync(string token)
    {
        var client = new DiscordRestClient();
        await client.LoginAsync(TokenType.Bot, token).ConfigureAwait(false);
        _log.LogInformation(
            "Bot logged in: {User} ({Id}) [token {Fp}]",
            client.CurrentUser?.Username, client.CurrentUser?.Id, TokenFingerprint.Short(token));
        return new Entry { Client = client, LastUsedUtc = DateTime.UtcNow };
    }

    /// <summary>Log out + dispose any client idle longer than the TTL.</summary>
    private void Sweep()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _clients)
        {
            var lazy = kv.Value;
            if (!lazy.IsValueCreated || !lazy.Value.IsCompletedSuccessfully) continue;
            var entry = lazy.Value.Result;
            if (now - entry.LastUsedUtc < _idleTtl) continue;
            if (_clients.TryRemove(kv.Key, out _))
            {
                _log.LogInformation("Evicted idle Discord client [token {Fp}]", kv.Key[..8].ToLowerInvariant());
                _ = DisposeClientAsync(entry.Client);
            }
        }
    }

    private static async Task DisposeClientAsync(DiscordRestClient client)
    {
        try { await client.LogoutAsync().ConfigureAwait(false); } catch { /* best-effort */ }
        client.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _sweeper.DisposeAsync().ConfigureAwait(false);
        foreach (var kv in _clients)
        {
            if (kv.Value.IsValueCreated && kv.Value.Value.IsCompletedSuccessfully)
                await DisposeClientAsync(kv.Value.Value.Result.Client).ConfigureAwait(false);
        }
        _clients.Clear();
    }
}
