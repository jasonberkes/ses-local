using Ses.Local.Core.Services;
using Xunit;

namespace Ses.Local.Core.Tests.Services;

public sealed class PrivateTagStripperTests
{
    [Fact]
    public void Strip_NoPrivateTags_ReturnsOriginal()
    {
        const string content = "Hello, this is normal content with no tags.";
        Assert.Equal(content, PrivateTagStripper.Strip(content));
    }

    [Fact]
    public void Strip_SinglePrivateTag_RedactsContent()
    {
        const string content = "Before <private>secret stuff</private> after";
        var result = PrivateTagStripper.Strip(content);

        Assert.Equal("Before [PRIVATE — redacted] after", result);
        Assert.DoesNotContain("secret stuff", result);
    }

    [Fact]
    public void Strip_MultiplePrivateTags_RedactsAll()
    {
        const string content = "A <private>secret1</private> B <private>secret2</private> C";
        var result = PrivateTagStripper.Strip(content);

        Assert.Equal("A [PRIVATE — redacted] B [PRIVATE — redacted] C", result);
    }

    [Fact]
    public void Strip_MultilinePrivateTag_RedactsAll()
    {
        const string content = "Before\n<private>\nline1\nline2\n</private>\nAfter";
        var result = PrivateTagStripper.Strip(content);

        Assert.Equal("Before\n[PRIVATE — redacted]\nAfter", result);
        Assert.DoesNotContain("line1", result);
    }

    [Fact]
    public void Strip_CaseInsensitive_Works()
    {
        const string content = "Before <PRIVATE>secret</PRIVATE> after";
        var result = PrivateTagStripper.Strip(content);

        Assert.DoesNotContain("secret", result);
        Assert.Contains("[PRIVATE — redacted]", result);
    }

    [Fact]
    public void Strip_EmptyContent_ReturnsEmpty()
    {
        Assert.Equal("", PrivateTagStripper.Strip(""));
    }

    [Fact]
    public void Strip_NullContent_ReturnsNull()
    {
        Assert.Null(PrivateTagStripper.Strip(null!));
    }

    [Fact]
    public void ContainsPrivateTags_WhenPresent_ReturnsTrue()
    {
        Assert.True(PrivateTagStripper.ContainsPrivateTags("Hello <private>data</private>"));
    }

    [Fact]
    public void ContainsPrivateTags_WhenAbsent_ReturnsFalse()
    {
        Assert.False(PrivateTagStripper.ContainsPrivateTags("Hello normal content"));
    }

    [Fact]
    public void ContainsPrivateTags_NullOrEmpty_ReturnsFalse()
    {
        Assert.False(PrivateTagStripper.ContainsPrivateTags(""));
        Assert.False(PrivateTagStripper.ContainsPrivateTags(null!));
    }

    [Fact]
    public void Strip_EntireContentIsPrivate_ReplacesEntirely()
    {
        const string content = "<private>everything is secret</private>";
        var result = PrivateTagStripper.Strip(content);

        Assert.Equal("[PRIVATE — redacted]", result);
    }
}
