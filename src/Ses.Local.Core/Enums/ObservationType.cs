namespace Ses.Local.Core.Enums;

/// <summary>Classifies the type of a structured observation extracted from a Claude Code session.</summary>
public enum ObservationType
{
    /// <summary>A tool invocation (e.g. Read, Write, Bash, Search).</summary>
    ToolUse,

    /// <summary>The result returned by a tool invocation.</summary>
    ToolResult,

    /// <summary>A plain text response block from the assistant.</summary>
    Text,

    /// <summary>An extended thinking block from the assistant.</summary>
    Thinking,

    /// <summary>A Bash tool_use that invokes git commit.</summary>
    GitCommit,

    /// <summary>A Bash tool_use that invokes a test runner (dotnet test, npm test, pytest, etc.).</summary>
    TestResult,

    /// <summary>A tool_result whose content contains error/exception/failed keywords.</summary>
    Error
}
