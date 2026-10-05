using Azure.AI.DocumentIntelligence;
using NSubstitute;
using Xunit;

namespace DocumentParsers.Tests;

public sealed class MarkdownImageOptionsTests
{
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
        var result = new XlsxParseResult();
        result.Worksheets.Add(new(1, "Data", new SpreadsheetElement
        {
            SheetName = "Data",
            Rows = [new(1, [new("A1", "Before", null)]), new(4, [new("A4", "After", null)])]
        }, [image]));
        result.Worksheets.Add(new(2, "ImageOnly", new SpreadsheetElement { SheetName = "ImageOnly", Rows = [] }, [image]));
        var parser = new XlsxDocumentParser();
        string Convert(bool skipImages) => parser.ConvertToMarkdown(result, skipImages: skipImages);

        var included = Convert(false);
        var skipped = Convert(true);
        const string comment = "<!-- image: abc.png; caption: Image caption; alt: Image alt text -->";
        Assert.Contains(comment, included);
        Assert.Equal(2, skipped.Split(comment).Length - 1);
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
        Assert.Contains("# ImageOnly", skipped);
    }
}
