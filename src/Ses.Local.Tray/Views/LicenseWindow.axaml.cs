using Avalonia.Controls;
using Avalonia.Interactivity;
using Ses.Local.Tray.Services;

namespace Ses.Local.Tray.Views;

public partial class LicenseWindow : Window
{
    private readonly DaemonAuthProxy? _proxy;
    private bool _activating;

    // Parameterless constructor required by Avalonia XAML loader
    public LicenseWindow()
    {
        InitializeComponent();
    }

    public LicenseWindow(DaemonAuthProxy proxy)
    {
        _proxy = proxy;
        InitializeComponent();
    }

    private async void OnActivateClick(object? sender, RoutedEventArgs e)
    {
        if (_activating || _proxy is null) return;

        var key = LicenseKeyBox?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
        {
            ShowError("Please enter your license key.");
            return;
        }

        _activating = true;
        ActivateButton!.IsEnabled = false;
        ActivateButton.Content = "Activating…";
        HideError();

        try
        {
            var (succeeded, error) = await _proxy.ActivateLicenseAsync(key);
            if (succeeded)
            {
                Close();
            }
            else
            {
                ShowError(ParseError(error) ?? "Activation failed. Please check your license key and try again.");
            }
        }
        finally
        {
            _activating = false;
            if (IsVisible)
            {
                ActivateButton.IsEnabled = true;
                ActivateButton.Content = "Activate";
            }
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void ShowError(string message)
    {
        if (StatusText is null) return;
        StatusText.Text = message;
        StatusText.IsVisible = true;
    }

    private void HideError()
    {
        if (StatusText is null) return;
        StatusText.IsVisible = false;
    }

    private static string? ParseError(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("error", out var err))
                return err.GetString();
        }
        catch { /* not JSON */ }
        return raw.Length > 200 ? raw[..200] : raw;
    }
}
