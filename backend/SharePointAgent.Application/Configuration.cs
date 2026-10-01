using System.ComponentModel.DataAnnotations;
using SharePointAgent.Domain;

namespace SharePointAgent.Application;

public sealed class SharePointOptions
{
    public const string SectionName = "SharePoint";
    [Required] public string TenantId { get; set; } = "";
    [Required] public string ClientId { get; set; } = "";
    [Required] public string ClientSecret { get; set; } = "";

    [Required] public string SiteHostname { get; set; } = "";

    [Required] public string SitePath { get; set; } = "";

    [Required] public string DocumentLibraryName { get; set; } = "";

    public bool SubscriptionRenewalEnabled { get; set; } = true;
    [Url] public string NotificationUrl { get; set; } = "";
    [Required, MinLength(16)] public string ClientState { get; set; } = "";
    [Range(1, 29)] public int SubscriptionLifetimeDays { get; set; } = 28;
    [Range(1, 24)] public int RenewalCheckHours { get; set; } = 12;
}

public sealed class ServiceBusOptions
{
    public const string SectionName = "ServiceBus";

    /// <summary>
    /// Whether Service Bus is available to this application. When false no client is registered and no
    /// connection settings are required, so features that depend on Service Bus must stay disabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public bool UsedManagedIdentity { get; set; }
    public string? FullyQualifiedNamespace { get; set; }
    public string? ConnectionString { get; set; }
    [Required] public string TopicName { get; set; } = "sharepoint-changes";
    [Required] public string SubscriptionName { get; set; } = "search-indexer";

    /// <summary>
    /// True when the settings required to create a Service Bus client are present.
    /// </summary>
    public bool IsConfigured => UsedManagedIdentity
        ? !string.IsNullOrWhiteSpace(FullyQualifiedNamespace)
        : !string.IsNullOrWhiteSpace(ConnectionString);
}

public sealed class SearchOptions
{
    public const string SectionName = "AzureSearch";
    public bool UsedManagedIdentity { get; set; }
    [Required, Url] public string Endpoint { get; set; } = "";
    public string? ApiKey { get; set; }
    [Required] public string SharePointIndexName { get; set; } = "sharepoint-files";
    [Required] public string UploadIndexName { get; set; } = "chat-uploads";
    [Range(1, 4096)] public int VectorDimensions { get; set; } = 1536;
}

public sealed class UploadOptions
{
    public string[] AllowedFileExtensions { get; set; } = [".pdf", ".docx", ".pptx", ".xlsx", ".txt", ".md", ".json", ".csv", ".png", ".jpg", ".jpeg", ".gif", ".webp"];

    public string[] TextFileExtensions { get; set; } = [".txt", ".md", ".json", ".csv"];
    public string[] ImageFileExtensions { get; set; } = [".png", ".jpg", ".jpeg", ".gif", ".webp"];
    public string[] GetImageFileExtensions() => NormalizeExtensions(ImageFileExtensions);
    public bool IsImageFile(string fileName) => GetImageFileExtensions()
        .Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    public string[] GetAllowedFileExtensions() => NormalizeExtensions(AllowedFileExtensions);
    public string[] GetTextFileExtensions() => NormalizeExtensions(TextFileExtensions);
    public bool IsTextFile(string fileName) => GetTextFileExtensions()
        .Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    private static string[] NormalizeExtensions(IEnumerable<string> extensions) => extensions
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Select(x => "." + x.Trim().TrimStart('.').ToLowerInvariant())
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public void ValidateFileName(string fileName)
    {
        var allowed = GetAllowedFileExtensions();
        if (!allowed.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"File type is not allowed. Allowed extensions: {string.Join(", ", allowed)}.", nameof(fileName));
        }
    }

    public const string SectionName = "Uploads";
    public bool UsedManagedIdentity { get; set; }
    public string? ConnectionString { get; set; }
    public string? ServiceUri { get; set; }
    [Required] public string ContainerName { get; set; } = "chat-uploads";
    [Range(1024, 209_715_200)] public int MaxFileBytes { get; set; } = 20 * 1024 * 1024;
    [Range(100, 8000)] public int ChunkSizeCharacters { get; set; } = 4000;
    [Range(0, 2000)] public int ChunkOverlapCharacters { get; set; } = 400;

    public bool IsConfigured => UsedManagedIdentity
        ? Uri.TryCreate(ServiceUri, UriKind.Absolute, out _)
        : !string.IsNullOrWhiteSpace(ConnectionString);
}

