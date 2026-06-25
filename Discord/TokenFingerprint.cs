using System.Security.Cryptography;
using System.Text;

namespace AdminDiscordMcp.Discord;

/// <summary>
/// Derives stable, non-reversible identifiers from a Discord bot token. We NEVER
/// log or persist the raw token — only a fingerprint. <see cref="Key"/> is the
/// full SHA-256 (used as the client-pool dictionary key); <see cref="Short"/> is
/// an 8-char tag for human-readable logs/audit entries.
/// </summary>
public static class TokenFingerprint
{
    public static string Key(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string Short(string token)
        => string.IsNullOrEmpty(token) ? "none" : Key(token)[..8].ToLowerInvariant();
}
