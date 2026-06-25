using System.Text;

namespace AdminDiscordMcp.Discord;

/// <summary>
/// Splits a message into Discord-sized pieces. Discord rejects message content
/// longer than 2000 characters, so <c>send_message</c> / <c>dm_user</c> route long
/// content through here and post it as multiple messages. Splits on line breaks
/// where possible; a single line longer than the limit is hard-split.
/// </summary>
public static class MessageChunker
{
    /// <summary>Discord's hard limit on message content length.</summary>
    public const int MaxMessageSize = 2000;

    public static IEnumerable<string> Chunk(string? message, int maxLength = MaxMessageSize)
    {
        if (string.IsNullOrEmpty(message)) yield break;
        if (message.Length <= maxLength) { yield return message; yield break; }

        var sb = new StringBuilder();
        foreach (var line in message.Replace("\r\n", "\n").Split('\n'))
        {
            // A single line that exceeds the budget can't fit any chunk — flush
            // what we have and hard-split the line into max-sized pieces.
            if (line.Length > maxLength)
            {
                if (sb.Length > 0) { yield return sb.ToString().TrimEnd('\n'); sb.Clear(); }
                for (int i = 0; i < line.Length; i += maxLength)
                    yield return line.Substring(i, Math.Min(maxLength, line.Length - i));
                continue;
            }

            // +1 for the '\n' we re-add between lines.
            if (sb.Length + line.Length + 1 > maxLength)
            {
                yield return sb.ToString().TrimEnd('\n');
                sb.Clear();
            }
            sb.Append(line).Append('\n');
        }

        if (sb.Length > 0) yield return sb.ToString().TrimEnd('\n');
    }
}
