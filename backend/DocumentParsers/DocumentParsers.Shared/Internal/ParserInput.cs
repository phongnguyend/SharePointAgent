using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;

namespace DocumentParsers;

internal static class ParserInput
{
    internal static async Task<MemoryStream> ReadAsync(Stream source, long maxInputBytes, bool zip, CancellationToken token,
        long maxExpandedBytes = 0, int maxZipEntries = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxInputBytes);
        if (zip)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExpandedBytes);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxZipEntries);
        }
        var buffer = new MemoryStream();
        try
        {
            var bytes = new byte[81920];
            int count;
            while ((count = await source.ReadAsync(bytes, token)) != 0)
            {
                if (buffer.Length + count > maxInputBytes)
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
                if (archive.Entries.Count > maxZipEntries)
                {
                    throw new InvalidDataException("Package exceeds the entry count limit.");
                }
                long total = 0;
                foreach (var entry in archive.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (entry.Length > maxExpandedBytes - total)
                    {
                        throw new InvalidDataException("Package exceeds the expanded size limit.");
                    }
                    using var content = entry.Open();
                    int read;
                    while ((read = await content.ReadAsync(bytes, token)) != 0)
                    {
                        total += read;
                        if (total > maxExpandedBytes)
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

    internal static OpenSettings Settings(long maxExpandedBytes) => new()
    {
        AutoSave = false,
        MaxCharactersInPart = maxExpandedBytes
    };
}

internal sealed class ImageReader(int maxImages, long maxImageBytes)
{
    private int _count;
    private long _bytes;

    internal byte[] Read(Stream source, CancellationToken token)
    {
        if (++_count > maxImages)
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
            if (_bytes > maxImageBytes)
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
        return new ImageElement
        {
            Data = Read(stream, token),
            ContentType = image.ContentType,
            FileName = image.Uri.OriginalString.Split('/')[^1]
        };
    }
}
