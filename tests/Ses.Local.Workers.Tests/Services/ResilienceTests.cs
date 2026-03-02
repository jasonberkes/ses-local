using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using Ses.Local.Workers;
using Ses.Local.Workers.Services;
using Xunit;

namespace Ses.Local.Workers.Tests.Services;

/// <summary>
/// Tests that HTTP clients use IHttpClientFactory and handle failure gracefully.
/// Each service is tested with a mock factory that simulates various error conditions.
/// </summary>
public sealed class ResilienceTests
{
    // ── DocumentServiceUploader ───────────────────────────────────────────────

    [Fact]
    public async Task DocumentServiceUploader_UsesIHttpClientFactory()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(DependencyInjection.DocumentServiceClientName))
               .Returns(() => new HttpClient(new ServiceUnavailableHandler())
               {
                   BaseAddress = new Uri("https://test.example.com")
               })
               .Verifiable();

        var uploader = new DocumentServiceUploader(factory.Object, NullLogger<DocumentServiceUploader>.Instance);
        var session  = new ConversationSession { Id = 1, Title = "T", ExternalId = "e1" };

        var result = await uploader.UploadAsync(session, Array.Empty<ConversationMessage>(), "pat");

        // Factory was called with correct named client key
        factory.Verify(f => f.CreateClient(DependencyInjection.DocumentServiceClientName), Times.Once);
        // Returns null on failure (does not throw)
        Assert.Null(result);
    }

    [Fact]
    public async Task DocumentServiceUploader_WhenServiceUnavailable_ReturnsNull()
    {
        var uploader = new DocumentServiceUploader(
            BuildFactory(DependencyInjection.DocumentServiceClientName, new ServiceUnavailableHandler()),
            NullLogger<DocumentServiceUploader>.Instance);
        var session = new ConversationSession { Id = 1, Title = "T", ExternalId = "e1" };

        var result = await uploader.UploadAsync(session, Array.Empty<ConversationMessage>(), "pat");
        Assert.Null(result);
    }

    // ── CloudMemoryRetainer ───────────────────────────────────────────────────

    [Fact]
    public async Task CloudMemoryRetainer_UsesIHttpClientFactory()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(DependencyInjection.CloudMemoryClientName))
               .Returns(() => new HttpClient(new ServiceUnavailableHandler())
               {
                   BaseAddress = new Uri("https://memory.test.com")
               })
               .Verifiable();

        var retainer = new CloudMemoryRetainer(factory.Object, NullLogger<CloudMemoryRetainer>.Instance);
        var session  = new ConversationSession { Id = 1, Title = "T", ExternalId = "e1" };
        var messages = new ConversationMessage[]
        {
            new() { Role = "assistant", Content = "Answer", CreatedAt = DateTime.UtcNow }
        };

        await retainer.RetainAsync(session, messages, "pat");

        factory.Verify(f => f.CreateClient(DependencyInjection.CloudMemoryClientName), Times.Once);
    }

    [Fact]
    public async Task CloudMemoryRetainer_WhenNetworkDown_ReturnsTrueGracefully()
    {
        var retainer = new CloudMemoryRetainer(
            BuildFactory(DependencyInjection.CloudMemoryClientName, new NetworkErrorHandler()),
            NullLogger<CloudMemoryRetainer>.Instance);
        var session  = new ConversationSession { Id = 1, ExternalId = "e1" };
        var messages = new ConversationMessage[]
        {
            new() { Role = "user",      Content = "Q", CreatedAt = DateTime.UtcNow },
            new() { Role = "assistant", Content = "A", CreatedAt = DateTime.UtcNow.AddSeconds(1) }
        };

        var result = await retainer.RetainAsync(session, messages, "pat");
        Assert.True(result); // Network failure is graceful degradation
    }

    [Fact]
    public async Task CloudMemoryRetainer_WhenUnauthorized_ReturnsTrueGracefully()
    {
        var retainer = new CloudMemoryRetainer(
            BuildFactory(DependencyInjection.CloudMemoryClientName,
                new StatusHandler(System.Net.HttpStatusCode.Unauthorized)),
            NullLogger<CloudMemoryRetainer>.Instance);
        var session  = new ConversationSession { Id = 1, ExternalId = "e1" };
        var messages = new ConversationMessage[]
        {
            new() { Role = "assistant", Content = "Answer", CreatedAt = DateTime.UtcNow }
        };

        var result = await retainer.RetainAsync(session, messages, "pat");
        Assert.True(result); // 401 = PAT lacks scope = not a failure
    }

    // ── SesMcpManager ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SesMcpManager_UsesIHttpClientFactory_ForInstallation()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(DependencyInjection.SesMcpInstallClientName))
               .Returns(() => new HttpClient(new ServiceUnavailableHandler()))
               .Verifiable();

        var keychain = new Mock<ICredentialStore>();
        keychain.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string?)null);
        var auth    = new Mock<IAuthService>();
        var updater = BuildUpdater();

        var manager = new SesMcpManager(factory.Object, keychain.Object, updater, auth.Object,
            NullLogger<SesMcpManager>.Instance);

        // Does not throw even when ses-mcp is missing and install fails
        var ex = await Record.ExceptionAsync(() => manager.CheckAndRepairAsync());
        Assert.Null(ex);
    }

    // ── ClaudeAiClient ────────────────────────────────────────────────────────

    [Fact]
    public async Task ClaudeAiClient_WhenUnauthorized_ReturnsNullOrgId()
    {
        var http = new HttpClient(new StatusHandler(System.Net.HttpStatusCode.Unauthorized))
        {
            BaseAddress = new Uri("https://claude.ai")
        };
        using var client = new ClaudeAiClient(http, "cookie", NullLogger<ClaudeAiClient>.Instance);

        var orgId = await client.GetOrgIdAsync();
        Assert.Null(orgId);
    }

    [Fact]
    public async Task ClaudeAiClient_WhenNetworkError_ReturnsNullOrgId()
    {
        var http = new HttpClient(new NetworkErrorHandler())
        {
            BaseAddress = new Uri("https://claude.ai")
        };
        using var client = new ClaudeAiClient(http, "cookie", NullLogger<ClaudeAiClient>.Instance);

        var orgId = await client.GetOrgIdAsync();
        Assert.Null(orgId);
    }

    // ── DI Registration ───────────────────────────────────────────────────────

    [Fact]
    public void DependencyInjection_RegistersIHttpClientFactory()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSesLocalWorkers();

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetService<IHttpClientFactory>();

        Assert.NotNull(factory);
    }

    [Fact]
    public void DependencyInjection_DocumentServiceClientIsRegistered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSesLocalWorkers();

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        // Should not throw — named client is registered
        var client = factory.CreateClient(DependencyInjection.DocumentServiceClientName);
        Assert.NotNull(client);
        Assert.Equal(
            new Uri("https://tm-documentservice-prod-eus2.redhill-040b1667.eastus2.azurecontainerapps.io"),
            client.BaseAddress);
    }

    [Fact]
    public void DependencyInjection_CloudMemoryClientIsRegistered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSesLocalWorkers();

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        var client = factory.CreateClient(DependencyInjection.CloudMemoryClientName);
        Assert.NotNull(client);
        Assert.Equal(new Uri("https://memory.tm.supereasysoftware.com"), client.BaseAddress);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IHttpClientFactory BuildFactory(string name, HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(name))
               .Returns(() => new HttpClient(handler)
               {
                   BaseAddress = new Uri("https://test.example.com")
               });
        return factory.Object;
    }

    private static SesMcpUpdater BuildUpdater()
    {
        var http = new HttpClient(new ServiceUnavailableHandler());
        return new SesMcpUpdater(NullLogger<SesMcpUpdater>.Instance, http);
    }

    private sealed class ServiceUnavailableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
    }

    private sealed class NetworkErrorHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("Simulated network error"));
    }

    private sealed class StatusHandler(System.Net.HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status));
    }
}
