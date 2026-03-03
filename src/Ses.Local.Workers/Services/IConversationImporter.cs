namespace Ses.Local.Workers.Services;

/// <summary>
/// Common contract for all AI-provider export parsers.
/// Implementors handle format detection and map provider-specific DTOs to
/// <see cref="Ses.Local.Core.Models.ConversationSession"/> / <see cref="Ses.Local.Core.Models.ConversationMessage"/>.
/// </summary>
public interface IConversationImporter
{
    /// <summary>
    /// Parses and imports an AI export file (JSON or ZIP) into local.db.
    /// </summary>
    /// <param name="filePath">Absolute path to the export file.</param>
    /// <param name="progress">Optional progress reporter — called after each conversation is processed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="importOptions">Optional filtering options for the import.</param>
    /// <returns>Summary of the import operation.</returns>
    Task<ImportResult> ImportAsync(
        string filePath,
        IProgress<ImportProgress>? progress = null,
        CancellationToken ct = default,
        ImportFilterOptions? importOptions = null);
}
