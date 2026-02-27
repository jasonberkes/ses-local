using Ses.Local.Hooks.Handlers;
using Xunit;

namespace Ses.Local.Workers.Tests.Workers;

public sealed class HooksTests
{
    [Fact]
    public async Task SessionStartHandler_WithEmptyInput_WritesEmptyJson()
    {
        var originalIn  = Console.In;
        var originalOut = Console.Out;
        try
        {
            Console.SetIn(new StringReader("{}"));
            var output = new StringWriter();
            Console.SetOut(output);

            await SessionStartHandler.RunAsync();

            var result = output.ToString().Trim();
            Assert.False(string.IsNullOrEmpty(result));
            // Should be valid JSON
            var parsed = System.Text.Json.JsonDocument.Parse(result);
            Assert.NotNull(parsed);
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task UserPromptSubmitHandler_WithEmptyPrompt_WritesEmptyObject()
    {
        var originalIn  = Console.In;
        var originalOut = Console.Out;
        try
        {
            Console.SetIn(new StringReader("{\"session_id\":\"test\",\"prompt\":\"\"}"));
            var output = new StringWriter();
            Console.SetOut(output);

            await UserPromptSubmitHandler.RunAsync();

            var result = output.ToString().Trim();
            Assert.Equal("{}", result);
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task PostToolUseHandler_WithValidInput_CompletesWithoutThrowing()
    {
        var originalIn = Console.In;
        try
        {
            Console.SetIn(new StringReader(
                "{\"session_id\":\"test\",\"tool\":\"Bash\",\"input\":{\"command\":\"ls\"},\"output\":\"file.cs\"}"));
            var ex = await Record.ExceptionAsync(PostToolUseHandler.RunAsync);
            Assert.Null(ex);
        }
        finally { Console.SetIn(originalIn); }
    }

    [Fact]
    public async Task PreCompactHandler_WithDecisionContent_CompletesWithoutThrowing()
    {
        var originalIn = Console.In;
        try
        {
            Console.SetIn(new StringReader(
                "{\"session_id\":\"test\",\"context_summary\":\"We decided to use PBKDF2 for key derivation. Architecture pattern: always use factory methods.\"}"));
            var ex = await Record.ExceptionAsync(PreCompactHandler.RunAsync);
            Assert.Null(ex);
        }
        finally { Console.SetIn(originalIn); }
    }

    [Fact]
    public async Task StopHandler_WithValidInput_CompletesWithoutThrowing()
    {
        var originalIn = Console.In;
        try
        {
            Console.SetIn(new StringReader("{\"session_id\":\"test-session\",\"num_turns\":5}"));
            var ex = await Record.ExceptionAsync(StopHandler.RunAsync);
            Assert.Null(ex);
        }
        finally { Console.SetIn(originalIn); }
    }

    [Fact]
    public async Task SubagentStopHandler_WithValidInput_CompletesWithoutThrowing()
    {
        var originalIn = Console.In;
        try
        {
            Console.SetIn(new StringReader(
                "{\"session_id\":\"sub\",\"parent_session_id\":\"parent\",\"summary\":\"Implemented OAuth\"}"));
            var ex = await Record.ExceptionAsync(SubagentStopHandler.RunAsync);
            Assert.Null(ex);
        }
        finally { Console.SetIn(originalIn); }
    }
}
