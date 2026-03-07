namespace Ses.Local.Core.Models;

/// <summary>Represents a recent Claude Code session grouped by project name.</summary>
public sealed record ActiveSession(string ProjectName, string? FullPath, DateTime LastActivity);
