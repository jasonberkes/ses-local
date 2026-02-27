namespace Ses.Local.Hooks.Handlers;

/// <summary>Injects top-N memories from local SQLite into CC session. Implementation: WI-946.</summary>
internal static class SessionStartHandler
{
    internal static Task RunAsync() => Task.CompletedTask;
}
