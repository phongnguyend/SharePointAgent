using SharePointAgent.Domain;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;

namespace SharePointAgent.Infrastructure;

/// <summary>
/// Converts a file in the agent's working directory to Markdown beside it. The bytes travel through this
/// host, which holds the MarkItDown credentials, so an isolated workspace never needs them.
/// </summary>
public sealed class AgentMarkdownConverter(
    IAgentWorkspace files,
    MarkItDownClient converter,
    IOptions<UploadOptions> uploads)
{
    /// <summary>Returns the Markdown file's path, relative to the working directory.</summary>
    public async Task<string> ConvertAsync(string path, CancellationToken cancellationToken, string? destinationPath = null, bool overwrite = false)
    {
        var source = await files.ReadAsync(path, cancellationToken);
        if (uploads.Value.IsTextFile(source.Name))
        {
            throw new ArgumentException($"This file is already text. Use {ChatAgentToolNames.ReadText} with its existing path.");
        }

        if (uploads.Value.IsImageFile(source.Name))
        {
            throw new ArgumentException($"Images are not converted to Markdown. Use {ChatAgentToolNames.DescribeImage} for image attachments.");
        }

        var destination = files.Normalize(string.IsNullOrWhiteSpace(destinationPath)
            ? $"Converted/{Guid.NewGuid():N}/{Path.GetFileNameWithoutExtension(source.Name)}.md"
            : destinationPath);
        if (!string.Equals(Path.GetExtension(destination), ".md", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The destination must be a Markdown file with a .md extension.");
        }

        if (await files.FindAsync(destination, cancellationToken) is { } existing && (existing.IsDirectory || !overwrite))
        {
            throw new ArgumentException("The destination already exists. Choose another file path or set overwrite to true for an existing file.");
        }

        var markdown = await converter.ConvertAsync(source.Name, source.Content, source.ContentType, cancellationToken);
        var result = await files.WriteTextAsync(destination, markdown, overwrite, cancellationToken);
        return result.Path;
    }
}
