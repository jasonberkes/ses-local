using Ses.Local.Core.Models;

namespace Ses.Local.Core.Interfaces;

/// <summary>
/// Extension point for the three-layer observation compression pipeline.
/// Layer 1 (rule-based) is always available; Layers 2 and 3 are implemented separately.
/// </summary>
public interface IObservationCompressor
{
    /// <summary>
    /// Compresses the observations for a session into a <see cref="SessionSummary"/>.
    /// </summary>
    /// <param name="sessionId">The session to summarize.</param>
    /// <param name="observations">All observations for that session, in sequence order.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A populated <see cref="SessionSummary"/> with <see cref="SessionSummary.CompressionLayer"/> set.</returns>
    Task<SessionSummary> CompressAsync(
        long sessionId,
        IReadOnlyList<ConversationObservation> observations,
        CancellationToken ct = default);
}
