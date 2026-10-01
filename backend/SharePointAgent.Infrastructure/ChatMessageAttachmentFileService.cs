using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointAgent.Persistence;
using SharePointAgent.Application;
using SharePointAgent.Domain;

using SearchOptions = SharePointAgent.Application.SearchOptions;

namespace SharePointAgent.Infrastructure;

public sealed class ChatMessageAttachmentFileService(
    IDbContextFactory<SharePointIndexDbContext> contextFactory,
    BlobServiceClient blobService,
    SearchIndexClient indexClient,
    AttachmentContentCache contentCache,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    IOptions<UploadOptions> uploadOptions,
    IOptions<SearchOptions> searchOptions,
    ILogger<ChatMessageAttachmentFileService> logger,
    ContentSafetyService? contentSafety = null,
    AttachmentImageService? imageService = null)
{
    private readonly UploadOptions _uploads = uploadOptions.Value;
    private readonly SearchOptions _search = searchOptions.Value;
    public IReadOnlyList<string> TextFileExtensions => _uploads.GetTextFileExtensions();
    public IReadOnlyList<string> ImageFileExtensions => _uploads.GetImageFileExtensions();
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _initialized;

    private BlobContainerClient Container => blobService.GetBlobContainerClient(_uploads.ContainerName);
    private SearchClient Search => indexClient.GetSearchClient(_search.UploadIndexName);

    public async Task<AttachmentFileRecord> CreateAsync(
        string fileName,
        string? contentType,
        long sizeBytes,
        Stream content,
        CancellationToken cancellationToken, Guid? createdById = null, bool indexAfterUpload = true)
    {
        _uploads.ValidateFileName(fileName);
        if (sizeBytes <= 0)
        {
            throw new ArgumentException("The uploaded file is empty.", nameof(sizeBytes));
        }
        if (sizeBytes > _uploads.MaxFileBytes)
        {
            throw new UploadTooLargeException(_uploads.MaxFileBytes);
        }

        var id = Guid.Empty;
        var safeName = Path.GetFileName(fileName);
        var blobName = string.Empty;
        var now = DateTimeOffset.UtcNow;
        var uploadStarted = false;
        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var row = new ChatMessageAttachmentFileEntity
            {
                CreatedById = createdById,
                FileName = safeName,
                ContentType = contentType,
                SizeBytes = sizeBytes,
                Status = UploadIndexStatus.NotStarted,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            try
            {
                await AttachmentStorageQuota.StoreAsync(context, row, async ct =>
                {
                    // StoreAsync has inserted the row and obtained its database-generated ID.
                    id = row.Id;
                    blobName = $"{id:N}/{safeName}";
                    row.BlobName = blobName;
                    await context.SaveChangesAsync(ct);
                    await Container.CreateIfNotExistsAsync(cancellationToken: ct);
                    uploadStarted = true;
                    await Container.GetBlobClient(blobName).UploadAsync(content,
                        new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } }, ct);
                }, cancellationToken);
            }
            catch
            {
                if (uploadStarted)
                {
                    // The quota transaction rolled back. Clean up even when the request was cancelled.
                    try
                    {
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        await Container.GetBlobClient(blobName).DeleteIfExistsAsync(cancellationToken: cleanup.Token);
                    }
                    catch (Exception cleanupError)
                    {
                        logger.LogError(cleanupError, "Failed to clean up unsuccessful attachment upload {AttachmentId}", id);
                    }
                }
                throw;
            }
        }

        if (!indexAfterUpload)
        {
            return new AttachmentFileRecord(id, safeName, contentType, sizeBytes, UploadIndexStatus.NotStarted,
                0, null, null, now, now, null, null, null, null, null, true);
        }

        using var embeddingOperation = EmbeddingUsageScope.Begin(new(Operation: "AttachmentIndex", UserId: createdById));
        return await IndexAsync(id, cancellationToken);
    }

    public async Task<AttachmentFileRecord?> ReindexAsync(Guid id, CancellationToken cancellationToken)
    {
        using var embeddingOperation = EmbeddingUsageScope.Begin(new(Operation: "AttachmentReindex"));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await context.ChatMessageAttachmentFiles.AnyAsync(x => x.Id == id, cancellationToken))
        {
            return null;
        }
        return await IndexAsync(id, cancellationToken);
    }

    public async Task<AttachmentFilePage> ListAsync(string? search, int skip, int top, CancellationToken cancellationToken, Guid? createdById = null)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.ChatMessageAttachmentFiles.AsNoTracking().Where(x => createdById == null || x.CreatedById == createdById);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.FileName.Contains(term));
        }
        var total = await query.LongCountAsync(cancellationToken);
        var rows = await query.OrderByDescending(x => x.CreatedAtUtc).Skip(skip).Take(top)
            .Select(x => new AttachmentFileRecord(
                x.Id, x.FileName, x.ContentType, x.SizeBytes, x.Status, x.ChunkCount, x.EmbeddingTokenCount,
                x.ErrorMessage, x.CreatedAtUtc, x.UpdatedAtUtc, x.IndexedAtUtc,
                x.ChatMessageAttachmentId,
                x.ChatMessageAttachment != null
                    ? x.ChatMessageAttachment.MessageId
                    : x.MessageAttachments.OrderBy(a => a.CreatedAtUtc).Select(a => (Guid?)a.MessageId).FirstOrDefault(),
                x.ChatMessageAttachment != null
                    ? x.ChatMessageAttachment.Message!.ConversationId
                    : x.MessageAttachments.OrderBy(a => a.CreatedAtUtc).Select(a => (Guid?)a.Message!.ConversationId).FirstOrDefault(),
                x.ChatMessageAttachment != null
                    ? x.ChatMessageAttachment.Message!.Conversation!.Title
                    : x.MessageAttachments.OrderBy(a => a.CreatedAtUtc).Select(a => a.Message!.Conversation!.Title).FirstOrDefault(),
                !x.MessageAttachments.Any()))
            .ToListAsync(cancellationToken);
        return new AttachmentFilePage(total, rows);
    }

    public async Task<AttachmentFileDownload?> DownloadAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await context.ChatMessageAttachmentFiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (row is null)
        {
            return null;
        }
        var cached = await contentCache.DownloadAsync(row, cancellationToken);
        return new AttachmentFileDownload(new FileStream(cached.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete), row.FileName, row.ContentType ?? "application/octet-stream");
    }

    public async Task<string?> GetIndexedMarkdownAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await context.ChatMessageAttachmentFiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (row is null)
        {
            return null;
        }
        return (await contentCache.GetMarkdownAsync(row, cancellationToken)).Content;
    }

    public async Task<string?> ConvertToMarkdownAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await context.ChatMessageAttachmentFiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (row is null)
        {
            return null;
        }

        // Convert the original without replacing the Markdown or chunks used by the index.
        return await contentCache.ConvertToMarkdownAsync(row, cancellationToken);
    }

    public async Task<DownloadedFile?> DownloadConversationAttachmentAsync(Guid conversationId, Guid attachmentId,
        CancellationToken cancellationToken)
    {
        var row = await FindConversationAttachmentAsync(conversationId, attachmentId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        var original = await contentCache.DownloadAsync(row, cancellationToken);
        return new DownloadedFile(original.LocalPath, row.FileName, new FileInfo(original.LocalPath).Length, original.CacheHit);
    }

    public async Task<DownloadedFile?> DownloadConversationAttachmentMarkdownAsync(Guid conversationId, Guid attachmentId,
        CancellationToken cancellationToken)
    {
        var row = await FindConversationAttachmentAsync(conversationId, attachmentId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        if (_uploads.IsTextFile(row.FileName))
        {
            throw new ArgumentException("This attachment is already text. Use download_attachment, then read_text with its localPath. Do not call download_attachment_markdown for text files.");
        }

        var cached = await contentCache.GetMarkdownAsync(row, cancellationToken);
        return new DownloadedFile(cached.LocalPath, row.FileName + ".md", new FileInfo(cached.LocalPath).Length, cached.MarkdownCacheHit);
    }

    private async Task<ChatMessageAttachmentFileEntity?> FindConversationAttachmentAsync(Guid conversationId, Guid attachmentId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.ChatMessageAttachmentFiles.AsNoTracking().FirstOrDefaultAsync(file =>
            file.Id == attachmentId && file.MessageAttachments.Any(link => link.Message!.ConversationId == conversationId), cancellationToken);
    }

    public async Task<bool> DeleteOrphanAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable,
            cancellationToken);
        var row = await context.ChatMessageAttachmentFiles.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (row is null)
        {
            return false;
        }
        if (row.ChatMessageAttachmentId is not null
            || await context.ChatMessageAttachments.AnyAsync(x => x.AttachmentFileId == id, cancellationToken)
            || await context.SignatureRequests.AnyAsync(x => x.AttachmentFileId == id, cancellationToken))
        {
            throw new AttachmentFileIsLinkedException();
        }

        await EnsureInfrastructureAsync(cancellationToken);
        await DeleteIndexDocumentsAsync(id, cancellationToken);
        await Container.GetBlobClient(row.BlobName).DeleteIfExistsAsync(cancellationToken: cancellationToken);
        await contentCache.DeleteAsync(id, cancellationToken);
        context.ChatMessageAttachmentFiles.Remove(row);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task ValidateReadyAsync(
        IReadOnlyCollection<Guid> uploadIds,
        CancellationToken cancellationToken)
    {
        if (uploadIds.Count == 0)
        {
            return;
        }

        var distinctIds = uploadIds.Distinct().ToArray();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var ready = await context.ChatMessageAttachmentFiles.AsNoTracking()
            .Where(x => distinctIds.Contains(x.Id)
                        && x.Status == UploadIndexStatus.Indexed
                        && x.ChatMessageAttachmentId == null
                        && !x.MessageAttachments.Any())
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        if (ready.Count != distinctIds.Length)
        {
            throw new InvalidOperationException(
                "Every attachment file must be indexed successfully and not already linked to a message.");
        }
    }

    public async Task<IReadOnlyList<ConversationAttachmentReference>> ListConversationAttachmentsAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await context.ChatMessageAttachments.AsNoTracking()
            .Where(x => x.Message!.ConversationId == conversationId
                        && x.AttachmentFile!.Status == UploadIndexStatus.Indexed)
            .Select(x => new { AttachmentId = x.AttachmentFileId, FileName = x.AttachmentFile!.FileName })
            .Distinct()
            .OrderBy(x => x.FileName)
            .ThenBy(x => x.AttachmentId)
            .ToListAsync(cancellationToken);
        return rows.Select(x => new ConversationAttachmentReference(x.AttachmentId, x.FileName)).ToArray();
    }

    public async Task<IReadOnlyList<AttachmentSearchHit>> SearchConversationAsync(
        Guid conversationId,
        string query,
        int top,
        Guid? attachmentId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        // The conversation ID comes from the server's current chat turn, not from the tool arguments.
        // Only files linked to messages in that conversation may contribute search results.
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var linkedAttachments = context.ChatMessageAttachments.AsNoTracking()
            .Where(x => x.Message!.ConversationId == conversationId
                        && x.AttachmentFile!.Status == UploadIndexStatus.Indexed);
        if (attachmentId is { } selectedId)
        {
            linkedAttachments = linkedAttachments.Where(x => x.AttachmentFileId == selectedId);
        }
        var attachmentIds = await linkedAttachments
            .Select(x => x.AttachmentFileId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (attachmentIds.Count == 0)
        {
            return [];
        }

        await EnsureInfrastructureAsync(cancellationToken);
        var count = Math.Clamp(top, 1, 10);
        using var embeddingAttribution = EmbeddingUsageScope.Begin(new(
            Operation: "AttachmentVectorSearch", ConversationId: conversationId, AttachmentId: attachmentId));
        var vector = await ChatEmbeddingUsage.GenerateQueryVectorAsync(embeddings, query, cancellationToken);
        var hits = new List<AttachmentSearchHit>();
        foreach (var batch in attachmentIds.Chunk(100))
        {
            var values = string.Join(',', batch.Select(id => id.ToString("D")));
            var options = new Azure.Search.Documents.SearchOptions
            {
                Filter = $"search.in(uploadId, '{values}', ',')",
                Size = count,
                VectorSearch = new VectorSearchOptions
                {
                    Queries = { new VectorizedQuery(vector) { KNearestNeighborsCount = count, Fields = { "contentVector" } } }
                }
            };
            options.Select.Add("uploadId");
            options.Select.Add("name");
            options.Select.Add("chunkNumber");
            options.Select.Add("content");

            var response = await Search.SearchAsync<UploadChunkDocument>(query, options, cancellationToken);
            await foreach (var result in response.Value.GetResultsAsync())
            {
                if (Guid.TryParse(result.Document.UploadId, out var id) && batch.Contains(id))
                {
                    hits.Add(new AttachmentSearchHit(
                        id, result.Document.Name, result.Document.ChunkNumber,
                        result.Document.Content, result.Score));
                }
            }
        }

        return hits.OrderByDescending(hit => hit.Score).Take(count).ToArray();
    }

    private async Task<AttachmentFileRecord> IndexAsync(Guid id, CancellationToken cancellationToken)
    {
        ChatMessageAttachmentFileEntity row;
        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            row = await context.ChatMessageAttachmentFiles.SingleAsync(x => x.Id == id, cancellationToken);
            row.Status = UploadIndexStatus.Indexing;
            row.ErrorMessage = null;
            row.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
        }

        try
        {
            await EnsureInfrastructureAsync(cancellationToken);
            var markdown = _uploads.IsImageFile(row.FileName)
                ? await (imageService ?? throw new InvalidOperationException("Image indexing is not configured."))
                    .ConvertForIndexAsync(row, cancellationToken)
                : await contentCache.ConvertForIndexAsync(row, cancellationToken);
            if (string.IsNullOrWhiteSpace(markdown))
            {
                throw new InvalidOperationException(
                    $"No readable text was extracted from '{row.FileName}'. " +
                    "For images or scanned documents, configure image text extraction (OCR) in the conversion service, then reindex the attachment.");
            }
            if (contentSafety is not null)
            {
                await contentSafety.CheckAsync(markdown, EmbeddingUsageScope.Current.UserId ?? row.CreatedById,
                    EmbeddingUsageScope.Current.ConversationId, cancellationToken, "AttachmentText",
                    EmbeddingUsageScope.Current.QuestionId, id);
            }
            var texts = TextChunker.Split(markdown, _uploads.ChunkSizeCharacters, _uploads.ChunkOverlapCharacters);
            var documents = new List<UploadChunkDocument>(texts.Count);
            long? embeddingTokenCount = 0;
            for (var index = 0; index < texts.Count; index++)
            {
                using var embeddingAttribution = EmbeddingUsageScope.Begin(new(
                    UserId: EmbeddingUsageScope.Current.UserId ?? row.CreatedById,
                    AttachmentId: id, ChunkNumber: index));
                var generated = await embeddings.GenerateAsync([texts[index]], cancellationToken: cancellationToken);
                var vector = generated[0].Vector;
                var tokens = generated.Usage?.TotalTokenCount ?? generated.Usage?.InputTokenCount;
                embeddingTokenCount = embeddingTokenCount.HasValue && tokens.HasValue
                    ? embeddingTokenCount.Value + tokens.Value
                    : null;
                documents.Add(new UploadChunkDocument
                {
                    Id = SearchChunkKey.For("upload", id.ToString("D"), index),
                    UploadId = id.ToString("D"),
                    Name = row.FileName,
                    MimeType = row.ContentType,
                    ChunkNumber = index,
                    Content = texts[index],
                    ContentVector = vector.ToArray(),
                });
            }

            await DeleteIndexDocumentsAsync(id, cancellationToken);
            if (documents.Count > 0)
            {
                foreach (var batch in documents.Chunk(1000))
                {
                    await Search.MergeOrUploadDocumentsAsync(batch, new IndexDocumentsOptions { ThrowOnAnyError = true }, cancellationToken);
                }
            }
            await contentCache.StoreMarkdownAsync(id, markdown, cancellationToken);
            await SetOutcomeAsync(id, UploadIndexStatus.Indexed, documents.Count, embeddingTokenCount, null, cancellationToken);
            logger.LogInformation("Indexed attachment {UploadId} ({FileName}) as {ChunkCount} chunks using {EmbeddingTokenCount} embedding tokens.", id, row.FileName, documents.Count, embeddingTokenCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Indexing upload {UploadId} ({FileName}) failed.", id, row.FileName);
            await SetOutcomeAsync(id, UploadIndexStatus.Failed, 0, null, ex.Message, cancellationToken);
        }

        await using var resultContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        return (await ListByIdAsync(resultContext, id, cancellationToken))!;
    }

    private async Task SetOutcomeAsync(Guid id, UploadIndexStatus status, int chunks, long? embeddingTokenCount, string? error, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.ChatMessageAttachmentFiles.Where(x => x.Id == id).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Status, status)
            .SetProperty(x => x.ChunkCount, chunks)
            .SetProperty(x => x.EmbeddingTokenCount, embeddingTokenCount)
            .SetProperty(x => x.ErrorMessage, error == null ? null : error.Length <= 4000 ? error : error[..4000])
            .SetProperty(x => x.UpdatedAtUtc, now)
            .SetProperty(x => x.IndexedAtUtc, status == UploadIndexStatus.Indexed ? now : null), cancellationToken);
    }

    private async Task EnsureInfrastructureAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            await Container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            var fields = new List<SearchField>
            {
                new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
                new SimpleField("uploadId", SearchFieldDataType.String) { IsFilterable = true },
                new SearchableField("name") { IsFilterable = true },
                new SimpleField("mimeType", SearchFieldDataType.String) { IsFilterable = true },
                new SimpleField("chunkNumber", SearchFieldDataType.Int32) { IsSortable = true },
                new SearchableField("content"),
                new SearchField("contentVector", SearchFieldDataType.Collection(SearchFieldDataType.Single))
                {
                    IsSearchable = true,
                    VectorSearchDimensions = _search.VectorDimensions,
                    VectorSearchProfileName = "content-vector-profile"
                }
            };
            var definition = new SearchIndex(_search.UploadIndexName, fields)
            {
                VectorSearch = new VectorSearch
                {
                    Algorithms = { new HnswAlgorithmConfiguration("content-hnsw") },
                    Profiles = { new VectorSearchProfile("content-vector-profile", "content-hnsw") }
                }
            };
            await indexClient.CreateOrUpdateIndexAsync(
                definition,
                allowIndexDowntime: false,
                cancellationToken: cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private async Task DeleteIndexDocumentsAsync(Guid id, CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        for (var skip = 0; ; skip += 1000)
        {
            var response = await Search.SearchAsync<UploadChunkDocument>("*", new Azure.Search.Documents.SearchOptions
            {
                Filter = $"uploadId eq '{id:D}'",
                Size = 1000,
                Skip = skip,
                Select = { "id" }
            }, cancellationToken);
            var pageCount = 0;
            await foreach (var result in response.Value.GetResultsAsync())
            {
                ids.Add(result.Document.Id);
                pageCount++;
            }
            if (pageCount < 1000)
            {
                break;
            }
        }
        foreach (var batch in ids.Chunk(1000))
        {
            await Search.DeleteDocumentsAsync("id", batch, cancellationToken: cancellationToken);
        }
    }

    private static Task<AttachmentFileRecord?> ListByIdAsync(
        SharePointIndexDbContext context,
        Guid id,
        CancellationToken cancellationToken) => context.ChatMessageAttachmentFiles.AsNoTracking()
        .Where(x => x.Id == id)
        .Select(x => new AttachmentFileRecord(
            x.Id, x.FileName, x.ContentType, x.SizeBytes, x.Status, x.ChunkCount, x.EmbeddingTokenCount,
            x.ErrorMessage, x.CreatedAtUtc, x.UpdatedAtUtc, x.IndexedAtUtc,
            x.ChatMessageAttachmentId,
            x.ChatMessageAttachment != null
                ? x.ChatMessageAttachment.MessageId
                : x.MessageAttachments.OrderBy(a => a.CreatedAtUtc).Select(a => (Guid?)a.MessageId).FirstOrDefault(),
            x.ChatMessageAttachment != null
                ? x.ChatMessageAttachment.Message!.ConversationId
                : x.MessageAttachments.OrderBy(a => a.CreatedAtUtc).Select(a => (Guid?)a.Message!.ConversationId).FirstOrDefault(),
            x.ChatMessageAttachment != null
                ? x.ChatMessageAttachment.Message!.Conversation!.Title
                : x.MessageAttachments.OrderBy(a => a.CreatedAtUtc).Select(a => a.Message!.Conversation!.Title).FirstOrDefault(),
            !x.MessageAttachments.Any()))
        .SingleOrDefaultAsync(cancellationToken);
}
