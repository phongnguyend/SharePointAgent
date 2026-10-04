using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;

namespace DocumentParsers;

internal static class ParserInput
{
    internal static async Task<MemoryStream> ReadAsync(Stream source, ParserOptions options, bool zip, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(source);
        options.Validate();
        var buffer = new MemoryStream();
        try
        {
            var bytes = new byte[81920];
            int count;
            while ((count = await source.ReadAsync(bytes, token)) != 0)
            {
                if (buffer.Length + count > options.MaxInputBytes)
                {
                    throw new InvalidDataException("Document exceeds the input size limit.");
                }
                await buffer.WriteAsync(bytes.AsMemory(0, count), token);
            }
            token.ThrowIfCancellationRequested();
            buffer.Position = 0;
            if (zip)
            {
                using var archive = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
                if (archive.Entries.Count > options.MaxZipEntries)
                {
                    throw new InvalidDataException("Package exceeds the entry count limit.");
                }
                long total = 0;
                foreach (var entry in archive.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (entry.Length > options.MaxExpandedBytes - total)
                    {
                        throw new InvalidDataException("Package exceeds the expanded size limit.");
                    }
                    using var content = entry.Open();
                    int read;
                    while ((read = await content.ReadAsync(bytes, token)) != 0)
                    {
                        total += read;
                        if (total > options.MaxExpandedBytes)
                        {
                            throw new InvalidDataException("Package exceeds the expanded size limit.");
                        }
                    }
                }
            }
            buffer.Position = 0;
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    internal static OpenSettings Settings(ParserOptions options) => new()
    {
        AutoSave = false,
        MaxCharactersInPart = options.MaxExpandedBytes
    };
}

internal sealed class ImageReader(ParserOptions options)
{
    private int _count;
    private long _bytes;

    internal byte[] Read(Stream source, CancellationToken token)
    {
        if (++_count > options.MaxImages)
        {
            throw new InvalidDataException("Extracted image count exceeds the limit.");
        }
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = source.Read(buffer)) != 0)
        {
            token.ThrowIfCancellationRequested();
            _bytes += count;
            if (_bytes > options.MaxImageBytes)
            {
                throw new InvalidDataException("Extracted images exceed the total byte limit.");
            }
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    internal ImageElement? FromPart(OpenXmlPartContainer part, string? id, List<DocumentParseWarning> warnings, CancellationToken token)
    {
        if (id is null || !part.TryGetPartById(id, out var related) || related is not ImagePart image)
        {
            warnings.Add(new("ImageUnavailable", "An image relationship is missing or external."));
            return null;
        }
        using var stream = image.GetStream(FileMode.Open, FileAccess.Read);
        return new ImageElement { Data = Read(stream, token), ContentType = image.ContentType };
    }
}
