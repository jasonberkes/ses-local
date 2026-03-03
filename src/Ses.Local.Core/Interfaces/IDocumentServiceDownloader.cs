using Ses.Local.Core.Models;

namespace Ses.Local.Core.Interfaces;

/// <summary>
/// Downloads and parses conversation transcripts from the cloud DocumentService
/// for multi-device pull sync (WI-991).
/// </summary>
public interface IDocumentServiceDownloader
{
    /// <summary>
    /// Queries DocumentService for documents created by ses-local that were updated after
    /// the given timestamp. Returns parsed documents from other devices (own uploads filtered out).
    /// </summary>
    Task<IReadOnlyList<PulledDocument>> GetDocumentsAsync(
        string pat,
        DateTime updatedAfter,
        string ownDeviceId,
        CancellationToken ct = default);
}
