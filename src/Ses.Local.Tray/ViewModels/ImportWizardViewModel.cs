using System.ComponentModel;
using System.Runtime.CompilerServices;
using Ses.Local.Tray.Services;

namespace Ses.Local.Tray.ViewModels;

public enum ImportSource { Claude, ChatGPT, Gemini }

public enum ImportWizardStep { Source, Instructions, Progress, Complete }

/// <summary>
/// ViewModel for the guided conversation import wizard in the Import tab.
///
/// Steps:
///   Source       → user picks Claude / ChatGPT / Gemini
///   Instructions → per-source how-to + file picker
///   Progress     → live import progress (polls daemon every 500 ms)
///   Complete     → summary of imported conversations
/// </summary>
public sealed class ImportWizardViewModel : INotifyPropertyChanged
{
    // ── Injectable delegates (kept as Func for testability) ───────────────────

    /// <summary>
    /// Opens a native file picker. Accepts allowed extensions (e.g. "*.json") and returns
    /// the selected path, or null if the user cancelled.
    /// Set by DropdownPanel.axaml.cs after construction.
    /// </summary>
    public Func<string[], Task<string?>>? FilePicker { get; set; }

    private readonly Func<string, CancellationToken, Task<bool>>       _startImport;
    private readonly Func<CancellationToken, Task<ImportStatusResponse?>> _getStatus;
    private readonly Func<CancellationToken, Task>                      _cancelImport;

    // ── Step state ─────────────────────────────────────────────────────────────

    private ImportWizardStep _step = ImportWizardStep.Source;
    private ImportSource     _source;
    private string           _filePath   = string.Empty;
    private string           _fileName   = string.Empty;
    private string           _lastImportInfo = string.Empty;

    // ── Progress state ─────────────────────────────────────────────────────────

    private int    _progressSessions;
    private int    _progressMessages;
    private int    _progressDuplicates;
    private int    _progressErrors;
    private bool   _isImportRunning;
    private string _progressStatus = string.Empty;

    private CancellationTokenSource? _pollCts;

    public event PropertyChangedEventHandler? PropertyChanged;

    // ── Constructor ────────────────────────────────────────────────────────────

    /// <summary>
    /// Production constructor — wires to <see cref="DaemonAuthProxy"/> methods.
    /// </summary>
    public ImportWizardViewModel(DaemonAuthProxy proxy)
        : this(
            (path, ct) => proxy.StartImportAsync(path, ct),
            ct          => proxy.GetImportStatusAsync(ct),
            ct          => proxy.CancelImportAsync(ct))
    {
    }

    /// <summary>
    /// Testable constructor — accept pure functions so tests can inject fakes.
    /// </summary>
    public ImportWizardViewModel(
        Func<string, CancellationToken, Task<bool>>        startImport,
        Func<CancellationToken, Task<ImportStatusResponse?>> getStatus,
        Func<CancellationToken, Task>                       cancelImport)
    {
        _startImport  = startImport;
        _getStatus    = getStatus;
        _cancelImport = cancelImport;
    }

    // ── Step properties ────────────────────────────────────────────────────────

