using Discord;

namespace AdminDiscordMcp.Discord;

/// <summary>
/// Turns the emoji strings agents pass in into Discord.Net emotes. Accepts a
/// unicode emoji ("👍") or a custom emote in Discord syntax ("&lt;:name:id&gt;" /
/// "&lt;a:name:id&gt;" for animated ones) — the same syntax the reaction tools
/// document and the same syntax <c>list_emojis</c> hands back.
/// </summary>
public static class EmoteParser
{
    public static IEmote? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var trimmed = s.Trim();
        if (Emote.TryParse(trimmed, out var custom)) return custom;
        try { return new Emoji(trimmed); }
        catch { return null; }
    }

    /// <summary>
    /// Compares two emotes the way Discord does: custom emotes match on ID, so an
    /// animated emote passed as "&lt;:name:id&gt;" still matches the "&lt;a:name:id&gt;"
    /// key on the message. Unicode emoji match on the character itself.
    /// </summary>
    public static bool Same(IEmote? a, IEmote? b)
    {
        if (a == null || b == null) return false;
        if (a is Emote ea && b is Emote eb) return ea.Id == eb.Id;
        if (a is Emote || b is Emote) return false;
        return string.Equals(a.Name, b.Name, StringComparison.Ordinal);
    }
}
