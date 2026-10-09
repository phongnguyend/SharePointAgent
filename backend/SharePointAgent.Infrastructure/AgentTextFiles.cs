using SharePointAgent.Domain;
using System.Collections.Concurrent;
using System.Text;

namespace SharePointAgent.Infrastructure;

public sealed record TextFilePage(string Path, int StartLine, int EndLine, int TotalLines, int? NextLine, string Text);

/// <summary>
/// What <c>read_text</c> is allowed to open: paths granted by successful downloads in a single agent
/// turn, plus anything inside the agent's working directory, which it can list and write to anyway.
/// </summary>
public sealed class AgentTextFiles(AgentFileSystem? workingDirectory = null)
{
    private readonly ConcurrentDictionary<string, byte> paths = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public void Register(string path) => paths.TryAdd(System.IO.Path.GetFullPath(path), 0);

    public async Task<TextFilePage> ReadAsync(string path, int startLine = 1, int? endLine = null, CancellationToken ct = default)
    {
        AttachmentMarkdownReader.ValidateRange(startLine, endLine);
        var fullPath = ResolveReadable(path, 50 * 1024 * 1024);

        using var reader = new StreamReader(fullPath, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync(ct);
        if (text.Contains('\0'))
        {
            throw new ArgumentException($"This is not a text file. Use {ChatAgentToolNames.ConvertToMarkdown} for supported documents, then read the returned localPath.");
        }

        var page = AttachmentMarkdownReader.Read(Guid.Empty, System.IO.Path.GetFileName(fullPath), new(text, fullPath, true, true), startLine, endLine);
        return new(fullPath, page.StartLine, page.EndLine, page.TotalLines, page.NextLine, page.Markdown);
    }

    /// <summary>
    /// The full path of a file the agent may read, or an <see cref="ArgumentException"/> explaining why not.
    /// Every tool that reads a sandbox file goes through this, so they all share one rule.
    /// </summary>
    public string ResolveReadable(string path, long maxBytes)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        if (!paths.ContainsKey(fullPath))
        {
            // Resolve throws when the path leaves the working directory, which is the same refusal.
            _ = workingDirectory?.Resolve(path, mustExist: true)
                ?? throw new ArgumentException("Download the file with a download tool in this turn first, then use the exact returned localPath.");
            fullPath = workingDirectory.Resolve(path, mustExist: true);
        }

        for (FileSystemInfo? entry = new FileInfo(fullPath); entry is not null;
             entry = entry is FileInfo file ? file.Directory : ((DirectoryInfo)entry).Parent)
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException($"Symbolic links are not supported by {ChatAgentToolNames.ReadText}.");
            }
        }
        if (new FileInfo(fullPath).Length > maxBytes)
        {
            throw new ArgumentException($"The file exceeds the {maxBytes / (1024 * 1024)} MB read limit.");
        }
        return fullPath;
    }
}
