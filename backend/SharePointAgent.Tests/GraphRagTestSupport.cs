using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Tests;

/// <summary>
/// In-memory implementations of the Graph RAG ports. They keep the contracts the Cosmos adapter keeps:
/// optimistic concurrency on states and entities, tenant scoping, and retracted assertions hidden from reads.
/// </summary>
internal sealed class InMemoryGraphProjectionStore : IGraphProjectionStore
{
    private readonly Dictionary<(string Tenant, string Id), (GraphDocumentState State, int Version)> _states = [];
    private readonly Dictionary<(string Tenant, string Id), (GraphEntityRecord Record, int Version)> _entities = [];
    private readonly Dictionary<string, GraphAssertion> _assertions = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public List<string> Operations { get; } = [];

    public Func<string, Exception?>? FailOn { get; set; }

    public double ChargePerRead { get; set; }

    public TimeSpan ReadDelay { get; set; }

    public int AdjacencyReads { get; private set; }

    public IReadOnlyCollection<GraphAssertion> Assertions
    {
        get
        {
            lock (_gate)
            {
                return _assertions.Values.ToList();
            }
        }
    }

    public Task<IReadOnlyList<GraphDocumentState>> GetDocumentStatesAsync(string tenantId, IReadOnlyCollection<string> documentIds, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<GraphDocumentState> found = documentIds
                .Where(id => _states.ContainsKey((tenantId, id)))
                .Select(id => _states[(tenantId, id)])
                .Select(entry => entry.State with { ConcurrencyToken = entry.Version.ToString() })
                .ToList();
            return Task.FromResult(found);
        }
    }

    public Task<string> SaveDocumentStateAsync(GraphDocumentState state, TimeSpan? timeToLive, CancellationToken cancellationToken)
    {
        Fail("saveDocumentState");
        lock (_gate)
        {
            Operations.Add($"state:{state.Status}");
            var key = (state.TenantId, state.DocumentId);
            var exists = _states.TryGetValue(key, out var current);
            if ((state.ConcurrencyToken is null && exists) || (state.ConcurrencyToken is not null && (!exists || current.Version.ToString() != state.ConcurrencyToken)))
            {
                throw new GraphConcurrencyException("conflict");
            }

            var version = exists ? current.Version + 1 : 1;
            _states[key] = (state with { ConcurrencyToken = null }, version);
            return Task.FromResult(version.ToString());
        }
    }

    public async IAsyncEnumerable<GraphDocumentState> ListDocumentStatesAsync(string tenantId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        List<GraphDocumentState> states;
        lock (_gate)
        {
            states = _states.Where(pair => pair.Key.Tenant == tenantId).Select(pair => pair.Value.State with { ConcurrencyToken = pair.Value.Version.ToString() }).ToList();
        }

        foreach (var state in states)
        {
            await Task.Yield();
            yield return state;
        }
    }

    public Task<IReadOnlyList<GraphEntityRecord>> GetEntitiesAsync(string tenantId, IReadOnlyCollection<string> entityIds, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<GraphEntityRecord> found = entityIds.Distinct()
                .Where(id => _entities.ContainsKey((tenantId, id)))
                .Select(id => _entities[(tenantId, id)])
                .Select(entry => entry.Record with { ConcurrencyToken = entry.Version.ToString() })
                .ToList();
            return Task.FromResult(found);
        }
    }

    public Task<string> SaveEntityAsync(GraphEntityRecord entity, CancellationToken cancellationToken)
    {
        Fail("saveEntity");
        lock (_gate)
        {
            Operations.Add("entity");
            var key = (entity.Entity.TenantId, entity.Entity.EntityId);
            var exists = _entities.TryGetValue(key, out var current);
            if ((entity.ConcurrencyToken is null && exists) || (entity.ConcurrencyToken is not null && (!exists || current.Version.ToString() != entity.ConcurrencyToken)))
            {
                throw new GraphConcurrencyException("conflict");
            }

            var version = exists ? current.Version + 1 : 1;
            _entities[key] = (entity with { ConcurrencyToken = null }, version);
            return Task.FromResult(version.ToString());
        }
    }

    public Task UpsertAssertionsAsync(IReadOnlyList<GraphAssertion> assertions, CancellationToken cancellationToken)
    {
        Fail("upsertAssertions");
        lock (_gate)
        {
            Operations.Add("assertions");
            foreach (var assertion in assertions)
            {
                _assertions[assertion.AssertionId] = assertion;
            }
        }
        return Task.CompletedTask;
    }

    public Task SetAssertionStatusAsync(IReadOnlyList<GraphAssertionKey> keys, GraphAssertionStatus status, TimeSpan? timeToLive, CancellationToken cancellationToken)
    {
        Fail("setAssertionStatus");
        lock (_gate)
        {
            Operations.Add($"status:{status}");
            foreach (var key in keys)
            {
                if (_assertions.TryGetValue(key.AssertionId, out var assertion))
                {
                    _assertions[key.AssertionId] = assertion with { Status = status };
                }
            }
        }
        return Task.CompletedTask;
    }

    public async Task<GraphAdjacencyPage> GetAdjacentAssertionsAsync(
        string tenantId, IReadOnlyCollection<string> entityIds, IReadOnlySet<string> predicates, GraphTraversalDirection direction, int maxResults, CancellationToken cancellationToken)
    {
        Fail("readAdjacency");
        if (ReadDelay > TimeSpan.Zero)
        {
            await Task.Delay(ReadDelay, cancellationToken);
        }

        lock (_gate)
        {
            AdjacencyReads++;
            var ids = entityIds.ToHashSet(StringComparer.Ordinal);
            var found = _assertions.Values
                .Where(assertion => assertion.TenantId == tenantId && assertion.Status != GraphAssertionStatus.Retracted && predicates.Contains(assertion.Predicate))
                .Where(assertion =>
                    (direction != GraphTraversalDirection.Incoming && ids.Contains(assertion.SubjectEntityId))
                    || (direction != GraphTraversalDirection.Outgoing && ids.Contains(assertion.ObjectEntityId)))
                .OrderBy(assertion => assertion.AssertionId, StringComparer.Ordinal)
                .ToList();
            return new GraphAdjacencyPage(found.Take(maxResults).ToList(), ChargePerRead, found.Count > maxResults);
        }
    }

    /// <summary>Adds an assertion directly, bypassing the writer, for traversal and tampering tests.</summary>
    public void Seed(GraphAssertion assertion)
    {
        lock (_gate)
        {
            _assertions[assertion.AssertionId] = assertion;
        }
    }

    public void SeedState(GraphDocumentState state)
    {
        lock (_gate)
        {
            var version = _states.TryGetValue((state.TenantId, state.DocumentId), out var current) ? current.Version + 1 : 1;
            _states[(state.TenantId, state.DocumentId)] = (state, version);
        }
    }

    private void Fail(string operation)
    {
        if (FailOn?.Invoke(operation) is { } exception)
        {
            throw exception;
        }
    }
}

