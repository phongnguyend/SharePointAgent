namespace SharePointAgent.Infrastructure;

public sealed record AttachmentMarkdownPage(Guid AttachmentId, string FileName, int StartLine, int EndLine,
    int TotalLines, int? NextLine, string Markdown, bool DownloadCacheHit, bool MarkdownCacheHit);

public static class AttachmentMarkdownReader
{
    public static void ValidateRange(int startLine, int? endLine)
    {
        if (startLine < 1 || endLine < startLine)
        {
            throw new ArgumentException("Line numbers are one-based; endLine must be at least startLine.");
        }
    }

    public static AttachmentMarkdownPage Read(Guid id, string name, CachedAttachmentMarkdown cached, int startLine = 1, int? endLine = null)
    {
        ValidateRange(startLine, endLine);
        var lines = new List<string>();
        using var reader = new StringReader(cached.Content);
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        var end = (int)Math.Min(lines.Count, Math.Min((long)endLine.GetValueOrDefault(int.MaxValue), (long)startLine + (endLine.HasValue ? 499 : 199)));
        var content = string.Join('\n', lines.Skip(startLine - 1).Take(Math.Max(0, end - startLine + 1)));
        return new(id, name, startLine, end, lines.Count, end < lines.Count ? end + 1 : null,
            content, cached.DownloadCacheHit, cached.MarkdownCacheHit);
    }
}
