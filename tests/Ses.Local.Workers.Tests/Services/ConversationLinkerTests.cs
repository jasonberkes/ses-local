using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Ses.Local.Core.Enums;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using Ses.Local.Workers.Services;
using Xunit;

namespace Ses.Local.Workers.Tests.Services;

/// <summary>
/// Unit tests for ConversationLinker heuristic cross-session detection (WI-986).
/// Uses mocked ILocalDbService to isolate each heuristic independently.
/// </summary>
public sealed class ConversationLinkerTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ConversationLinker CreateLinker(ILocalDbService db) =>
        new(db, NullLogger<ConversationLinker>.Instance);

    private static ConversationSession Session(
        long id,
        ConversationSource source,
        string title,
        DateTime? createdAt = null) => new()
    {
        Id         = id,
        Source     = source,
        ExternalId = Guid.NewGuid().ToString(),
        Title      = title,
        CreatedAt  = createdAt ?? DateTime.UtcNow,
        UpdatedAt  = createdAt ?? DateTime.UtcNow
    };

    private static Mock<ILocalDbService> BuildMock(
        ConversationSession target,
        IReadOnlyList<ConversationSession>? candidates   = null,
        IReadOnlyList<ConversationObservation>? observations = null,
        SessionSummary? summary                          = null,
        IReadOnlyList<SessionSummary>? bulkSummaries     = null,
        IReadOnlyList<(long, int)>? sharedFiles          = null)
    {
        var mock = new Mock<ILocalDbService>(MockBehavior.Strict);

        mock.Setup(d => d.GetSessionByIdAsync(target.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(target);

        mock.Setup(d => d.GetObservationsAsync(target.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(observations ?? []);

        mock.Setup(d => d.GetSessionSummaryAsync(target.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(summary);

        mock.Setup(d => d.GetSessionsInTimeWindowAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), target.Id,
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(candidates ?? []);

        if (bulkSummaries is not null)
        {
            mock.Setup(d => d.GetBulkSessionSummariesAsync(
                    It.IsAny<IEnumerable<long>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(bulkSummaries);
        }
        else if (candidates is { Count: > 0 })
        {
            mock.Setup(d => d.GetBulkSessionSummariesAsync(
                    It.IsAny<IEnumerable<long>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
        }

        if (observations is { Count: > 0 } obs && obs.Any(o => !string.IsNullOrEmpty(o.FilePath)))
        {
            mock.Setup(d => d.GetSessionsWithSharedFilesAsync(
                    It.IsAny<IEnumerable<string>>(), target.Id,
                    It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(sharedFiles ?? []);
        }
        else if (sharedFiles is not null)
        {
            mock.Setup(d => d.GetSessionsWithSharedFilesAsync(
                    It.IsAny<IEnumerable<string>>(), target.Id,
                    It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(sharedFiles);
        }

        mock.Setup(d => d.CreateConversationLinksAsync(
                It.IsAny<IEnumerable<ConversationRelationship>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return mock;
    }

    // ── ExtractProjectName ────────────────────────────────────────────────────

    [Fact]
    public void ExtractProjectName_CcSessionWithProjectInTitle_ReturnsProjectName()
    {
        var session = Session(1, ConversationSource.ClaudeCode, "ses-local/abc12345");
        Assert.Equal("ses-local", ConversationLinker.ExtractProjectName(session));
    }

    [Fact]
    public void ExtractProjectName_CcSessionWithSubagentPrefix_ReturnsProjectName()
    {
        var session = Session(1, ConversationSource.ClaudeCode, "[subagent] ses-local/abc12345");
        Assert.Equal("ses-local", ConversationLinker.ExtractProjectName(session));
    }

    [Fact]
    public void ExtractProjectName_NonCcSession_ReturnsNull()
    {
        var session = Session(1, ConversationSource.ClaudeChat, "Help with ses-local auth");
        Assert.Null(ConversationLinker.ExtractProjectName(session));
    }

    [Fact]
    public void ExtractProjectName_CcSessionNoSlash_ReturnsNull()
    {
        var session = Session(1, ConversationSource.ClaudeCode, "abc12345");
        Assert.Null(ConversationLinker.ExtractProjectName(session));
    }

    // ── NormalizeTitle ────────────────────────────────────────────────────────

    [Fact]
    public void NormalizeTitle_CcTitle_StripsSessionIdHash()
    {
        Assert.Equal("ses-local", ConversationLinker.NormalizeTitle("ses-local/abc12345"));
    }

    [Fact]
    public void NormalizeTitle_SubagentPrefix_IsStripped()
    {
        Assert.Equal("ses-local", ConversationLinker.NormalizeTitle("[subagent] ses-local/abc12345"));
    }

    [Fact]
    public void NormalizeTitle_PlainTitle_IsLowercased()
    {
        Assert.Equal("fix auth service", ConversationLinker.NormalizeTitle("Fix Auth Service"));
    }

    // ── TitleSimilarity ───────────────────────────────────────────────────────

    [Fact]
    public void TitleSimilarity_IdenticalTitles_Returns1()
    {
        Assert.Equal(1.0, ConversationLinker.TitleSimilarity("ses-local", "ses-local"));
    }

    [Fact]
    public void TitleSimilarity_CompletelyDifferent_ReturnsLow()
    {
        var sim = ConversationLinker.TitleSimilarity("authentication service", "database migration");
        Assert.True(sim < 0.3);
    }

    [Fact]
    public void TitleSimilarity_HighOverlap_ReturnsHighScore()
    {
        var sim = ConversationLinker.TitleSimilarity(
            "fix auth service token refresh",
            "auth service token refresh bug");
        Assert.True(sim >= 0.5, $"Expected >= 0.5, got {sim}");
    }

    [Fact]
    public void TitleSimilarity_EmptyStrings_Returns0()
    {
        Assert.Equal(0.0, ConversationLinker.TitleSimilarity("", "something"));
        Assert.Equal(0.0, ConversationLinker.TitleSimilarity("something", ""));
    }

    // ── SAME PROJECT heuristic ────────────────────────────────────────────────

    [Fact]
    public async Task ProcessSessionAsync_SameProjectDetected_CreatesLink()
    {
        var ccSession      = Session(1, ConversationSource.ClaudeCode,  "ses-local/abc12345");
        var desktopSession = Session(2, ConversationSource.ClaudeChat,  "Help with ses-local auth");

        IReadOnlyList<ConversationRelationship>? captured = null;
        var mock = BuildMock(ccSession, candidates: [desktopSession]);
        mock.Setup(d => d.CreateConversationLinksAsync(
                It.IsAny<IEnumerable<ConversationRelationship>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ConversationRelationship>, CancellationToken>(
                (links, _) => captured = links.ToList())
            .Returns(Task.CompletedTask);

        await CreateLinker(mock.Object).ProcessSessionAsync(ccSession.Id);

        Assert.NotNull(captured);
        Assert.Contains(captured, l =>
            l.RelationshipType == "same_project" &&
            l.Confidence >= 0.8 &&
            ((l.SessionIdA == 1 && l.SessionIdB == 2) || (l.SessionIdA == 2 && l.SessionIdB == 1)));
    }

    [Fact]
    public async Task ProcessSessionAsync_NonCcSessionReferencingCcProject_CreatesLink()
    {
        var desktopSession = Session(1, ConversationSource.ClaudeChat,  "I need help with ses-local");
        var ccSession      = Session(2, ConversationSource.ClaudeCode,  "ses-local/abc12345");

        IReadOnlyList<ConversationRelationship>? captured = null;
        var mock = BuildMock(desktopSession, candidates: [ccSession]);
        mock.Setup(d => d.CreateConversationLinksAsync(
                It.IsAny<IEnumerable<ConversationRelationship>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ConversationRelationship>, CancellationToken>(
                (links, _) => captured = links.ToList())
            .Returns(Task.CompletedTask);

        await CreateLinker(mock.Object).ProcessSessionAsync(desktopSession.Id);

        Assert.NotNull(captured);
        Assert.Contains(captured, l =>
            l.RelationshipType == "same_project" &&
            l.Confidence >= 0.8);
    }

    [Fact]
    public async Task ProcessSessionAsync_DifferentProjects_NoSameProjectLink()
    {
        var ccSession      = Session(1, ConversationSource.ClaudeCode, "ses-local/abc12345");
        var desktopSession = Session(2, ConversationSource.ClaudeChat, "Help with completely different thing");

        IReadOnlyList<ConversationRelationship>? captured = null;
        var mock = BuildMock(ccSession, candidates: [desktopSession]);
        mock.Setup(d => d.CreateConversationLinksAsync(
                It.IsAny<IEnumerable<ConversationRelationship>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ConversationRelationship>, CancellationToken>(
                (links, _) => captured = links.ToList())
            .Returns(Task.CompletedTask);

        await CreateLinker(mock.Object).ProcessSessionAsync(ccSession.Id);

        Assert.True(captured is null || !captured.Any(l => l.RelationshipType == "same_project"));
    }

    // ── FILE OVERLAP heuristic ────────────────────────────────────────────────

    [Fact]
    public async Task ProcessSessionAsync_SharedFiles_CreatesSameTopicLink()
    {
        var target = Session(1, ConversationSource.ClaudeCode, "ses-local/abc12345");

        var observations = new List<ConversationObservation>
        {
            new() { Id = 10, SessionId = 1, ObservationType = ObservationType.ToolUse,
                    ToolName = "Read", FilePath = "/src/Auth.cs",  SequenceNumber = 0, CreatedAt = DateTime.UtcNow },
            new() { Id = 11, SessionId = 1, ObservationType = ObservationType.ToolUse,
                    ToolName = "Write", FilePath = "/src/Auth.cs", SequenceNumber = 1, CreatedAt = DateTime.UtcNow }
        };

        // Candidate shares 1 of 1 distinct file path → overlap = 1.0
        IReadOnlyList<(long, int)> sharedFiles = [(2L, 1)];

        IReadOnlyList<ConversationRelationship>? captured = null;
        var mock = BuildMock(target, observations: observations, sharedFiles: sharedFiles);
        mock.Setup(d => d.CreateConversationLinksAsync(
                It.IsAny<IEnumerable<ConversationRelationship>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ConversationRelationship>, CancellationToken>(
                (links, _) => captured = links.ToList())
            .Returns(Task.CompletedTask);

        await CreateLinker(mock.Object).ProcessSessionAsync(target.Id);

        Assert.NotNull(captured);
        var fileLink = Assert.Single(captured, l => l.RelationshipType == "same_topic");
        Assert.True(fileLink.Confidence >= 0.9, $"Expected high confidence, got {fileLink.Confidence}");
    }

    [Fact]
    public async Task ProcessSessionAsync_NoSharedFiles_NoSameTopicFromFiles()
    {
        var target = Session(1, ConversationSource.ClaudeCode, "ses-local/abc12345");

        var observations = new List<ConversationObservation>
        {
            new() { Id = 10, SessionId = 1, ObservationType = ObservationType.ToolUse,
                    FilePath = "/src/Auth.cs", SequenceNumber = 0, CreatedAt = DateTime.UtcNow }
        };

        IReadOnlyList<(long, int)> sharedFiles = []; // no shared sessions

        IReadOnlyList<ConversationRelationship>? captured = null;
        var mock = BuildMock(target, observations: observations, sharedFiles: sharedFiles);
        mock.Setup(d => d.CreateConversationLinksAsync(
                It.IsAny<IEnumerable<ConversationRelationship>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ConversationRelationship>, CancellationToken>(
                (links, _) => captured = links.ToList())
            .Returns(Task.CompletedTask);

        await CreateLinker(mock.Object).ProcessSessionAsync(target.Id);

        Assert.True(captured is null || !captured.Any(l => l.RelationshipType == "same_topic"));
    }

    // ── TEMPORAL heuristic ────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessSessionAsync_DifferentSurfaceWithin30Min_CreatesTemporalLink()
    {
        var baseTime       = new DateTime(2024, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var target         = Session(1, ConversationSource.ClaudeCode, "ses-local/abc12345", baseTime);
        var desktopSession = Session(2, ConversationSource.ClaudeChat,  "unrelated title",   baseTime.AddMinutes(15));

        IReadOnlyList<ConversationRelationship>? captured = null;
        var mock = BuildMock(target, candidates: [desktopSession]);
        mock.Setup(d => d.CreateConversationLinksAsync(
                It.IsAny<IEnumerable<ConversationRelationship>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ConversationRelationship>, CancellationToken>(
                (links, _) => captured = links.ToList())
            .Returns(Task.CompletedTask);

        await CreateLinker(mock.Object).ProcessSessionAsync(target.Id);

        Assert.NotNull(captured);
        Assert.Contains(captured, l =>
            l.RelationshipType == "temporal" &&
            Math.Abs(l.Confidence - 0.6) < 0.001);
    }

    [Fact]
    public async Task ProcessSessionAsync_DifferentSurfaceOver30Min_NoTemporalLink()
    {
        var baseTime       = new DateTime(2024, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var target         = Session(1, ConversationSource.ClaudeCode, "ses-local/abc12345", baseTime);
        var desktopSession = Session(2, ConversationSource.ClaudeChat,  "unrelated title",   baseTime.AddMinutes(45));

        IReadOnlyList<ConversationRelationship>? captured = null;
        var mock = BuildMock(target, candidates: [desktopSession]);
        mock.Setup(d => d.CreateConversationLinksAsync(
                It.IsAny<IEnumerable<ConversationRelationship>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ConversationRelationship>, CancellationToken>(
                (links, _) => captured = links.ToList())
            .Returns(Task.CompletedTask);

        await CreateLinker(mock.Object).ProcessSessionAsync(target.Id);

        Assert.True(captured is null || !captured.Any(l => l.RelationshipType == "temporal"));
    }

    [Fact]
    public async Task ProcessSessionAsync_SameSurfaceWithin30Min_NoTemporalLink()
    {
        var baseTime = new DateTime(2024, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var target   = Session(1, ConversationSource.ClaudeCode, "ses-local/abc12345", baseTime);
        var other    = Session(2, ConversationSource.ClaudeCode, "ses-local/xyz99999",  baseTime.AddMinutes(5));

        IReadOnlyList<ConversationRelationship>? captured = null;
        var mock = BuildMock(target, candidates: [other]);
        mock.Setup(d => d.CreateConversationLinksAsync(
                It.IsAny<IEnumerable<ConversationRelationship>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ConversationRelationship>, CancellationToken>(
                (links, _) => captured = links.ToList())
            .Returns(Task.CompletedTask);

        await CreateLinker(mock.Object).ProcessSessionAsync(target.Id);

        Assert.True(captured is null || !captured.Any(l => l.RelationshipType == "temporal"));
    }

    // ── CONTINUATION heuristic ────────────────────────────────────────────────

    [Fact]
    public async Task ProcessSessionAsync_SimilarTitles_CreatesContinuationLink()
    {
        var target    = Session(1, ConversationSource.ClaudeCode, "ses-local/abc12345");
        var candidate = Session(2, ConversationSource.ClaudeCode, "ses-local/def67890"); // same project = similar normalized title

        IReadOnlyList<ConversationRelationship>? captured = null;
        var mock = BuildMock(target, candidates: [candidate]);
        mock.Setup(d => d.CreateConversationLinksAsync(
                It.IsAny<IEnumerable<ConversationRelationship>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ConversationRelationship>, CancellationToken>(
                (links, _) => captured = links.ToList())
            .Returns(Task.CompletedTask);

        await CreateLinker(mock.Object).ProcessSessionAsync(target.Id);

        // Both normalize to "ses-local" → TitleSimilarity = 1.0 → continuation link
        Assert.NotNull(captured);
        Assert.Contains(captured, l => l.RelationshipType == "continuation");
    }

    [Fact]
    public async Task ProcessSessionAsync_VeryDifferentTitles_NoContinuationLink()
    {
        var target    = Session(1, ConversationSource.ClaudeCode, "authentication");
        var candidate = Session(2, ConversationSource.ClaudeChat,  "database migration schema");

        IReadOnlyList<ConversationRelationship>? captured = null;
        var mock = BuildMock(target, candidates: [candidate]);
        mock.Setup(d => d.CreateConversationLinksAsync(
                It.IsAny<IEnumerable<ConversationRelationship>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ConversationRelationship>, CancellationToken>(
                (links, _) => captured = links.ToList())
            .Returns(Task.CompletedTask);

        await CreateLinker(mock.Object).ProcessSessionAsync(target.Id);

        Assert.True(captured is null || !captured.Any(l => l.RelationshipType == "continuation"));
    }

    // ── CONCEPT OVERLAP heuristic ─────────────────────────────────────────────

    [Fact]
    public async Task ProcessSessionAsync_OverlappingConcepts_CreatesSameTopicLink()
    {
        var target = Session(1, ConversationSource.ClaudeCode, "ses-local/abc12345");
        var targetSummary = new SessionSummary
        {
            Id = 10, SessionId = 1,
            Concepts = "AuthService, TokenRefresh, JwtValidation",
            Category = "bugfix", Narrative = "Fixed auth", CreatedAt = DateTime.UtcNow
        };

        var candidate        = Session(2, ConversationSource.ClaudeChat, "Auth debug session");
        var candidateSummary = new SessionSummary
        {
            Id = 20, SessionId = 2,
            Concepts = "AuthService, TokenRefresh, UserProfile",
            Category = "feature", Narrative = "New feature", CreatedAt = DateTime.UtcNow
        };

        IReadOnlyList<ConversationRelationship>? captured = null;
        var mock = BuildMock(target,
            candidates: [candidate],
            summary: targetSummary,
            bulkSummaries: [candidateSummary]);
        mock.Setup(d => d.CreateConversationLinksAsync(
                It.IsAny<IEnumerable<ConversationRelationship>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ConversationRelationship>, CancellationToken>(
                (links, _) => captured = links.ToList())
            .Returns(Task.CompletedTask);

        await CreateLinker(mock.Object).ProcessSessionAsync(target.Id);

        Assert.NotNull(captured);
        var topicLink = Assert.Single(captured, l => l.RelationshipType == "same_topic");
        // Jaccard: intersection={AuthService,TokenRefresh}=2, union=4 → 0.5
        Assert.True(topicLink.Confidence >= 0.4, $"Expected concept Jaccard >= 0.4, got {topicLink.Confidence}");
    }

    [Fact]
    public async Task ProcessSessionAsync_NoConceptOverlap_NoSameTopicFromConcepts()
    {
        var target = Session(1, ConversationSource.ClaudeCode, "ses-local/abc12345");
        var targetSummary = new SessionSummary
        {
            Id = 10, SessionId = 1,
            Concepts = "AuthService, TokenRefresh",
            Category = "bugfix", Narrative = "Fixed auth", CreatedAt = DateTime.UtcNow
        };

        var candidate        = Session(2, ConversationSource.ClaudeChat, "Completely unrelated");
        var candidateSummary = new SessionSummary
        {
            Id = 20, SessionId = 2,
            Concepts = "DatabaseMigration, SqlSchema",
            Category = "feature", Narrative = "DB work", CreatedAt = DateTime.UtcNow
        };

        IReadOnlyList<ConversationRelationship>? captured = null;
        var mock = BuildMock(target,
            candidates: [candidate],
            summary: targetSummary,
            bulkSummaries: [candidateSummary]);
        mock.Setup(d => d.CreateConversationLinksAsync(
                It.IsAny<IEnumerable<ConversationRelationship>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ConversationRelationship>, CancellationToken>(
                (links, _) => captured = links.ToList())
            .Returns(Task.CompletedTask);

        await CreateLinker(mock.Object).ProcessSessionAsync(target.Id);

        Assert.True(captured is null || !captured.Any(l => l.RelationshipType == "same_topic"));
    }

    // ── Canonical ordering ────────────────────────────────────────────────────

    [Fact]
    public void MakeLink_AlwaysProducesSessionIdALessThanB()
    {
        var link1 = ConversationLinker.MakeLink(5, 3, "temporal", 0.6, "test");
        var link2 = ConversationLinker.MakeLink(3, 5, "temporal", 0.6, "test");

        Assert.True(link1.SessionIdA < link1.SessionIdB);
        Assert.True(link2.SessionIdA < link2.SessionIdB);
        Assert.Equal(link1.SessionIdA, link2.SessionIdA);
        Assert.Equal(link1.SessionIdB, link2.SessionIdB);
    }

    [Fact]
    public void MakeLink_ConfidenceClampedTo01()
    {
        var linkLow  = ConversationLinker.MakeLink(1, 2, "temporal", -0.5, "test");
        var linkHigh = ConversationLinker.MakeLink(1, 2, "temporal",  1.5, "test");

        Assert.Equal(0.0, linkLow.Confidence);
        Assert.Equal(1.0, linkHigh.Confidence);
    }

    // ── No candidates — no DB write ───────────────────────────────────────────

    [Fact]
    public async Task ProcessSessionAsync_NoCandidates_DoesNotCallCreate()
    {
        var target = Session(1, ConversationSource.ClaudeCode, "ses-local/abc12345");
        var mock   = BuildMock(target);

        await CreateLinker(mock.Object).ProcessSessionAsync(target.Id);

        mock.Verify(d => d.CreateConversationLinksAsync(
            It.IsAny<IEnumerable<ConversationRelationship>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Session not found ─────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessSessionAsync_SessionNotFound_DoesNotThrow()
    {
        var mock = new Mock<ILocalDbService>();
        mock.Setup(d => d.GetSessionByIdAsync(999L, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConversationSession?)null);

        // Should not throw
        await CreateLinker(mock.Object).ProcessSessionAsync(999L);

        mock.Verify(d => d.CreateConversationLinksAsync(
            It.IsAny<IEnumerable<ConversationRelationship>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
