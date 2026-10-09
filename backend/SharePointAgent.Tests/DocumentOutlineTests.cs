using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class DocumentOutlineTests : IDisposable
{
    private const string Markdown = "# Agreement\nIntro\n## Scope\nScope text\n## Termination\nEither party\nmay end it.\n# Appendix\nRates";

    private const string Tree = """
        {"doc_name":"agreement","source_line_offset":0,"warnings":[],
         "structure":[
           {"title":"Agreement","node_id":"0000","line_num":1,"prefix_summary":"The agreement","nodes":[
             {"title":"Scope","node_id":"0001","line_num":3,"summary":"What is covered"},
             {"title":"Termination","node_id":"0002","line_num":5}]},
           {"title":"Appendix","node_id":"0003","line_num":8}]}
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "outline-test-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _requests = [];
    private readonly List<string> _fileNames = [];

    public DocumentOutlineTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    private AgentDocumentOutlines Outlines(string response = Tree)
    {
        var http = new HttpClient(new Handler(async request =>
        {
            var form = (MultipartFormDataContent)request.Content!;
            var parts = form.ToDictionary(part => part.Headers.ContentDisposition!.Name!.Trim('"'));
            _requests.Add($"text={await parts["include_text"].ReadAsStringAsync()};summaries={await parts["include_summaries"].ReadAsStringAsync()}");
            _fileNames.Add(parts["file"].Headers.ContentDisposition!.FileName!.Trim('"'));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }));
        var client = new PageIndexClient(http, Options.Create(new PageIndexOptions { Endpoint = "https://pageindex.example" }));
        return new AgentDocumentOutlines(client, new MemoryCache(new MemoryCacheOptions()), Options.Create(new UploadOptions()), NullLogger<AgentDocumentOutlines>.Instance);
    }

    private AgentFileSystem Workspace => new(Options.Create(new LocalWorkingDirectoryOptions { Directory = _directory }));

    private string Write(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task SectionsCoverTheirSubsectionsAndLineUpWithReadText()
    {
        var path = Write("agreement.md", Markdown);

        var outline = await Outlines().GetAsync(Workspace, path, includeSummaries: false, default);

        Assert.Equal(9, outline.TotalLines);
        Assert.Equal(
            [("Agreement", 1, 1, 7), ("Scope", 2, 3, 4), ("Termination", 2, 5, 7), ("Appendix", 1, 8, 9)],
            outline.Sections.Select(section => (section.Title, section.Level, section.StartLine, section.EndLine)).ToArray());
        Assert.Equal("The agreement", outline.Sections[0].Summary);
        Assert.Equal("What is covered", outline.Sections[1].Summary);
        Assert.False(outline.Truncated);

        var termination = await new AgentTextFiles(Workspace).ReadAsync(path, outline.Sections[2].StartLine, outline.Sections[2].EndLine);
        Assert.Equal("## Termination\nEither party\nmay end it.", termination.Text);
    }

    [Fact]
    public async Task OnlyHeadingsAreRequestedUnlessSummariesAreAskedFor()
    {
        var path = Write("agreement.md", Markdown);

        await Outlines().GetAsync(Workspace, path, includeSummaries: false, default);
        await Outlines().GetAsync(Workspace, path, includeSummaries: true, default);

        Assert.Equal(["text=false;summaries=false", "text=false;summaries=true"], _requests);
    }

    [Fact]
    public async Task OutlinesOfUnchangedFilesAreCached()
    {
        var path = Write("agreement.md", Markdown);
        var outlines = Outlines();

        await outlines.GetAsync(Workspace, path, includeSummaries: false, default);
        await outlines.GetAsync(Workspace, path, includeSummaries: false, default);
        File.AppendAllText(path, "\nMore");
        await outlines.GetAsync(Workspace, path, includeSummaries: false, default);

        Assert.Equal(2, _requests.Count);
    }

    [Fact]
    public async Task OnlyMarkdownIsSentToTheService()
    {
        var path = Write("report.pdf", "%PDF-1.7");

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => Outlines().GetAsync(Workspace, path, includeSummaries: false, default));

        Assert.Contains(ChatAgentToolNames.ConvertToMarkdown, exception.Message);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task PlainTextWithMarkdownHeadingsIsOutlinedAsMarkdown()
    {
        var path = Write("notes.txt", Markdown);

        var outline = await Outlines().GetAsync(Workspace, path, includeSummaries: false, default);

        Assert.Equal(4, outline.Sections.Count);
        Assert.Equal(["notes.md"], _fileNames);
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("readme.md")]
    public async Task FilesWithoutHeadingsGetNoOutlineAndNoServiceCall(string name)
    {
        var path = Write(name, "Line one\nLine two\n#hashtag is not a heading\nLine four");

        var outline = await Outlines().GetAsync(Workspace, path, includeSummaries: false, default);

        Assert.Empty(outline.Sections);
        Assert.Equal(4, outline.TotalLines);
        Assert.Equal(AgentDocumentOutlines.NoHeadingsWarning, Assert.Single(outline.Warnings));
        Assert.Empty(_requests);
    }

    [Theory]
    [InlineData("data.csv")]
    [InlineData("settings.json")]
    public async Task OtherTextFormatsAreReadDirectlyRatherThanConverted(string name)
    {
        var path = Write(name, "a,b\n1,2");

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => Outlines().GetAsync(Workspace, path, includeSummaries: false, default));

        Assert.Contains(ChatAgentToolNames.ReadText, exception.Message);
        Assert.DoesNotContain(ChatAgentToolNames.ConvertToMarkdown, exception.Message);
        Assert.Empty(_requests);
    }

    [Fact]
    public void TheHeadingAddedBeforeAPreambleIsMappedBackToTheFile()
    {
        var result = new PageIndexResult
        {
            DocumentName = "notes",
            SourceLineOffset = 2,
            Structure =
            [
                new PageIndexNode { Title = "Document", LineNumber = 1 },
                new PageIndexNode { Title = "Details", LineNumber = 5 }
            ]
        };

        var (sections, _) = AgentDocumentOutlines.Flatten(result, totalLines: 6);

        Assert.Equal([(1, 2), (3, 6)], sections.Select(section => (section.StartLine, section.EndLine)).ToArray());
    }

    [Fact]
    public void LargeOutlinesAreCutOffAndFlagged()
    {
        var result = new PageIndexResult
        {
            DocumentName = "big",
            Structure = Enumerable.Range(1, AgentDocumentOutlines.MaxSections + 5).Select(line => new PageIndexNode { Title = $"S{line}", LineNumber = line }).ToList()
        };

        var (sections, truncated) = AgentDocumentOutlines.Flatten(result, totalLines: AgentDocumentOutlines.MaxSections + 5);

        Assert.Equal(AgentDocumentOutlines.MaxSections, sections.Count);
        Assert.True(truncated);
    }

    [Fact]
    public async Task OutlinesFollowTheWorkingDirectorysAccessRule()
    {
        var outside = Path.Combine(Path.GetTempPath(), "outline-outside-" + Guid.NewGuid().ToString("N") + ".md");
        File.WriteAllText(outside, Markdown);
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => Outlines().GetAsync(Workspace, outside, includeSummaries: false, default));
            await Assert.ThrowsAsync<ArgumentException>(() => Outlines().GetAsync(Workspace, "../x.md", includeSummaries: false, default));
            Assert.Empty(_requests);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void TheOutlineToolIsNotAStandingCapability()
    {
        Assert.DoesNotContain(ChatAgentService.GetTools(), tool => tool.Name == ChatAgentToolNames.GetDocumentOutline);
        Assert.Contains($"When the {ChatAgentToolNames.GetDocumentOutline} tool is available", AgentDefaults.Instructions);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