internal sealed class InMemoryAliasIndex : IEntityAliasIndex
{
    private readonly List<EntityAliasEntry> _entries = [];

    public IReadOnlyList<EntityAliasEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToList();
            }
        }
    }

    public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> FindAsync(
        string tenantId, string? entityType, IReadOnlyCollection<string> normalizedAliases, CancellationToken cancellationToken)
    {
        lock (_entries)
        {
            IReadOnlyDictionary<string, IReadOnlyList<string>> found = _entries
                .Where(entry => entry.TenantId == tenantId && (entityType is null || entry.EntityType == entityType) && normalizedAliases.Contains(entry.NormalizedAlias))
                .GroupBy(entry => entry.NormalizedAlias)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(entry => entry.EntityId).Distinct().ToList());
            return Task.FromResult(found);
        }
    }

    public Task UpsertAsync(IReadOnlyCollection<EntityAliasEntry> entries, CancellationToken cancellationToken)
    {
        lock (_entries)
        {
            foreach (var entry in entries)
            {
                if (!_entries.Contains(entry))
                {
                    _entries.Add(entry);
                }
            }
        }
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryArchive : ICanonicalGraphArchive
{
    private readonly Dictionary<string, CanonicalGraphSnapshot> _snapshots = new(StringComparer.Ordinal);

    public List<string> Operations { get; } = [];

    public int Count => _snapshots.Count;

    public Task<CanonicalGraphSnapshot> SaveAsync(CanonicalGraphSnapshot snapshot, CancellationToken cancellationToken)
    {
        Operations.Add("save");
        var key = Key(snapshot.TenantId, snapshot.DocumentId, snapshot.DocumentVersion, snapshot.Extraction.ExtractionVersion);
        if (!_snapshots.TryGetValue(key, out var stored))
        {
            stored = snapshot;
            _snapshots[key] = snapshot;
        }
        return Task.FromResult(stored);
    }

    public Task<CanonicalGraphSnapshot?> TryLoadAsync(string tenantId, string documentId, string documentVersion, string extractionVersion, CancellationToken cancellationToken) =>
        Task.FromResult(_snapshots.GetValueOrDefault(Key(tenantId, documentId, documentVersion, extractionVersion)));

    private static string Key(string tenant, string document, string version, string extraction) => $"{tenant}|{document}|{version}|{extraction}";
}

internal sealed class InMemoryFileMetadata : IFileMetadataRepository
{
    private readonly Dictionary<(string, string), FileIndexRecord> _records = [];

    public Func<FileIndexRecord, FileIndexRecord>? OnSecondRead { get; set; }

    private readonly Dictionary<(string, string), int> _reads = [];

    public void Put(FileIndexRecord record)
    {
        lock (_records)
        {
            _records[(record.DriveId, record.ItemId)] = record;
        }
    }

    public void Remove(string driveId, string itemId)
    {
        lock (_records)
        {
            _records.Remove((driveId, itemId));
        }
    }

    public Task<FileIndexRecord?> GetAsync(string driveId, string itemId, CancellationToken cancellationToken)
    {
        lock (_records)
        {
            if (!_records.TryGetValue((driveId, itemId), out var record))
            {
                return Task.FromResult<FileIndexRecord?>(null);
            }

            var reads = _reads.GetValueOrDefault((driveId, itemId)) + 1;
            _reads[(driveId, itemId)] = reads;
            return Task.FromResult<FileIndexRecord?>(reads == 2 && OnSecondRead is not null ? OnSecondRead(record) : record);
        }
    }

    public Task SaveAsync(FileIndexRecord record, CancellationToken cancellationToken)
    {
        Put(record);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string driveId, string itemId, CancellationToken cancellationToken)
    {
        Remove(driveId, itemId);
        return Task.CompletedTask;
    }

    public Task MarkSeenAsync(string driveId, string itemId, Guid scanId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<string>> ListItemsOutsideScanAsync(string driveId, Guid scanId, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public IReadOnlyList<FileIndexRecord> All()
    {
        lock (_records)
        {
            return _records.Values.ToList();
        }
    }
}

/// <summary>
/// Stands in for the permission-trimmed search: each user sees only the chunks granted to them, and the
/// content is whatever the index currently holds.
/// </summary>
internal sealed class FakeAuthorizedChunkStore : IAuthorizedChunkStore
{
    private readonly Dictionary<string, SearchQueryHit> _chunks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _grants = new(StringComparer.Ordinal);

    public int Calls { get; private set; }

    public void Put(string driveId, string itemId, int chunkNumber, string content, string name = "file.docx")
    {
        var id = SearchChunkKey.For(driveId, itemId, chunkNumber);
        _chunks[id] = new SearchQueryHit(id, driveId, itemId, name, "/docs", $"https://contoso/{itemId}", null, null, null, chunkNumber, content, null);
    }

    public void Grant(string userId, string driveId, string itemId, int chunkNumber)
    {
        if (!_grants.TryGetValue(userId, out var granted))
        {
            granted = new HashSet<string>(StringComparer.Ordinal);
            _grants[userId] = granted;
        }
        granted.Add(SearchChunkKey.For(driveId, itemId, chunkNumber));
    }

    public void Revoke(string userId, string driveId, string itemId, int chunkNumber) =>
        _grants.GetValueOrDefault(userId)?.Remove(SearchChunkKey.For(driveId, itemId, chunkNumber));

    public Task<IReadOnlyList<SearchQueryHit>> GetAuthorizedChunksAsync(string userId, IReadOnlyCollection<string> chunkIds, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("A user is required.", nameof(userId));
        }

        Calls++;
        var granted = _grants.GetValueOrDefault(userId) ?? [];
        IReadOnlyList<SearchQueryHit> hits = chunkIds.Where(granted.Contains).Where(_chunks.ContainsKey).Select(id => _chunks[id]).ToList();
        return Task.FromResult(hits);
    }

    public SearchQueryHit Hit(string driveId, string itemId, int chunkNumber) => _chunks[SearchChunkKey.For(driveId, itemId, chunkNumber)];
}

/// <summary>Returns a scripted extraction per chunk text and counts the calls, so tests can prove the model was not used.</summary>
internal sealed class ScriptedExtractor(Func<GraphExtractionInput, GraphExtractionResponse> script) : IGraphExtractor
{
    private int _calls;

    public int Calls => _calls;

    public string ModelId => "test-model";

    public string PromptVersion => "test-prompt";

    public Task<GraphExtractionResponse> ExtractAsync(GraphExtractionInput input, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(script(input));
    }
}

internal sealed class FakeChunkSource : IGraphChunkSource
{
    private readonly Dictionary<(string, string), List<GraphSourceChunk>> _chunks = [];

    public void Put(string driveId, string itemId, params string[] contents) =>
        _chunks[(driveId, itemId)] = contents.Select((content, index) => new GraphSourceChunk(SearchChunkKey.For(driveId, itemId, index), index, content)).ToList();

    public Task<IReadOnlyList<GraphSourceChunk>> GetChunksAsync(string driveId, string itemId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<GraphSourceChunk>>(_chunks.GetValueOrDefault((driveId, itemId)) ?? []);
}

/// <summary>Captures every rendered log message, for tests that prove restricted text never reaches logs.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_messages)
            {
                return _messages.ToList();
            }
        }
    }

    public ILogger<T> For<T>() => new Logger<T>(new LoggerFactory([this]));

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public void Dispose()
    {
    }

    private void Add(string message)
    {
        lock (_messages)
        {
            _messages.Add(message);
        }
    }

    private sealed class CapturingLogger(CapturingLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            provider.Add(formatter(state, exception) + (exception is null ? "" : " " + exception));
    }
}

