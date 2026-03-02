namespace Ses.Local.Core.Interfaces;

/// <summary>
/// Generates CLAUDE.md context files in project root directories based on recent session activity.
/// Files are only written when EnableClaudeMdGeneration is true and the project has not created
/// its own CLAUDE.md (detected by absence of the ses-local generated header).
/// </summary>
public interface IClaudeMdGenerator
{
    /// <summary>
    /// Generates or updates a CLAUDE.md file in the given project directory.
    /// No-ops if generation is disabled, the path is excluded, or a user-created file exists.
    /// </summary>
    Task GenerateAsync(string projectPath, CancellationToken ct = default);
}
