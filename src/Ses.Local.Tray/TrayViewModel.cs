namespace Ses.Local.Tray;

/// <summary>View model for the ses-local tray dashboard. Full implementation in WI-937.</summary>
public sealed class TrayViewModel
{
    public bool IsClaudeAiSyncEnabled { get; set; }
    public bool IsClaudeDesktopSyncEnabled { get; set; }
    public bool IsClaudeCodeSyncEnabled { get; set; }
    public bool IsCoworkSyncEnabled { get; set; }
    public bool IsMcpMemoryToolsEnabled { get; set; }
    public bool IsCloudMemorySyncEnabled { get; set; }
    public bool IsCCHooksEnabled { get; set; }
    public string OverallStatus { get; set; } = "Starting...";
    public DateTime? LastSyncAt { get; set; }
}
