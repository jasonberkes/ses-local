using Ses.Local.Tray.Services;
using Ses.Local.Tray.ViewModels;
using Xunit;

namespace Ses.Local.Workers.Tests.ViewModels;

public sealed class ImportWizardViewModelTests
{
    // ── Source selection ───────────────────────────────────────────────────────

    [Fact]
    public void Constructor_StartsAtSourceStep()
    {
        var vm = MakeVm();

        Assert.True(vm.IsSourceStep);
        Assert.False(vm.IsInstructionsStep);
        Assert.False(vm.IsProgressStep);
        Assert.False(vm.IsCompleteStep);
    }

    [Theory]
    [InlineData(ImportSource.Claude,  "Claude")]
    [InlineData(ImportSource.ChatGPT, "ChatGPT")]
    [InlineData(ImportSource.Gemini,  "Gemini")]
    public void SelectSource_AdvancesToInstructionsStep_AndSetsSourceLabel(ImportSource source, string expectedLabel)
    {
        var vm = MakeVm();

        vm.SelectSource(source);

        Assert.True(vm.IsInstructionsStep);
        Assert.Equal(expectedLabel, vm.SourceLabel);
    }

    [Fact]
    public void SelectSourceWithFile_PopulatesFilePathAndName()
    {
        var vm = MakeVm();

        vm.SelectSourceWithFile(ImportSource.Claude, "/tmp/export.json");

        Assert.True(vm.IsInstructionsStep);
        Assert.Equal("/tmp/export.json", vm.FilePath);
        Assert.Equal("export.json",      vm.FileName);
    }

    [Fact]
    public void Reset_ReturnsToSourceStep_AndClearsFile()
    {
        var vm = MakeVm();
        vm.SelectSource(ImportSource.Claude);

        vm.Reset();

        Assert.True(vm.IsSourceStep);
        Assert.Empty(vm.FilePath);
        Assert.Empty(vm.FileName);
    }

    // ── Import lifecycle ───────────────────────────────────────────────────────

    [Fact]
    public async Task StartImportAsync_WhenDaemonReturnsTrue_AdvancesToProgressStep()
    {
        var tcs    = new TaskCompletionSource<ImportStatusResponse?>();
        var vm     = MakeVm(
            startImport:  (_, _) => Task.FromResult(true),
            getStatus:    _ => tcs.Task,
            cancelImport: _ => Task.CompletedTask);
        vm.SelectSource(ImportSource.Claude);
        vm.FilePath.GetType(); // suppress unused warning — set indirectly
        typeof(ImportWizardViewModel).GetProperty("FilePath")!.GetSetMethod(true)?.Invoke(vm, ["/tmp/export.json"]);

        await vm.StartImportAsync();

        Assert.True(vm.IsProgressStep);
        Assert.True(vm.IsImportRunning);

        // Cleanup
        tcs.SetResult(null);
    }

    [Fact]
    public async Task StartImportAsync_WhenDaemonReturnsFalse_StaysOnProgressStep_WithError()
    {
        var vm = MakeVm(startImport: (_, _) => Task.FromResult(false));
        vm.SelectSource(ImportSource.Claude);
        typeof(ImportWizardViewModel).GetProperty("FilePath")!.GetSetMethod(true)?.Invoke(vm, ["/tmp/export.json"]);

        await vm.StartImportAsync();

        Assert.True(vm.IsProgressStep);
        Assert.False(vm.IsImportRunning);
        Assert.Contains("Failed", vm.ProgressStatus);
    }

    [Fact]
    public async Task PollProgress_WhenStatusCompletes_AdvancesToCompleteStep()
    {
        var completedStatus = new ImportStatusResponse
        {
            IsRunning        = false,
            SessionsImported = 10,
            MessagesImported = 200,
        };

        ImportStatusResponse? nextStatus = new ImportStatusResponse { IsRunning = true };
        var vm = MakeVm(
            startImport:  (_, _) => Task.FromResult(true),
            getStatus:    async ct =>
            {
                await Task.Delay(10, ct);
                var s = nextStatus;
                nextStatus = completedStatus;
                return s;
            },
            cancelImport: _ => Task.CompletedTask);

        vm.SelectSource(ImportSource.Claude);
        typeof(ImportWizardViewModel).GetProperty("FilePath")!.GetSetMethod(true)?.Invoke(vm, ["/tmp/export.json"]);

        await vm.StartImportAsync();

        // Wait for polling to complete
        for (var i = 0; i < 50 && !vm.IsCompleteStep; i++)
            await Task.Delay(50);

        Assert.True(vm.IsCompleteStep);
        Assert.Equal(10, vm.ProgressSessions);
    }

    // ── Re-import (TRAY-6) ─────────────────────────────────────────────────────

    [Fact]
    public void SelectSourceWithFile_WhenFileExists_SetsStateCorrectly()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            var vm = MakeVm();
            vm.SelectSourceWithFile(ImportSource.Claude, tmpFile);

            Assert.True(vm.IsInstructionsStep);
            Assert.Equal(tmpFile, vm.FilePath);
            Assert.Equal(Path.GetFileName(tmpFile), vm.FileName);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    // ── ImportMore ─────────────────────────────────────────────────────────────

    [Fact]
    public void ImportMore_FromCompleteStep_GoesToInstructionsWithSameSource()
    {
        var vm = MakeVm();
        vm.SelectSource(ImportSource.ChatGPT);
        // Manually set to Complete step via reflection to simulate completed import
        typeof(ImportWizardViewModel).GetProperty("Step", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetSetMethod(true)?.Invoke(vm, [ImportWizardStep.Complete]);

        vm.ImportMore();

        Assert.True(vm.IsInstructionsStep);
        Assert.Equal("ChatGPT", vm.SourceLabel);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static ImportWizardViewModel MakeVm(
        Func<string, CancellationToken, Task<bool>>? startImport   = null,
        Func<CancellationToken, Task<ImportStatusResponse?>>? getStatus = null,
        Func<CancellationToken, Task>? cancelImport = null) =>
        new(
            startImport   ?? ((_, _) => Task.FromResult(true)),
            getStatus     ?? (_ => Task.FromResult<ImportStatusResponse?>(null)),
            cancelImport  ?? (_ => Task.CompletedTask));
}
