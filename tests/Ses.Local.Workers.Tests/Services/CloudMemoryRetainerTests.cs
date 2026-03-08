using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Ses.Local.Core.Models;
using Ses.Local.Workers.Services;
using Xunit;

namespace Ses.Local.Workers.Tests.Services;

public sealed class CloudMemoryRetainerTests
{
    private static readonly ConversationSession TestSession = new()
    {
        Id = 1, Title = "Test", ExternalId = "uuid-1",
        Source = Core.Enums.ConversationSource.ClaudeCode
    };

    private static readonly ConversationMessage[] TestMessages =
    [
        new() { Role = "user", Content = "Hello", CreatedAt = DateTime.UtcNow },
        new() { Role = "assistant", Content = "Hi!", CreatedAt = DateTime.UtcNow.AddSeconds(1) }
    ];

    [Fact]
    public async Task RetainAsync_WhenDnsFails_DisablesAndReturnsTrueGracefully()
    {
        var retainer = new CloudMemoryRetainer(BuildDnsErrorFactory(), NullLogger<CloudMemoryRetainer>.Instance);

        // First call should fail DNS and disable
        var result1 = await retainer.RetainAsync(TestSession, TestMessages, "pat");
        Assert.True(result1); // graceful degradation

        // Second call should skip entirely (DNS disabled, within retry window)
        var result2 = await retainer.RetainAsync(TestSession, TestMessages, "pat");
        Assert.True(result2);
    }

    [Fact]
    public async Task RetainAsync_WithEmptyMessages_ReturnsTrue()
    {
        var retainer = new CloudMemoryRetainer(BuildDnsErrorFactory(), NullLogger<CloudMemoryRetainer>.Instance);
        var result = await retainer.RetainAsync(TestSession, [], "pat");
        Assert.True(result);
    }

    [Fact]
    public async Task RetainAsync_WithNoAssistantMessages_ReturnsTrue()
    {
        var retainer = new CloudMemoryRetainer(BuildDnsErrorFactory(), NullLogger<CloudMemoryRetainer>.Instance);
        var messages = new ConversationMessage[]
        {
            new() { Role = "user", Content = "Hello", CreatedAt = DateTime.UtcNow }
        };
        var result = await retainer.RetainAsync(TestSession, messages, "pat");
        Assert.True(result);
    }

    private static IHttpClientFactory BuildDnsErrorFactory()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
               .Returns(() => new HttpClient(new DnsErrorHandler())
               {
                   BaseAddress = new Uri("https://memory.tm.supereasysoftware.com/")
               });
        return factory.Object;
    }

    private sealed class DnsErrorHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(
                new HttpRequestException("nodename nor servname provided, or not known"));
    }
}
