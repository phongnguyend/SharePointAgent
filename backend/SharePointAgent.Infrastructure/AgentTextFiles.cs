using System.Collections.Concurrent;
using System.Text;

namespace SharePointAgent.Infrastructure;

public sealed record TextFilePage(string Path, int StartLine, int EndLine, int TotalLines, int? NextLine, string Text);

/// <summary>Paths granted by successful downloads in a single agent turn.</summary>
public sealed class AgentTextFiles
{
    private readonly ConcurrentDictionary<string, byte> paths = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public void Register(string path) => paths.TryAdd(System.IO.Path.GetFullPath(path), 0);

    public async Task<TextFilePage> ReadAsync(string path, int startLine = 1, int? endLine = null, CancellationToken ct = default)
    {
        AttachmentMarkdownReader.ValidateRange(startLine, endLine);
        var fullPath = System.IO.Path.GetFullPath(path);
        if (!paths.ContainsKey(fullPath))
        {
            throw new ArgumentException("Download the file with a download tool in this turn first, then use the exact returned localPath.");
        }

        for (FileSystemInfo? entry = new FileInfo(fullPath); entry is not null;
             entry = entry is FileInfo file ? file.Directory : ((DirectoryInfo)entry).Parent)
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException("Symbolic links are not supported by read_text.");
            }
        }
        if (new FileInfo(fullPath).Length > 50 * 1024 * 1024)
        {
            throw new ArgumentException("The text file exceeds the 50 MB read limit.");
        }

        using var reader = new StreamReader(fullPath, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync(ct);
        if (text.Contains('\0'))
        {
            throw new ArgumentException("This is not a text file. Download the attachment Markdown instead.");
        }

        var page = AttachmentMarkdownReader.Read(Guid.Empty, System.IO.Path.GetFileName(fullPath), new(text, fullPath, true, true), startLine, endLine);
        return new(fullPath, page.StartLine, page.EndLine, page.TotalLines, page.NextLine, page.Markdown);
    }
}
