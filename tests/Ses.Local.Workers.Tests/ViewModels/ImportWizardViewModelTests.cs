using Ses.Local.Tray.Services;
using Ses.Local.Tray.ViewModels;
using Xunit;

namespace Ses.Local.Workers.Tests.ViewModels;

public sealed class ImportWizardViewModelTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static ImportWizardViewModel CreateVm(
        Func<string, CancellationToken, Task<bool>>?          startImport  = null,
        Func<CancellationToken, Task<ImportStatusResponse?>>? getStatus    = null,
        Func<CancellationToken, Task>?                        cancelImport = null)
    {
        return new ImportWizardViewModel(
            startImport  ?? ((_, _) => Task.FromResult(true)),
            getStatus    ?? (_ => Task.FromResult<ImportStatusResponse?>(null)),
            cancelImport ?? (_ => Task.CompletedTask));
    }

    private static ImportStatusResponse DoneStatus(int sessions = 10) => new()
    {
        IsRunning        = false,
        SessionsImported = sessions,
        MessagesImported = sessions * 5,
        Duplicates       = 1,
        Errors           = 0,
        Format           = "claude",
    };

    // ── Step navigation ───────────────────────────────────────────────────────

    [Fact]
    public void DefaultStep_IsSource()
    {
        var vm = CreateVm();

        Assert.Equal(ImportWizardStep.Source, vm.Step);
        Assert.True(vm.IsSourceStep);
        Assert.False(vm.IsInstructionsStep);
        Assert.False(vm.IsProgressStep);
        Assert.False(vm.IsCompleteStep);
    }

    [Fact]
    public void SelectSource_Claude_AdvancesToInstructionsStep()
    {
        var vm = CreateVm();

        vm.SelectSource(ImportSource.Claude);

        Assert.Equal(ImportWizardStep.Instructions, vm.Step);
        Assert.True(vm.IsInstructionsStep);
        Assert.Equal(ImportSource.Claude, vm.SelectedSource);
    }

    [Fact]
    public void SelectSource_ChatGPT_SetsCorrectLabel()
    {
        var vm = CreateVm();

        vm.SelectSource(ImportSource.ChatGPT);

        Assert.Equal("ChatGPT", vm.SourceLabel);
        Assert.Contains(".zip", vm.FileExtensions[0]);
    }

    [Fact]
    public void SelectSource_Gemini_SetsCorrectLabel()
    {
        var vm = CreateVm();

        vm.SelectSource(ImportSource.Gemini);

        Assert.Equal("Gemini", vm.SourceLabel);
        Assert.Contains(".zip", vm.FileExtensions[0]);
    }

    [Fact]
    public void SelectSource_Claude_SetsJsonExtension()
    {
        var vm = CreateVm();

        vm.SelectSource(ImportSource.Claude);

        Assert.Contains("*.json", vm.FileExtensions);
    }

    [Fact]
    public void Reset_ReturnsToSourceStep()
    {
        var vm = CreateVm();
        vm.SelectSource(ImportSource.Claude);

        vm.Reset();

        Assert.Equal(ImportWizardStep.Source, vm.Step);
    }

    [Fact]
    public void Reset_ClearsFilePath()
    {
        var vm = CreateVm();
        vm.SelectSource(ImportSource.Claude);
        vm.FilePicker = _ => Task.FromResult<string?>("/tmp/test.json");

        vm.Reset();

        Assert.Equal(string.Empty, vm.FilePath);
        Assert.Equal(string.Empty, vm.FileName);
    }

    // ── Source → Instructions content ─────────────────────────────────────────

    [Theory]
    [InlineData(ImportSource.Claude,  "claude.ai")]
    [InlineData(ImportSource.ChatGPT, "chat.openai.com")]
    [InlineData(ImportSource.Gemini,  "takeout.google.com")]
    public void InstructionsText_ContainsPlatformUrl(ImportSource source, string expectedUrl)
    {
        var vm = CreateVm();

        vm.SelectSource(source);

        Assert.Contains(expectedUrl, vm.InstructionsText, StringComparison.OrdinalIgnoreCase);
    }

    // ── File picking ──────────────────────────────────────────────────────────

    [Fact]
    public async Task PickFileAsync_WhenPickerReturnsPath_SetsFileName()
    {
        var vm = CreateVm();
        vm.SelectSource(ImportSource.Claude);
        vm.FilePicker = _ => Task.FromResult<string?>("/Users/test/conversations.json");

        await vm.PickFileAsync();

        Assert.Equal("/Users/test/conversations.json", vm.FilePath);
        Assert.Equal("conversations.json", vm.FileName);
    }

    [Fact]
    public async Task PickFileAsync_WhenPickerReturnsCancelled_DoesNotSetPath()
    {
        var vm = CreateVm();
        vm.SelectSource(ImportSource.Claude);
        vm.FilePicker = _ => Task.FromResult<string?>(null);

        await vm.PickFileAsync();

        Assert.Equal(string.Empty, vm.FilePath);
    }

    // ── Import triggering ─────────────────────────────────────────────────────

    [Fact]
    public async Task StartImportAsync_CallsStartImportDelegate()
    {
        var called = false;
        var vm = CreateVm(
            startImport: (path, _) =>
            {
                called = true;
                Assert.Equal("/tmp/conversations.json", path);
                return Task.FromResult(true);
            },
            getStatus: _ => Task.FromResult<ImportStatusResponse?>(null));

        vm.SelectSource(ImportSource.Claude);
        vm.FilePicker = _ => Task.FromResult<string?>("/tmp/conversations.json");
        await vm.PickFileAsync();
        await vm.StartImportAsync();

        Assert.True(called);
    }

    [Fact]
    public async Task StartImportAsync_AdvancesToProgressStep()
    {
        var vm = CreateVm(
            getStatus: _ => Task.FromResult<ImportStatusResponse?>(null));

        vm.SelectSource(ImportSource.Claude);
        vm.FilePicker = _ => Task.FromResult<string?>("/tmp/conversations.json");
        await vm.PickFileAsync();
        await vm.StartImportAsync();

        Assert.Equal(ImportWizardStep.Progress, vm.Step);
    }

    [Fact]
    public async Task StartImportAsync_WhenDaemonFails_StaysOnProgressWithErrorStatus()
    {
        var vm = CreateVm(
            startImport: (_, _) => Task.FromResult(false),
            getStatus: _ => Task.FromResult<ImportStatusResponse?>(null));

        vm.SelectSource(ImportSource.Claude);
        vm.FilePicker = _ => Task.FromResult<string?>("/tmp/conversations.json");
        await vm.PickFileAsync();
        await vm.StartImportAsync();

        Assert.Equal(ImportWizardStep.Progress, vm.Step);
        Assert.False(vm.IsImportRunning);
        Assert.Contains("Failed", vm.ProgressStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartImportAsync_WithNoFilePath_DoesNotAdvance()
    {
        var vm = CreateVm();
        vm.SelectSource(ImportSource.Claude);
        // No file picked

        await vm.StartImportAsync();

        Assert.Equal(ImportWizardStep.Instructions, vm.Step);
    }

    // ── Progress updates ──────────────────────────────────────────────────────

    [Fact]
    public async Task PollProgress_WhenImportComplete_AdvancesToCompleteStep()
    {
        var tcs = new TaskCompletionSource<bool>();
        var statusCallCount = 0;

        var vm = CreateVm(
            startImport: (_, _) => Task.FromResult(true),
            getStatus: _ =>
            {
                statusCallCount++;
                // Return running on first poll, done on second
                if (statusCallCount < 2)
                    return Task.FromResult<ImportStatusResponse?>(new ImportStatusResponse { IsRunning = true });

                var result = DoneStatus(42);
                tcs.TrySetResult(true);
                return Task.FromResult<ImportStatusResponse?>(result);
            });

        vm.SelectSource(ImportSource.Claude);
        vm.FilePicker = _ => Task.FromResult<string?>("/tmp/conversations.json");
        await vm.PickFileAsync();
        await vm.StartImportAsync();

        // Wait for polling to detect completion (up to 5 seconds)
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100); // let the ViewModel state update

        Assert.Equal(ImportWizardStep.Complete, vm.Step);
        Assert.Equal(42, vm.ProgressSessions);
    }

    [Fact]
    public async Task PollProgress_UpdatesProgressProperties()
    {
        var callCount = 0;

        var vm = CreateVm(
            startImport: (_, _) => Task.FromResult(true),
            getStatus: _ =>
            {
                callCount++;
                return Task.FromResult<ImportStatusResponse?>(new ImportStatusResponse
                {
                    IsRunning        = true,
                    SessionsImported = callCount * 10,
                    MessagesImported = callCount * 100,
                    Duplicates       = callCount,
                    Errors           = 0,
                });
            });

        vm.SelectSource(ImportSource.Claude);
        vm.FilePicker = _ => Task.FromResult<string?>("/tmp/conversations.json");
        await vm.PickFileAsync();
        await vm.StartImportAsync();

        await Task.Delay(1200); // let at least 2 poll ticks complete

        Assert.True(vm.ProgressSessions > 0);
    }

    // ── Cancel ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelAsync_CallsCancelDelegate()
    {
        var cancelCalled = false;
        var vm = CreateVm(
            cancelImport: _ => { cancelCalled = true; return Task.CompletedTask; });

        vm.SelectSource(ImportSource.Claude);
        vm.FilePicker = _ => Task.FromResult<string?>("/tmp/conversations.json");
        await vm.PickFileAsync();
        await vm.StartImportAsync();
        await vm.CancelAsync();

        Assert.True(cancelCalled);
    }

    [Fact]
    public async Task CancelAsync_ReturnsToInstructionsStep()
    {
        var vm = CreateVm(
            cancelImport: _ => Task.CompletedTask);

        vm.SelectSource(ImportSource.Claude);
        vm.FilePicker = _ => Task.FromResult<string?>("/tmp/conversations.json");
        await vm.PickFileAsync();
        await vm.StartImportAsync();
        await vm.CancelAsync();

        Assert.Equal(ImportWizardStep.Instructions, vm.Step);
    }

    // ── ImportMore / Done ─────────────────────────────────────────────────────

    [Fact]
    public async Task ImportMore_ReturnsToInstructionsStep()
    {
        var tcs = new TaskCompletionSource<bool>();

        var vm = CreateVm(
            startImport: (_, _) => Task.FromResult(true),
            getStatus: _ =>
            {
                tcs.TrySetResult(true);
                return Task.FromResult<ImportStatusResponse?>(DoneStatus());
            });

        vm.SelectSource(ImportSource.Claude);
        vm.FilePicker = _ => Task.FromResult<string?>("/tmp/conversations.json");
        await vm.PickFileAsync();
        await vm.StartImportAsync();
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        vm.ImportMore();

        Assert.Equal(ImportWizardStep.Instructions, vm.Step);
    }

    // ── PropertyChanged notifications ─────────────────────────────────────────

    [Fact]
    public void SelectSource_FiresPropertyChangedForStep()
    {
        var vm       = CreateVm();
        var changed  = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SelectSource(ImportSource.Claude);

        Assert.Contains(nameof(ImportWizardViewModel.Step),               changed);
        Assert.Contains(nameof(ImportWizardViewModel.IsInstructionsStep), changed);
    }
}
