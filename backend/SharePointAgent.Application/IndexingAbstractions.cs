using SharePointAgent.Domain;

namespace SharePointAgent.Application;

// Abstractions over services outside this application — Service Bus, Azure AI Search, the extraction
// pipeline. The application's own database is reached through the repositories in Repositories.cs.

public interface IChangeSignalPublisher
{
    Task PublishAsync(SharePointChangeSignal signal, CancellationToken cancellationToken);
}

/// <summary>Turns a downloaded SharePoint file into the plain text that gets chunked and embedded.</summary>
public interface IContentExtractor
{
    Task<string> ExtractAsync(DriveItemChange item, byte[] content, CancellationToken cancellationToken);
}

/// <summary>Extracts text from a PDF, per <see cref="ContentExtractionOptions.Pdf"/>.</summary>
public interface IPdfExtractor
{
    Task<string> ExtractAsync(DriveItemChange item, byte[] content, CancellationToken cancellationToken);
}

/// <summary>Extracts text from a DOCX, per <see cref="ContentExtractionOptions.Docx"/>.</summary>
public interface IDocxExtractor
{
    Task<string> ExtractAsync(DriveItemChange item, byte[] content, CancellationToken cancellationToken);
}

/// <summary>Extracts text from a PPTX, per <see cref="ContentExtractionOptions.Pptx"/>.</summary>
public interface IPptxExtractor
{
    Task<string> ExtractAsync(DriveItemChange item, byte[] content, CancellationToken cancellationToken);
}

/// <summary>Extracts text from an XLSX, per <see cref="ContentExtractionOptions.Xlsx"/>.</summary>
public interface IXlsxExtractor
{
    Task<string> ExtractAsync(DriveItemChange item, byte[] content, CancellationToken cancellationToken);
}

/// <summary>Extracts text from an image by combining an OCR pass with a vision model description.</summary>
public interface IImageExtractor
{
    Task<string> ExtractAsync(DriveItemChange item, byte[] content, CancellationToken cancellationToken);
}

public interface ISearchIndexStore
{
    Task EnsureIndexAsync(CancellationToken cancellationToken);
    Task ReplaceItemAsync(string driveId, string itemId, IReadOnlyList<SearchChunkDocument> chunks, CancellationToken cancellationToken);
    Task DeleteItemAsync(string driveId, string itemId, CancellationToken cancellationToken);

    /// <summary>
    /// Merges file and permission fields onto chunks that are already indexed, leaving their content and
    /// vectors untouched. Returns false when the index does not hold every chunk, so the caller can fall
    /// back to a full rebuild instead of leaving the file half-updated.
    /// </summary>
    Task<bool> TryMergeItemMetadataAsync(IReadOnlyList<SearchChunkMetadataDocument> chunks, CancellationToken cancellationToken);
}

public interface ISearchQueryStore
{
    Task<SearchQueryResults> SearchAsync(SearchQueryMode mode, SearchQueryRequest request, CancellationToken cancellationToken);
}

public interface ISharePointChangeProcessor
{
    Task ProcessAsync(CancellationToken cancellationToken);
    Task<FileIndexRecord?> ReindexAsync(string driveId, string itemId, CancellationToken cancellationToken);
}
