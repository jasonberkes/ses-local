namespace Ses.Local.Tray.Services;

/// <summary>Represents a project directory and its CLAUDE.md file status.</summary>
public sealed class ProjectClaudeMd
{
    public string ProjectName    { get; init; } = string.Empty;
    public string ProjectPath    { get; init; } = string.Empty;
    public string? ClaudeMdPath  { get; init; }
    public DateTime? LastModified  { get; init; }
    public long? FileSizeBytes   { get; init; }
    public bool HasClaudeMd => ClaudeMdPath is not null;
}

/// <summary>
/// Scans known project directories for CLAUDE.md files.
/// Primary source: daemon /api/projects (from JSONL cwd fields).
/// Fallback: common filesystem locations scanned one level deep.
/// </summary>
public class ClaudeMdScannerService
{
    private readonly DaemonAuthProxy _proxy;

    private static readonly string[] s_fallbackRoots =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Dropbox", "Dev"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Projects"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "dev"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "code"),
    ];

    private static readonly string[] s_claudeMdCandidates = ["CLAUDE.md", "claude.md"];

    public ClaudeMdScannerService(DaemonAuthProxy proxy) => _proxy = proxy;

    /// <summary>
    /// Returns all known project directories with their CLAUDE.md status.
    /// </summary>
    public virtual async Task<IReadOnlyList<ProjectClaudeMd>> ScanAsync(CancellationToken ct = default)
    {
        var dirs = await GetProjectDirectoriesAsync(ct);
        var results = new List<ProjectClaudeMd>(dirs.Count);

        foreach (var dir in dirs)
        {
            if (ct.IsCancellationRequested) break;
            results.Add(BuildEntry(dir));
        }

        return results;
    }

    /// <summary>
    /// Builds a <see cref="ProjectClaudeMd"/> entry for a single directory.
    /// Searches CLAUDE.md, claude.md, and .claude/CLAUDE.md (case-insensitive on the file name).
    /// </summary>
    public static ProjectClaudeMd BuildEntry(string dir)
    {
        var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
        var claudeMdPath = FindClaudeMd(dir);

        DateTime? lastModified = null;
        long? fileSize = null;
        if (claudeMdPath is not null)
        {
            try
            {
                var info = new FileInfo(claudeMdPath);
                lastModified = info.LastWriteTime;
                fileSize     = info.Length;
            }
            catch { /* file may have been deleted since scan */ }
        }

        return new ProjectClaudeMd
        {
            ProjectName   = name,
            ProjectPath   = dir,
            ClaudeMdPath  = claudeMdPath,
            LastModified  = lastModified,
            FileSizeBytes = fileSize,
        };
    }

    /// <summary>Searches for a CLAUDE.md file in the given directory (case-insensitive).</summary>
    public static string? FindClaudeMd(string dir)
    {
        if (!Directory.Exists(dir)) return null;

        // Check direct candidates first
        foreach (var candidate in s_claudeMdCandidates)
        {
            var path = Path.Combine(dir, candidate);
            if (File.Exists(path)) return path;
        }

        // Also check .claude/ subdirectory
        var dotClaudeDir = Path.Combine(dir, ".claude");
        if (Directory.Exists(dotClaudeDir))
        {
            foreach (var candidate in s_claudeMdCandidates)
            {
                var path = Path.Combine(dotClaudeDir, candidate);
                if (File.Exists(path)) return path;
            }
        }

        return null;
    }

    private async Task<List<string>> GetProjectDirectoriesAsync(CancellationToken ct)
    {
        // Primary: ask daemon for known project dirs (reads cwd from JSONL files)
        var daemonPaths = await _proxy.GetKnownProjectsAsync(ct);
        if (daemonPaths is { Count: > 0 })
        {
            var valid = daemonPaths
                .Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (valid.Count > 0)
                return valid;
        }

        // Fallback: scan common top-level directories one level deep
        var dirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in s_fallbackRoots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(root).Take(50))
                    dirs.Add(sub);
            }
            catch { /* permission error */ }
        }

        return dirs.OrderBy(d => d).ToList();
    }
}