/// <summary>Builders for consistent graph fixtures.</summary>
internal static class GraphFixtures
{
    public const string Tenant = "tenant-a";

    public const string Drive = "drive-1";

    public const string Fingerprint = "chunk=4000/400;embedding=e;dimensions=1536";

    public static readonly GraphOntology Ontology = GraphOntology.Create(GraphOntologyDefaults.CreateDefinition());

    public static IOptions<GraphRagOptions> Options(Action<GraphRagOptions>? configure = null)
    {
        var options = new GraphRagOptions { IndexingEnabled = true, RetrievalEnabled = true };
        configure?.Invoke(options);
        return Microsoft.Extensions.Options.Options.Create(options);
    }

    public static IOptions<SharePointOptions> SharePoint(string tenant = Tenant) =>
        Microsoft.Extensions.Options.Options.Create(new SharePointOptions { TenantId = tenant });

    public static string EntityId(string type, string name, string tenant = Tenant) =>
        GraphIds.ForEntity(tenant, type, GraphEntityKey.FromName(name));

    public static GraphEntity Entity(string type, string name, string tenant = Tenant) =>
        new(tenant, EntityId(type, name, tenant), type, name, [], new Dictionary<string, string>());

    public static FileIndexRecord File(string itemId, string cTag = "\"c:{1},1\"", int chunkCount = 1, string name = "file.docx", bool encrypted = false) => new(
        Drive, itemId, name, "/docs", $"https://contoso/{itemId}", null, 10, null, "\"e\"", cTag, "cGVybWlzc2lvbnM=", Fingerprint, chunkCount,
        Guid.Empty, DateTimeOffset.UnixEpoch, null,
        encrypted ? new FileSensitivity("label", "Secret", true, true, DateTimeOffset.UnixEpoch) : new FileSensitivity(null, null, false, false, DateTimeOffset.UnixEpoch));

