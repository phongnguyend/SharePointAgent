using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Infrastructure;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class AttachmentUploadValidationTests
{
    [Theory]
    [InlineData("report.DOCX")]
    [InlineData("report.pdf")]
    [InlineData("scan.PDF")]
    [InlineData("slides.pptx")]
    [InlineData("sheet.xlsx")]
    [InlineData("notes.txt")]
    [InlineData("data.json")]
    [InlineData("data.csv")]
    [InlineData("data.CSV")]
    [InlineData("readme.md")]
    [InlineData("clipboard.png")]
    [InlineData("photo.JPG")]
    [InlineData("photo.jpeg")]
    [InlineData("animation.gif")]
    [InlineData("image.webp")]
    public void DefaultTypesAreAllowed(string name) => new UploadOptions().ValidateFileName(name);

    [Fact]
    public void CsvIsTextByDefault() => Assert.True(new UploadOptions().IsTextFile("data.CSV"));

    [Theory]
    [InlineData("program.exe")]
    [InlineData("report.docx.exe")]
    [InlineData("no-extension")]
    public async Task UnsupportedTypesAreRejectedBeforeStorage(string name)
    {
        var service = new ChatMessageAttachmentFileService(null!, null!, null!, null!, null!,
            Options.Create(new UploadOptions()), Options.Create(new SearchOptions()), null!);
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(name, null, 5, Stream.Null, default));
    }

    [Fact]
    public void ConfiguredListReplacesDefaultsAndEmptyListDeniesAll()
    {
        var options = new UploadOptions { AllowedFileExtensions = [" PDF "] };
        options.ValidateFileName("report.pdf");
        Assert.Throws<ArgumentException>(() => options.ValidateFileName("report.docx"));
        options.AllowedFileExtensions = [];
        Assert.Throws<ArgumentException>(() => options.ValidateFileName("report.pdf"));
    }
}
