using Azure.AI.DocumentIntelligence;
using NSubstitute;
using Xunit;

namespace DocumentParsers.Tests;

public sealed class MarkdownImageOptionsTests
{
    [Theory]
    [InlineData(null, null, "<!-- image: abc.png -->")]
    [InlineData("  ", "\r\n", "<!-- image: abc.png -->")]
    [InlineData(" Sales overview ", null, "<!-- image: abc.png; caption: Sales overview -->")]
    [InlineData(null, " Chart ", "<!-- image: abc.png; alt: Chart -->")]
    [InlineData("A-->B\r\nC", "<chart> & data", "<!-- image: abc.png; caption: A&#45;&#45;&gt;B&#13;&#10;C; alt: &lt;chart&gt; &amp; data -->")]
    public void CommentsOmitEmptyFieldsTrimAndEscapeMetadata(string? caption, string? alt, string expected)
    {
        var image = new ImageElement
        {
            Data = [1],
            ContentType = "image/png",
            FileName = "abc.png",
            Caption = caption,
            AltText = alt
        };
        var result = new DocxParseResult();
        result.BodyElements.Add(image);
        var parser = new DocxDocumentParser();
        Assert.Equal(expected, parser.ConvertToMarkdown(result, skipImages: true));
        Assert.StartsWith(expected, parser.ConvertToMarkdown(result));
        Assert.Equal(caption, image.Caption);
        Assert.Equal(alt, image.AltText);
    }

    [Fact]
    public void PlaceholderEscapesFileNamesAndPreservesEveryOccurrence()
    {
        var image = new ImageElement
        {
            Data = [1],
            ContentType = "image/png",
            FileName = "folder/abc-->\r\n.png"
        };
        var result = new DocxParseResult();
        result.BodyElements.AddRange([image, image]);
        var markdown = new DocxDocumentParser().ConvertToMarkdown(result, skipImages: true);
        Assert.Equal(2, markdown.Split("<!-- image: abc&#45;&#45;&gt;&#13;&#10;.png -->").Length - 1);
        Assert.DoesNotContain("folder", markdown);
        Assert.Equal("folder/abc-->\r\n.png", image.FileName);
    }

    [Fact]
    public void SkippingImagesRetainsCaptionAndAltInCommentsAndPreservesSource()
    {
        var image = new ImageElement
        {
            Data = [1, 2, 3],
            ContentType = "image/png",
            FileName = "abc.png",
            Caption = "Image caption",
            AltText = "Image alt text",
            Description = "Image description",
            ExtractedText = "Image OCR text",
            Anchor = "D3",
            PageNumber = 2
        };
        var result = new DocxParseResult();
        result.BodyElements.AddRange([new TextElement("Before"), image, new TextElement("After")]);
        var parser = new DocxDocumentParser();
        string Convert(bool skipImages) => parser.ConvertToMarkdown(result, skipImages: skipImages);

        var included = Convert(false);
        var skipped = Convert(true);
        const string comment = "<!-- image: abc.png; caption: Image caption; alt: Image alt text -->";
        Assert.Contains(comment, included);
        Assert.Equal(1, skipped.Split(comment).Length - 1);
        Assert.True(skipped.IndexOf("Before", StringComparison.Ordinal) < skipped.IndexOf(comment, StringComparison.Ordinal));
        foreach (var field in new[] { "[Image]", "Caption: ", "Alt text: ", image.Description, image.ExtractedText, "Anchor: D3" })
        {
            Assert.Contains(field, included);
            Assert.DoesNotContain(field, skipped);
        }
        Assert.Contains("Before", skipped);
        Assert.Contains("After", skipped);
        Assert.True(skipped.IndexOf("Before", StringComparison.Ordinal) < skipped.IndexOf("After", StringComparison.Ordinal));
        Assert.Equal(included, Convert(false));
        Assert.Equal(new byte[] { 1, 2, 3 }, image.Data);
    }
}