    public static string Revision(FileIndexRecord record) => GraphDocumentIds.ForIndexedFile(record)!;

    public static EvidenceRef Evidence(FileIndexRecord record, int chunkNumber, string content, string tenant = Tenant) => new(
        tenant, GraphDocumentIds.ForDriveItem(record.DriveId, record.ItemId), Revision(record), SearchChunkKey.For(record.DriveId, record.ItemId, chunkNumber),
        GraphDocumentIds.ForChunkContent(content), null, null, null);

    public static GraphAssertion Assertion(GraphEntity subject, string predicate, GraphEntity @object, EvidenceRef evidence, GraphAssertionStatus status = GraphAssertionStatus.Asserted)
    {
        var symmetric = Ontology.Relations[predicate].Symmetric;
        var (first, second) = symmetric && string.CompareOrdinal(subject.EntityId, @object.EntityId) > 0 ? (@object, subject) : (subject, @object);
        var id = GraphIds.ForAssertion(evidence, first.EntityId, predicate, second.EntityId, symmetric);
        return new GraphAssertion(evidence.TenantId, id, first.EntityId, predicate, second.EntityId, evidence, status, Ontology.Version, "x1");
    }

    public static CanonicalGraphSnapshot Snapshot(FileIndexRecord record, IReadOnlyList<GraphEntity> entities, IReadOnlyList<GraphAssertion> assertions, string extractionVersion = "x1") => new(
        Tenant, GraphDocumentIds.ForDriveItem(record.DriveId, record.ItemId), Revision(record), CanonicalGraphSnapshot.CurrentSchemaVersion,
        new GraphExtractionProvenance(extractionVersion, "test-model", "test-prompt", Ontology.Version), entities,
        assertions.Select(assertion => assertion with { ExtractionVersion = extractionVersion }).ToList(),
        entities.Select((entity, index) => new EntityResolutionDecision($"0:e{index}", entity.EntityId, EntityResolutionMethod.NewEntity, [])).ToList());

    public static GraphDocumentState ReadyState(FileIndexRecord record, string tenant = Tenant) => new(
        tenant, GraphDocumentIds.ForDriveItem(record.DriveId, record.ItemId), GraphProjectionStatus.Ready, Revision(record), "x1", [], null, [], null, null, DateTimeOffset.UnixEpoch);

    public static ExtractedEntity Extracted(string reference, string type, string name, params (string Key, string Value)[] attributes) => new()
    {
        Ref = reference,
        Type = type,
        Name = name,
        Attributes = attributes.Select(attribute => new ExtractedAttribute { Key = attribute.Key, Value = attribute.Value }).ToList()
    };

    public static ExtractedRelation Relation(string subject, string predicate, string @object, string quote) => new()
    {
        SubjectRef = subject,
        Predicate = predicate,
        ObjectRef = @object,
        EvidenceQuote = quote
    };
}
