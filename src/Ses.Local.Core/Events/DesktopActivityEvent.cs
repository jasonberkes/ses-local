namespace Ses.Local.Core.Events;

/// <summary>
/// Raised when Claude Desktop Local Storage changes are detected.
/// Carries the extracted conversation UUIDs so the API client
/// can fetch only what's new or updated.
/// </summary>
public sealed class DesktopActivityEvent
{
    public DateTime DetectedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Conversation UUIDs found in the LDB files.
    /// Empty = file changed but no UUIDs extracted (rare).
    /// </summary>
    public IReadOnlyList<string> ConversationUuids { get; init; } = Array.Empty<string>();
}
