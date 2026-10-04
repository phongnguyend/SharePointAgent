using Azure.AI.DocumentIntelligence;
using NSubstitute;
using Xunit;

namespace DocumentParsers.Tests;

public sealed class MarkdownImageOptionsTests
{
    [Fact]
    public void SkippingImagesOmitsAllImageFieldsAndPreservesSource()
    {
        var image = new ImageElement
        {
            Data = [1, 2, 3],
            ContentType = "image/png",
            Caption = "Image caption",
            AltText = "Image alt text",
            Description = "Image description",
            ExtractedText = "Image OCR text",
            Anchor = "D3",
            PageNumber = 2
        };
        var result = new PdfParseResult();
        result.Elements.AddRange([new TextElement("Before") { PageNumber = 1 }, image, new TextElement("After") { PageNumber = 3 }]);
        var parser = new PdfDocumentParser(Substitute.For<DocumentIntelligenceClient>());
        string Convert(bool skipImages) => parser.ConvertToMarkdown(result, skipImages: skipImages);

        var included = Convert(false);
        var skipped = Convert(true);
        foreach (var field in new[] { "[Image]", image.Caption, image.AltText, image.Description, image.ExtractedText, "Anchor: D3" })
        {
            Assert.Contains(field, included);
            Assert.DoesNotContain(field, skipped);
        }
        Assert.Contains("Before", skipped);
        Assert.Contains("After", skipped);
        Assert.True(skipped.IndexOf("Before", StringComparison.Ordinal) < skipped.IndexOf("After", StringComparison.Ordinal));
        Assert.Equal(included, Convert(false));
        Assert.Equal(new byte[] { 1, 2, 3 }, image.Data);
        Assert.DoesNotContain("<!-- Page 2 -->", skipped);
    }
}
