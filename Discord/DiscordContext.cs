using Discord.Rest;
using Microsoft.AspNetCore.Http;

namespace AdminDiscordMcp.Discord;

/// <summary>
/// Per-request access to the caller's Discord identity. Scoped: it reads the bot
/// token from the current request's header and resolves the matching pooled REST
/// client. Tools depend on this instead of any fixed/global client, which is what
/// makes the server multi-tenant and stateless.
/// </summary>
public interface IDiscordContext
{
    /// <summary>Logged-in REST client for the token on the current request.</summary>
    Task<DiscordRestClient> GetClientAsync();

    /// <summary>Short, non-reversible tag of the current token for logs/audit.</summary>
    string Fingerprint { get; }
}

public sealed class DiscordContext : IDiscordContext
{
    public const string TokenHeader = "X-Discord-Bot-Token";

    private readonly IHttpContextAccessor _http;
    private readonly DiscordClientPool _pool;

    public DiscordContext(IHttpContextAccessor http, DiscordClientPool pool)
    {
        _http = http;
        _pool = pool;
    }

    private string Token =>
        _http.HttpContext?.Request.Headers[TokenHeader].ToString() ?? string.Empty;

    public string Fingerprint => TokenFingerprint.Short(Token);

    public Task<DiscordRestClient> GetClientAsync() => _pool.GetOrCreateAsync(Token);
}
