using Ses.Local.Core.Enums;
using Ses.Local.Core.Models;
using Xunit;

namespace Ses.Local.Core.Tests.Models;

public sealed class ConversationSessionTests
{
    [Fact]
    public void ConversationSession_DefaultValues_AreCorrect()
    {
        var session = new ConversationSession();
        Assert.Equal(string.Empty, session.ExternalId);
        Assert.Equal(string.Empty, session.Title);
        Assert.Null(session.SyncedAt);
        Assert.Null(session.ContentHash);
    }

    [Fact]
    public void ConversationSource_AllExpectedValues_Exist()
    {
        var values = Enum.GetValues<ConversationSource>();
        Assert.Contains(ConversationSource.ClaudeChat, values);
        Assert.Contains(ConversationSource.ClaudeCode, values);
        Assert.Contains(ConversationSource.Cowork, values);
        Assert.Contains(ConversationSource.ChatGpt, values);
    }

    [Fact]
    public void SesFeature_AllExpectedValues_Exist()
    {
        var values = Enum.GetValues<SesFeature>();
        Assert.Contains(SesFeature.ClaudeAiSync, values);
        Assert.Contains(SesFeature.CCHooks, values);
        Assert.Contains(SesFeature.CloudMemorySync, values);
    }
}
