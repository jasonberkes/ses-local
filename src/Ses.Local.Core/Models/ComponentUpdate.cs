namespace Ses.Local.Core.Models;

/// <summary>Represents the update availability status for a single binary component.</summary>
public sealed record ComponentUpdate(
    string  Name,
    string? InstalledVersion,
    string? LatestVersion,
    bool    UpdateAvailable);
