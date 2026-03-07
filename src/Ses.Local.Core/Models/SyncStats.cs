namespace Ses.Local.Core.Models;

/// <summary>Aggregate sync statistics for all conversation surfaces, read directly from local SQLite.</summary>
public sealed class SyncStats
{
    public SurfaceStats ClaudeChat { get; init; } = new();
    public SurfaceStats ClaudeCode { get; init; } = new();
    public SurfaceStats Cowork     { get; init; } = new();
    public SurfaceStats ChatGpt    { get; init; } = new();
    public SurfaceStats Gemini     { get; init; } = new();
    public int          TotalConversations  { get; init; }
    public int          TotalMessages       { get; init; }
    public long         LocalDbSizeBytes    { get; init; }
    public DateTime?    OldestConversation  { get; init; }
    public DateTime?    NewestConversation  { get; init; }
}

/// <summary>Per-surface conversation statistics.</summary>
public sealed class SurfaceStats
{
    public int       Count        { get; init; }
    public DateTime? LastActivity { get; init; }
}
