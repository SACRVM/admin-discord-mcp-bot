namespace AdminDiscordMcp.Config;

/// <summary>
/// Well-known per-user locations for runtime data. The server may run from a
/// read-only or unpredictable working directory (e.g. as a Scheduled Task), so
/// logs and the audit trail go to <c>%LOCALAPPDATA%\admin-discord-mcp\</c>, never
/// next to the executable.
/// </summary>
public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "admin-discord-mcp");

    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string LogFilePath => Path.Combine(LogsDir, "admcp.log");
    public static string AuditLogPath => Path.Combine(DataDir, "admin-audit.log");

    public static void Ensure()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
    }
}