public sealed class OpenAiOptions
{
    public const string SectionName = "AzureOpenAI";
    public bool UsedManagedIdentity { get; set; }
    [Required, Url] public string Endpoint { get; set; } = "";
    [Required] public string EmbeddingDeployment { get; set; } = "text-embedding-3-small";

    /// <summary>
    /// The chat deployment the assistant runs on, on the same resource as
    /// <see cref="EmbeddingDeployment"/>.
    /// </summary>
    [Required] public string ChatDeployment { get; set; } = "gpt-5-mini";

    public string? ApiKey { get; set; }
}

/// <summary>
/// The SQL Server database the worker keeps its own state in: the Microsoft Graph delta checkpoint, and a
/// record of what was last indexed for each SharePoint file. The record lets a delta pass tell an
/// unchanged file from a changed one, so an unchanged file is not downloaded, extracted, and embedded
/// again. Managed identity is expressed in the connection string — <c>Authentication=Active Directory
/// Default</c> — because SQL Server access is granted inside the database rather than by Azure RBAC.
/// </summary>
public sealed class SqlServerOptions
{
    public const string SectionName = "SqlServer";

    [Required] public string ConnectionString { get; set; } = "";

    /// <summary>
    /// Whether an application applies pending Entity Framework Core migrations as it starts. Set it to
    /// false when the schema is deployed by the pipeline and the application's login has no DDL rights.
    /// The schema itself is defined by <c>SharePointIndexDbContext</c>, not by configuration.
    /// </summary>
    public bool AutoMigrate { get; set; } = true;

    [Range(1, 600)] public int CommandTimeoutSeconds { get; set; } = 30;
}

public sealed class DocumentIntelligenceOptions
{
    public const string SectionName = "DocumentIntelligence";
    public bool UsedManagedIdentity { get; set; }
    public string? Endpoint { get; set; }
    public string ModelId { get; set; } = "prebuilt-read";
    public string ApiVersion { get; set; } = "2024-11-30";
    public string? ApiKey { get; set; }
}

/// <summary>Which service extracts text for a file type: Document Intelligence or MarkItDown.</summary>
public enum ContentExtractionMethod
{
    DocumentIntelligence,
    MarkItDown,
}

public sealed class ContentExtractionOptions
{
    public const string SectionName = "ContentExtraction";
    public ContentExtractionMethod Pdf { get; set; } = ContentExtractionMethod.DocumentIntelligence;
    public ContentExtractionMethod Docx { get; set; } = ContentExtractionMethod.MarkItDown;
    public ContentExtractionMethod Pptx { get; set; } = ContentExtractionMethod.MarkItDown;
    public ContentExtractionMethod Xlsx { get; set; } = ContentExtractionMethod.MarkItDown;
}

public sealed class MarkItDownOptions
{
    public string? ApiKey { get; set; }

    public const string SectionName = "MarkItDown";

    /// <summary>
    /// Base address of the MarkItDown service, for example <c>http://localhost:8000</c>. Required, because
    /// DOCX, PPTX, and XLSX files are converted there.
    /// </summary>
    [Url] public string? Endpoint { get; set; }

    public string ConvertPath { get; set; } = "/convert";
    [Required] public string HealthPath { get; set; } = "/health";
    [Range(1, 3600)] public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Whether the worker probes <see cref="HealthPath"/> as it starts and on an interval, so an
    /// unreachable service is reported before the next file needs converting.
    /// </summary>
    public bool HealthCheckEnabled { get; set; } = true;

