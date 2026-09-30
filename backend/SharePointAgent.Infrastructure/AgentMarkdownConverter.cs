using Microsoft.Extensions.Options;
using SharePointAgent.Application;

namespace SharePointAgent.Infrastructure;

public sealed class AgentMarkdownConverter(
    AgentFileSystem files,
    MarkItDownClient converter,
    IOptions<UploadOptions> uploads)
{
    public async Task<string> ConvertAsync(string path, CancellationToken cancellationToken, string? destinationPath = null, bool overwrite = false)
    {
        var source = await files.ReadAsync(path, cancellationToken);
        if (uploads.Value.IsTextFile(source.Name))
        {
            throw new ArgumentException("This file is already text. Use read_text with its existing path.");
        }

        if (uploads.Value.IsImageFile(source.Name))
        {
            throw new ArgumentException("Images are not converted to Markdown. Use describe_image for image attachments.");
        }

        var destination = string.IsNullOrWhiteSpace(destinationPath)
            ? Path.Combine("Converted", Guid.NewGuid().ToString("N"), Path.GetFileNameWithoutExtension(source.Name) + ".md")
            : destinationPath;
        var fullDestination = files.Resolve(destination);
        if (!string.Equals(Path.GetExtension(fullDestination), ".md", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The destination must be a Markdown file with a .md extension.");
        }

        if (Directory.Exists(fullDestination) || (!overwrite && File.Exists(fullDestination)))
        {
            throw new ArgumentException("The destination already exists. Choose another file path or set overwrite to true for an existing file.");
        }

        var markdown = await converter.ConvertAsync(source.Name, source.Content, source.ContentType, cancellationToken);
        var result = await files.WriteTextAsync(destination, markdown, overwrite, cancellationToken);
        return files.Resolve(result.Path, mustExist: true);
    }
}
