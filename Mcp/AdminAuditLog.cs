using System.Text.Json;

namespace AdminDiscordMcp.Mcp;

/// <summary>
/// Append-only forensic log. One JSON object per line in <c>data/admin-audit.log</c>.
/// This is a record, not a gate — it never blocks anything, it just lets you see
/// what the agent did after the fact. The raw bot token is NEVER written; callers
/// pass only a token fingerprint as <paramref name="actor"/>.
/// </summary>
public sealed class AdminAuditLog
{
    private readonly object _lock = new();
    private readonly string _path = Path.Combine("data", "admin-audit.log");

    public AdminAuditLog()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    public void Log(string action, object payload, bool ok = true, string? actor = null)
    {
        var line = JsonSerializer.Serialize(new
        {
            ts = DateTimeOffset.UtcNow,
            action,
            ok,
            actor,
            payload
        });
        lock (_lock)
        {
            File.AppendAllText(_path, line + Environment.NewLine);
        }
    }
}