    [Range(1, 1440)] public int HealthCheckMinutes { get; set; } = 5;

    /// <summary>
    /// True when an endpoint is configured, so the client can be called.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint);
}

/// <summary>
/// The one directory on this host the chat assistant works in. SharePoint downloads and conversation
/// attachments are cached here, and the agent's file tools can read, write, and organise inside it and
/// nowhere else. The directory is also a cache: a file already on disk is handed back as it is rather
/// than downloaded again.
/// </summary>
public sealed class LocalWorkingDirectoryOptions
{
    public const string SectionName = "LocalWorkingDirectory";

    /// <summary>
    /// Root of the working directory. A relative path resolves against the process working directory.
    /// Empty — the default — puts it in <c>sharepoint-agent</c> under the system temporary
    /// directory. In a Foundry sandbox this should be a path the session keeps between turns.
    /// </summary>
    public string Directory { get; set; } = "";

    /// <summary>Limits on what the SharePoint download and upload tools move through the directory.</summary>
    public DownloadOptions Downloads { get; set; } = new();

    /// <summary>The absolute root directory, whatever form <see cref="Directory"/> was configured in.</summary>
    public string ResolvedDirectory => Path.GetFullPath(string.IsNullOrWhiteSpace(Directory)
        ? Path.Combine(Path.GetTempPath(), "sharepoint-agent")
        : Directory);

    /// <summary>
    /// Everything fetched from elsewhere, under one folder of the working directory rather than at the
    /// top of it. That leaves the top level for what the agent writes itself, so a listing tells its
    /// own work from copies of other people's documents. These are derived rather than configured, so
    /// the parts cannot be pointed at different disks and the agent's file tools always reach them.
    /// </summary>
    public string ResolvedDownloadsDirectory => Path.Combine(ResolvedDirectory, DownloadsFolderName);

    /// <summary>Library documents, one folder per drive item.</summary>
    public string ResolvedSharePointDirectory => Path.Combine(ResolvedDownloadsDirectory, SharePointFolderName);

    /// <summary>Conversation attachments and the Markdown they were converted to, one folder each.</summary>
    public string ResolvedAttachmentsDirectory => Path.Combine(ResolvedDownloadsDirectory, AttachmentsFolderName);

    /// <summary>The folder names the agent sees in a listing and uses in a path.</summary>
    public const string DownloadsFolderName = "Downloads";

    public const string SharePointFolderName = "SharePoint";

    public const string AttachmentsFolderName = "Attachments";
}

/// <summary>
/// How large a file the download and upload tools will move. Nested under
/// <see cref="LocalWorkingDirectoryOptions"/>, because it is a limit on what passes through that
/// directory rather than a place of its own.
/// </summary>
public sealed class DownloadOptions
{
    [Range(1024, 209_715_200)] public int MaxFileBytes { get; set; } = 20 * 1024 * 1024;
}

public sealed class ProcessorOptions
{
    public const string SectionName = "Processor";
    [Range(1024, 104_857_600)] public int MaxFileBytes { get; set; } = 20 * 1024 * 1024;
    [Range(100, 8000)] public int ChunkSizeCharacters { get; set; } = 4000;
    [Range(0, 2000)] public int ChunkOverlapCharacters { get; set; } = 400;
    public bool SyncOnStartup { get; set; } = true;
    public bool ChangeSignalListenerEnabled { get; set; } = true;
    public bool ScheduledSyncEnabled { get; set; } = true;
    [Range(1, 1440)] public int ScheduledSyncMinutes { get; set; } = 5;

    /// <summary>
    /// File extensions eligible for indexing; files with any other extension are skipped. Entries are
    /// matched case-insensitively, with or without a leading dot.
    /// </summary>
    public IList<string> AllowedFileExtensions { get; set; } = [];
}
