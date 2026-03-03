using System.Text.RegularExpressions;

namespace Ses.Local.Core.Services;

/// <summary>
/// Strips &lt;private&gt;...&lt;/private&gt; tags from content BEFORE storage.
/// Content inside private tags is replaced with "[PRIVATE — redacted]".
/// This is a pre-storage operation — the original content is never persisted.
/// </summary>
public static partial class PrivateTagStripper
{
    private const string Redacted = "[PRIVATE — redacted]";

    /// <summary>
    /// Replaces all &lt;private&gt;...&lt;/private&gt; blocks with "[PRIVATE — redacted]".
    /// Returns the original string if no private tags are found.
    /// </summary>
    public static string Strip(string content)
    {
        if (string.IsNullOrEmpty(content)) return content;
        if (!content.Contains("<private>", StringComparison.OrdinalIgnoreCase)) return content;

        return PrivateTagRegex().Replace(content, Redacted);
    }

    /// <summary>Returns true if the content contains any &lt;private&gt; tags.</summary>
    public static bool ContainsPrivateTags(string content)
        => !string.IsNullOrEmpty(content) &&
           content.Contains("<private>", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"<private>[\s\S]*?</private>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex PrivateTagRegex();
}
