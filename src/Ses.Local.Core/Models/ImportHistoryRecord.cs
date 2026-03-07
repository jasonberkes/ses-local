namespace Ses.Local.Core.Models;

/// <summary>
/// Record of a single import operation stored in the import_history table.
/// </summary>
public sealed class ImportHistoryRecord
{
    public long     Id                { get; set; }
    public string   Source            { get; set; } = string.Empty; // "claude" | "chatgpt" | "gemini"
    public string   FilePath          { get; set; } = string.Empty;
    public DateTime ImportedAt        { get; set; }
    public int      SessionsImported  { get; set; }
    public int      MessagesImported  { get; set; }
    public int      DuplicatesSkipped { get; set; }
    public int      Errors            { get; set; }
}
