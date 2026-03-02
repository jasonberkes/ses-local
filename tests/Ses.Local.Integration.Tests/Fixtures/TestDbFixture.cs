using Microsoft.Extensions.Logging.Abstractions;
using Ses.Local.Workers.Services;

namespace Ses.Local.Integration.Tests.Fixtures;

/// <summary>
/// Creates a real SQLite database in a per-test temp directory.
/// Implements IAsyncDisposable so xunit cleans up after each test class.
/// </summary>
public sealed class TestDbFixture : IAsyncDisposable
{
    public string TempDir { get; }
    public string DbPath { get; }
    public LocalDbService Db { get; }

    public TestDbFixture()
    {
        TempDir = Path.Combine(Path.GetTempPath(), "ses-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TempDir);
        DbPath = Path.Combine(TempDir, "local-test.db");
        Db = new LocalDbService(DbPath, NullLogger<LocalDbService>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        try { Directory.Delete(TempDir, recursive: true); } catch { /* best effort */ }
    }
}
