using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Infrastructure;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class AttachmentUploadValidationTests
{
    [Theory]
    [InlineData("report.DOCX")]
    [InlineData("slides.pptx")]
    [InlineData("sheet.xlsx")]
    [InlineData("notes.txt")]
    [InlineData("data.json")]
    [InlineData("data.csv")]
    [InlineData("data.CSV")]
    [InlineData("readme.md")]
    public void DefaultTypesAreAllowed(string name) => new UploadOptions().ValidateFileName(name);

    [Fact]
    public void CsvIsTextByDefault() => Assert.True(new UploadOptions().IsTextFile("data.CSV"));

    [Theory]
    [InlineData("program.exe")]
    [InlineData("report.docx.exe")]
    [InlineData("no-extension")]
    [InlineData("report.pdf")]
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
