using SharePointAgent.Application;
using SharePointAgent.Domain;
using System.Text;

namespace SharePointAgent.Infrastructure;

public sealed record TextFilePage(string Path, int StartLine, int EndLine, int TotalLines, int? NextLine, string Text);

/// <summary>
/// What <c>read_text</c> reads: text files in the agent's working directory, wherever that directory is.
/// Every file the agent can name is in it, from downloads, conversions, and its own writes, and the
/// workspace's own path rules are what keep a read inside it.
/// </summary>
public sealed class AgentTextFiles(IAgentWorkspace workspace)
{
    public const long MaxReadBytes = 50 * 1024 * 1024;

    public async Task<TextFilePage> ReadAsync(string path, int startLine = 1, int? endLine = null, CancellationToken ct = default)
    {
        AttachmentMarkdownReader.ValidateRange(startLine, endLine);
        var file = await workspace.ReadAsync(path, ct, MaxReadBytes);

        string text;
        try
        {
            using var reader = new StreamReader(new MemoryStream(file.Content), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            text = await reader.ReadToEndAsync(ct);
        }
        catch (DecoderFallbackException)
        {
            text = "\0";
        }

        if (text.Contains('\0'))
        {
            throw new ArgumentException($"This is not a text file. Use {ChatAgentToolNames.ConvertToMarkdown} for supported documents, then read the returned localPath.");
        }

        var page = AttachmentMarkdownReader.Read(Guid.Empty, file.Name, new(text, file.Path, true, true), startLine, endLine);
        return new(file.Path, page.StartLine, page.EndLine, page.TotalLines, page.NextLine, page.Markdown);
    }
}
