namespace Ses.Local.Core.Models;

public sealed class FeatureStatus
{
    public string Name { get; init; } = string.Empty;
    public string Key { get; init; } = string.Empty;
    public StatusDot Dot { get; set; } = StatusDot.Grey;
    public string LastActivity { get; set; } = "Never";
    public bool IsEnabled { get; set; }
    public bool IsComingSoon { get; init; }
}

public enum StatusDot { Green, Yellow, Red, Grey }
