namespace Ses.Local.Integration.Tests.Fixtures;

/// <summary>
/// Generates realistic Claude Code JSONL content for integration tests.
/// </summary>
public static class SampleJsonlGenerator
{
    /// <summary>
    /// Builds a JSONL file with user message, tool_use (Read), tool_result, and assistant text response.
    /// </summary>
    public static string BuildBasicSession(string cwd = "/Users/test/myproject", string timestamp = "2026-01-01T10:00:00Z")
    {
        return $$$"""
            {"type":"user","message":{"role":"user","content":"Can you read and summarize this file?"},"sessionId":"test-basic","uuid":"u1","timestamp":"{{{timestamp}}}","cwd":"{{{cwd}}}","gitBranch":"main"}
            {"type":"assistant","message":{"role":"assistant","model":"claude-sonnet-4-6","usage":{"input_tokens":50,"output_tokens":30},"content":[{"type":"tool_use","id":"tu_001","name":"Read","input":{"file_path":"/src/Program.cs"}}]},"uuid":"a1","timestamp":"2026-01-01T10:00:01Z"}
            {"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tu_001","content":"using System;\nnamespace App { class Program { static void Main() => Console.WriteLine(\"Hello\"); } }"}]},"uuid":"u2","timestamp":"2026-01-01T10:00:02Z"}
            {"type":"assistant","message":{"role":"assistant","model":"claude-sonnet-4-6","usage":{"input_tokens":80,"output_tokens":40},"content":[{"type":"text","text":"The file contains a simple C# console app that prints Hello to stdout."}]},"uuid":"a2","timestamp":"2026-01-01T10:00:03Z"}
            """;
    }

    /// <summary>
    /// Builds a JSONL with git commit and test runner tool_use blocks.
    /// </summary>
    public static string BuildSessionWithSpecialTools(string cwd = "/Users/test/project", string timestamp = "2026-02-01T09:00:00Z")
    {
        return $$$"""
            {"type":"user","message":{"role":"user","content":"Run the tests and then commit."},"sessionId":"test-special","uuid":"u1","timestamp":"{{{timestamp}}}","cwd":"{{{cwd}}}","gitBranch":"feature/test"}
            {"type":"assistant","message":{"role":"assistant","model":"claude-sonnet-4-6","usage":{"input_tokens":20,"output_tokens":15},"content":[{"type":"tool_use","id":"tu_002","name":"Bash","input":{"command":"dotnet test /repo/tests","timeout":60000}}]},"uuid":"a1","timestamp":"2026-02-01T09:00:01Z"}
            {"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tu_002","content":"Passed: 42, Failed: 0, Skipped: 1"}]},"uuid":"u2","timestamp":"2026-02-01T09:00:05Z"}
            {"type":"assistant","message":{"role":"assistant","model":"claude-sonnet-4-6","usage":{"input_tokens":35,"output_tokens":12},"content":[{"type":"tool_use","id":"tu_003","name":"Bash","input":{"command":"git commit -m \"fix: update pipeline\""}}]},"uuid":"a2","timestamp":"2026-02-01T09:00:06Z"}
            {"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tu_003","content":"[feature/test abc1234] fix: update pipeline\n 1 file changed"}]},"uuid":"u3","timestamp":"2026-02-01T09:00:07Z"}
            {"type":"assistant","message":{"role":"assistant","model":"claude-sonnet-4-6","usage":{"input_tokens":50,"output_tokens":20},"content":[{"type":"text","text":"All tests passed and the commit was created successfully."}]},"uuid":"a3","timestamp":"2026-02-01T09:00:08Z"}
            """;
    }

    /// <summary>
    /// Builds a JSONL where a tool_result contains error text.
    /// </summary>
    public static string BuildSessionWithError(string cwd = "/Users/test/project", string timestamp = "2026-03-01T14:00:00Z")
    {
        return $$$"""
            {"type":"user","message":{"role":"user","content":"Try to build the project."},"sessionId":"test-error","uuid":"u1","timestamp":"{{{timestamp}}}","cwd":"{{{cwd}}}","gitBranch":"main"}
            {"type":"assistant","message":{"role":"assistant","model":"claude-sonnet-4-6","usage":{"input_tokens":15,"output_tokens":10},"content":[{"type":"tool_use","id":"tu_004","name":"Bash","input":{"command":"dotnet build /repo/src"}}]},"uuid":"a1","timestamp":"2026-03-01T14:00:01Z"}
            {"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tu_004","content":"Build FAILED.\nerror CS0246: The type or namespace name 'Foo' could not be found"}]},"uuid":"u2","timestamp":"2026-03-01T14:00:03Z"}
            {"type":"assistant","message":{"role":"assistant","model":"claude-sonnet-4-6","usage":{"input_tokens":60,"output_tokens":25},"content":[{"type":"text","text":"The build failed due to a missing type. Let me fix it."}]},"uuid":"a2","timestamp":"2026-03-01T14:00:04Z"}
            """;
    }

    /// <summary>
    /// Builds a JSONL with a thinking block (extended thinking).
    /// </summary>
    public static string BuildSessionWithThinking(string cwd = "/Users/test/project", string timestamp = "2026-03-01T15:00:00Z")
    {
        return $$$"""
            {"type":"user","message":{"role":"user","content":"Design the architecture for a caching layer."},"sessionId":"test-thinking","uuid":"u1","timestamp":"{{{timestamp}}}","cwd":"{{{cwd}}}","gitBranch":"main"}
            {"type":"assistant","message":{"role":"assistant","model":"claude-sonnet-4-6","usage":{"input_tokens":30,"output_tokens":80},"content":[{"type":"thinking","thinking":"The user wants a caching layer. I should consider Redis, in-memory, and hybrid approaches..."},{"type":"text","text":"Here is a proposed caching architecture..."}]},"uuid":"a1","timestamp":"2026-03-01T15:00:01Z"}
            """;
    }
}
