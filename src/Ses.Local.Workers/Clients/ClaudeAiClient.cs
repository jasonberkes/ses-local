using Microsoft.Extensions.Logging;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;

namespace Ses.Local.Workers.Clients;

/// <summary>
/// Fetches conversation content from claude.ai API using Chromium session cookie.
/// Implementation: WI-941.
/// </summary>
public sealed class ClaudeAiClient : IConversationProvider
{
    private readonly ILogger<ClaudeAiClient> _logger;
    public ClaudeAiClient(ILogger<ClaudeAiClient> logger) => _logger = logger;

    public Task<IReadOnlyList<ConversationSession>> GetSessionsAsync(DateTime? since = null, CancellationToken ct = default)
    {
        _logger.LogInformation("ClaudeAiClient.GetSessionsAsync stub — WI-941");
        return Task.FromResult<IReadOnlyList<ConversationSession>>([]);
    }

    public Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(string externalSessionId, CancellationToken ct = default)
    {
        _logger.LogInformation("ClaudeAiClient.GetMessagesAsync stub — WI-941");
        return Task.FromResult<IReadOnlyList<ConversationMessage>>([]);
    }
}
