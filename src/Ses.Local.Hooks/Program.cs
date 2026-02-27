// ses-hooks: Claude Code lifecycle hooks binary
// AOT compiled for fast startup. Spawned by Claude Code per hook event.
// Full implementation: WI-946.

using Ses.Local.Hooks.Handlers;

var hookType = args.Length > 0 ? args[0] : string.Empty;

Func<Task> handler = hookType switch
{
    "SessionStart"       => SessionStartHandler.RunAsync,
    "UserPromptSubmit"   => UserPromptSubmitHandler.RunAsync,
    "PostToolUse"        => PostToolUseHandler.RunAsync,
    "PreCompact"         => PreCompactHandler.RunAsync,
    "Stop"               => StopHandler.RunAsync,
    "SubagentStop"       => SubagentStopHandler.RunAsync,
    _                    => () => { Console.Error.WriteLine($"Unknown hook: {hookType}"); return Task.CompletedTask; }
};

await handler();
