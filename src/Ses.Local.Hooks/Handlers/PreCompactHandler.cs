namespace Ses.Local.Hooks.Handlers;

/// <summary>Handles PreCompact hook event. Implementation: WI-946.</summary>
internal static class PreCompactHandler
{
    internal static Task RunAsync() => Task.CompletedTask;
}
