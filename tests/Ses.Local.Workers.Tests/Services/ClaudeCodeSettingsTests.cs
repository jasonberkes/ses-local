using Ses.Local.Core.Models;
using Ses.Local.Workers.Services;
using Xunit;

namespace Ses.Local.Workers.Tests.Services;

public sealed class ClaudeCodeSettingsTests
{
    [Fact]
    public void LoadOrCreate_WhenFileMissing_ReturnsEmptySettings()
    {
        var settings = ClaudeCodeSettings.LoadOrCreate("/nonexistent/path/settings.json");
        Assert.False(settings.HasCorrectHooks("/usr/local/bin/ses-hooks"));
    }

    [Fact]
    public void UpsertSesHooks_AddsAllSixEvents()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var settings = ClaudeCodeSettings.LoadOrCreate(tempFile);
            settings.UpsertSesHooks("/home/user/.ses/bin/ses-hooks");
            settings.Save(tempFile);

            // Reload and verify
            var reloaded = ClaudeCodeSettings.LoadOrCreate(tempFile);
            Assert.True(reloaded.HasCorrectHooks("/home/user/.ses/bin/ses-hooks"));
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void HasCorrectHooks_WrongPath_ReturnsFalse()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var settings = ClaudeCodeSettings.LoadOrCreate(tempFile);
            settings.UpsertSesHooks("/old/path/ses-hooks");
            settings.Save(tempFile);

            var reloaded = ClaudeCodeSettings.LoadOrCreate(tempFile);
            Assert.False(reloaded.HasCorrectHooks("/new/path/ses-hooks"));
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void UpsertSesHooks_PreservesExistingUnrelatedHooks()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // Write settings with an existing unrelated hook
            File.WriteAllText(tempFile, """
                {
                  "hooks": {
                    "SessionStart": [
                      { "hooks": [{ "type": "command", "command": "/usr/local/bin/other-tool" }] }
                    ]
                  }
                }
                """);

            var settings = ClaudeCodeSettings.LoadOrCreate(tempFile);
            settings.UpsertSesHooks("/home/.ses/bin/ses-hooks");
            settings.Save(tempFile);

            var json = File.ReadAllText(tempFile);
            // Both the original hook AND ses-hooks should be present
            Assert.Contains("other-tool", json);
            Assert.Contains("ses-hooks", json);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void UpsertSesHooks_RepairsStaleEntry()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var settings = ClaudeCodeSettings.LoadOrCreate(tempFile);
            settings.UpsertSesHooks("/old/path/ses-hooks");
            settings.Save(tempFile);

            // Now repair with new path
            var reloaded = ClaudeCodeSettings.LoadOrCreate(tempFile);
            reloaded.UpsertSesHooks("/new/path/ses-hooks");
            reloaded.Save(tempFile);

            var final = ClaudeCodeSettings.LoadOrCreate(tempFile);
            Assert.True(final.HasCorrectHooks("/new/path/ses-hooks"));
            Assert.False(final.HasCorrectHooks("/old/path/ses-hooks"));

            // Only one ses-hooks entry per event (no duplicates)
            var json = File.ReadAllText(tempFile);
            Assert.Equal(1, json.Split("/new/path/ses-hooks SessionStart").Length - 1);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void GetClaudeCodeSettingsPath_ContainsDotClaude()
    {
        var path = SesMcpManager.GetClaudeCodeSettingsPath();
        Assert.Contains(".claude", path);
        Assert.Contains("settings.json", path);
    }
}
