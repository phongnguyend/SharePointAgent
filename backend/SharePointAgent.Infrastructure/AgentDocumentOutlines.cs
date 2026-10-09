using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure;

/// <summary>
/// One section of a document outline. <see cref="StartLine"/> and <see cref="EndLine"/> are one-based,
/// inclusive line numbers in the file the outline was built from, the same numbering <c>read_text</c> uses,
/// and a section's range includes its subsections.
/// </summary>
public sealed record DocumentOutlineSection(string Id, string Title, int Level, int StartLine, int EndLine, string? Summary);

public sealed record DocumentOutline(
    string Path,
    string DocumentName,
    int TotalLines,
    IReadOnlyList<DocumentOutlineSection> Sections,
    bool Truncated,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Builds section outlines of Markdown files with the PageIndex service, so the agent can read a long
/// document by section instead of from the top. Plain-text files are outlined the same way when they use
/// Markdown headings, which notes and exported wiki pages often do. Outlines are built from headings alone
/// unless summaries are asked for, which is the only case that calls a model. They are cached by content, so
/// asking again for an unchanged file costs nothing, and a file without headings is answered without calling
/// the service at all.
/// <para>
/// PageIndex does not know about SharePoint permissions. Only files in the agent's working directory are
/// sent, read through the same workspace every other tool uses.
/// </para>
/// </summary>
public sealed partial class AgentDocumentOutlines(
    PageIndexClient pageIndex,
    IMemoryCache cache,
    IOptions<UploadOptions> uploads,
    ILogger<AgentDocumentOutlines> logger)
{
    /// <summary>The PageIndex service's own upload limit.</summary>
    public const long MaxBytes = 25 * 1024 * 1024;

    /// <summary>More sections than this are cut off; the agent can still read the rest by line range.</summary>
    public const int MaxSections = 300;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    /// <summary>Files outlined directly. Plain text is sent as Markdown, the only text format PageIndex accepts.</summary>
    private static readonly HashSet<string> OutlinedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".txt" };

    public const string NoHeadingsWarning = "This file has no headings, so it has no outline. Read it with read_text and follow nextLine.";

    public async Task<DocumentOutline> GetAsync(IAgentWorkspace workspace, string path, bool includeSummaries, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(workspace.Normalize(path));
        if (!OutlinedExtensions.Contains(Path.GetExtension(name)))
        {
            // Other text formats, such as CSV and JSON, have no sections; converting them is refused as well.
            throw new ArgumentException(uploads.Value.IsTextFile(name)
                ? $"This file is plain text with no section structure to outline. Read it with {ChatAgentToolNames.ReadText} and follow nextLine."
                : $"Outlines are built from Markdown. Use {ChatAgentToolNames.ConvertToMarkdown} first, then pass the localPath it returns.");
        }

        var file = await workspace.ReadAsync(path, cancellationToken, MaxBytes);
        var fullPath = file.Path;
        var content = file.Content;
        var text = Encoding.UTF8.GetString(content);
        var totalLines = CountLines(text);
        if (!HeadingPattern().IsMatch(text))
        {
            return new DocumentOutline(fullPath, Path.GetFileNameWithoutExtension(name), totalLines, [], false, [NoHeadingsWarning]);
        }

        var key = $"document-outline:{Convert.ToHexStringLower(SHA256.HashData(content))}:{includeSummaries}";
        if (cache.TryGetValue(key, out DocumentOutline? cached) && cached is not null)
        {
            return cached with { Path = fullPath };
        }

        var result = await pageIndex.IndexAsync(
            Path.ChangeExtension(name, ".md"), content, "text/markdown", cancellationToken, includeText: false, includeSummaries: includeSummaries);
        if (result.Usage is { ModelCalls: > 0 } usage)
        {
            logger.LogInformation("PageIndex summarized an outline with {ModelCalls} model calls and {TotalTokens} tokens.", usage.ModelCalls, usage.TotalTokens);
        }

        var (sections, truncated) = Flatten(result, totalLines);
        var outline = new DocumentOutline(fullPath, result.DocumentName, totalLines, sections, truncated, result.Warnings);
        cache.Set(key, outline, new MemoryCacheEntryOptions { SlidingExpiration = CacheDuration });
        return outline;
    }

    /// <summary>
    /// Turns the PageIndex tree into a flat list in document order. A section ends where the next section at
    /// the same or a higher level starts, so its range covers its subsections. Line numbers are moved back by
    /// the service's <see cref="PageIndexResult.SourceLineOffset"/>, which accounts for the heading it adds to
    /// files whose text starts before the first heading.
    /// </summary>
    internal static (IReadOnlyList<DocumentOutlineSection> Sections, bool Truncated) Flatten(PageIndexResult result, int totalLines)
    {
        if (totalLines == 0)
        {
            return ([], false);
        }

        var offset = result.SourceLineOffset ?? 0;
        var nodes = new List<(PageIndexNode Node, int Level)>();
        void Visit(IEnumerable<PageIndexNode> children, int level)
        {
            foreach (var child in children)
            {
                nodes.Add((child, level));
                Visit(child.Nodes, level + 1);
            }
        }
        Visit(result.Structure, 1);

        var starts = nodes.Select(entry => Math.Clamp((entry.Node.LineNumber ?? 1) - offset, 1, totalLines)).ToList();
        var sections = new List<DocumentOutlineSection>();
        for (var index = 0; index < nodes.Count && sections.Count < MaxSections; index++)
        {
            var (node, level) = nodes[index];
            var end = totalLines;
            for (var next = index + 1; next < nodes.Count; next++)
            {
                if (nodes[next].Level <= level)
                {
                    end = starts[next] - 1;
                    break;
                }
            }

            sections.Add(new DocumentOutlineSection(
                node.NodeId ?? index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                string.IsNullOrWhiteSpace(node.Title) ? "(untitled)" : node.Title.Trim(),
                level,
                starts[index],
                Math.Max(starts[index], end),
                node.Summary ?? node.PrefixSummary));
        }
        return (sections, nodes.Count > MaxSections);
    }

    /// <summary>A Markdown ATX heading: one to six '#' characters, a space, and a title.</summary>
    [GeneratedRegex(@"^[ ]{0,3}#{1,6}[ \t]+\S", RegexOptions.Multiline)]
    private static partial Regex HeadingPattern();

    private static int CountLines(string text)
    {
        var count = 0;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is not null)
        {
            count++;
        }
        return count;
    }
}