    public ImportWizardStep Step
    {
        get => _step;
        private set
        {
            _step = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSourceStep));
            OnPropertyChanged(nameof(IsInstructionsStep));
            OnPropertyChanged(nameof(IsProgressStep));
            OnPropertyChanged(nameof(IsCompleteStep));
        }
    }

    public bool IsSourceStep       => _step == ImportWizardStep.Source;
    public bool IsInstructionsStep => _step == ImportWizardStep.Instructions;
    public bool IsProgressStep     => _step == ImportWizardStep.Progress;
    public bool IsCompleteStep     => _step == ImportWizardStep.Complete;

    // ── Source selection ────────────────────────────────────────────────────────

    public ImportSource SelectedSource
    {
        get => _source;
        private set
        {
            _source = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(InstructionsText));
            OnPropertyChanged(nameof(SourceLabel));
            OnPropertyChanged(nameof(FileExtensions));
        }
    }

    public string SourceLabel => _source switch
    {
        ImportSource.ChatGPT => "ChatGPT",
        ImportSource.Gemini  => "Gemini",
        _                    => "Claude"
    };

    public string[] FileExtensions => _source switch
    {
        ImportSource.ChatGPT => ["*.zip"],
        ImportSource.Gemini  => ["*.zip"],
        _                    => ["*.json"]
    };

    public string InstructionsText => _source switch
    {
        ImportSource.ChatGPT =>
            "How to export from ChatGPT:\n" +
            "1. Go to chat.openai.com → Settings → Data Controls\n" +
            "2. Click \"Export data\"\n" +
            "3. You'll receive an email with a download link\n" +
            "4. Download the .zip file\n" +
            "5. Select it below",
        ImportSource.Gemini =>
            "How to export from Gemini:\n" +
            "1. Go to takeout.google.com\n" +
            "2. Select \"Gemini Apps\" only\n" +
            "3. Click \"Create export\"\n" +
            "4. Download the .zip file\n" +
            "5. Select it below",
        _ =>
            "How to export from Claude:\n" +
            "1. Go to claude.ai → Settings → Account\n" +
            "2. Click \"Export Data\"\n" +
            "3. You'll receive an email with a download link\n" +
            "4. Download the .json file\n" +
            "5. Select it below"
    };

    // ── File selection ─────────────────────────────────────────────────────────

    public string FilePath
    {
        get => _filePath;
        private set { _filePath = value; OnPropertyChanged(); }
    }

    public string FileName
    {
        get => _fileName;
        private set { _fileName = value; OnPropertyChanged(); }
    }

    // ── Progress ────────────────────────────────────────────────────────────────

    public int ProgressSessions
    {
        get => _progressSessions;
        private set { _progressSessions = value; OnPropertyChanged(); }
    }

    public int ProgressMessages
    {
        get => _progressMessages;
        private set { _progressMessages = value; OnPropertyChanged(); }
    }

    public int ProgressDuplicates
    {
        get => _progressDuplicates;
        private set { _progressDuplicates = value; OnPropertyChanged(); }
    }

    public int ProgressErrors
    {
        get => _progressErrors;
        private set { _progressErrors = value; OnPropertyChanged(); }
    }

    public bool IsImportRunning
    {
        get => _isImportRunning;
        private set { _isImportRunning = value; OnPropertyChanged(); }
    }

    public string ProgressStatus
    {
        get => _progressStatus;
        private set { _progressStatus = value; OnPropertyChanged(); }
    }

    public string LastImportInfo
    {
        get => _lastImportInfo;
        set { _lastImportInfo = value; OnPropertyChanged(); }
    }

    // ── Commands / actions ─────────────────────────────────────────────────────

    /// <summary>Step 1 → 2: select source and advance to instructions.</summary>
    public void SelectSource(ImportSource source)
    {
        SelectedSource = source;
        FilePath       = string.Empty;
        FileName       = string.Empty;
        Step           = ImportWizardStep.Instructions;
    }

    /// <summary>Step 2: invoke the file picker and store the selected path.</summary>
    public async Task PickFileAsync()
    {
        if (FilePicker is null) return;

        var path = await FilePicker(FileExtensions);
        if (path is null) return;

        FilePath = path;
        FileName = Path.GetFileName(path);
    }

    /// <summary>Step 2 → 3: start the import and begin polling for progress.</summary>
    public async Task StartImportAsync()
    {
        if (string.IsNullOrEmpty(FilePath)) return;

        ResetProgress();
        Step            = ImportWizardStep.Progress;
        IsImportRunning = true;
        ProgressStatus  = $"Importing {SourceLabel} conversations...";

        var started = await _startImport(FilePath, CancellationToken.None);
        if (!started)
        {
            ProgressStatus  = "Failed to start import (daemon unavailable or import already running).";
            IsImportRunning = false;
            return;
        }

        _pollCts = new CancellationTokenSource();
        _ = PollProgressAsync(_pollCts.Token);
    }

    /// <summary>Step 3: cancel the in-progress import.</summary>
    public async Task CancelAsync()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts        = null;
        await _cancelImport(CancellationToken.None);
        IsImportRunning = false;
        Step            = ImportWizardStep.Instructions;
    }

    /// <summary>Step 4 → 1: reset wizard to choose another file.</summary>
    public void Reset()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts  = null;
        FilePath  = string.Empty;
        FileName  = string.Empty;
        ResetProgress();
        Step = ImportWizardStep.Source;
    }

    /// <summary>Step 4 "Import More" → go to instructions for same source.</summary>
    public void ImportMore()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
        FilePath = string.Empty;
        FileName = string.Empty;
        ResetProgress();
        Step = ImportWizardStep.Instructions;
    }

    // ── Polling ────────────────────────────────────────────────────────────────

    private async Task PollProgressAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(ct);
                var status = await _getStatus(ct);
                if (status is null) continue;

                ProgressSessions   = status.SessionsImported;
                ProgressMessages   = status.MessagesImported;
                ProgressDuplicates = status.Duplicates;
                ProgressErrors     = status.Errors;

                if (!status.IsRunning)
                {
                    IsImportRunning = false;
                    Step            = ImportWizardStep.Complete;
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Daemon temporarily unreachable — keep polling
            }
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private void ResetProgress()
    {
        ProgressSessions   = 0;
        ProgressMessages   = 0;
        ProgressDuplicates = 0;
        ProgressErrors     = 0;
        IsImportRunning    = false;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
