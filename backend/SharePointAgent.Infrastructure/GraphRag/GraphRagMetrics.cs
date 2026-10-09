using System.Diagnostics;
using System.Diagnostics.Metrics;
using SharePointAgent.Infrastructure.Monitoring;

namespace SharePointAgent.Infrastructure.GraphRag;

/// <summary>
/// Graph RAG instruments, on the meter <see cref="Telemetry"/> already exports. Tags carry outcomes, reason
/// codes, and operation names only; never document text, entity names, IDs, or paths.
/// </summary>
public static class GraphRagMetrics
{
    public const string MeterName = Telemetry.SourceName;

    private static readonly Meter Meter = new(MeterName);

    public static readonly ActivitySource Activities = Telemetry.Activities;

    private static readonly Counter<long> IndexingDocuments = Meter.CreateCounter<long>(
        "graph_rag.indexing.documents", description: "Graph indexing requests processed, by outcome.");

    private static readonly Counter<long> IndexingFailures = Meter.CreateCounter<long>(
        "graph_rag.indexing.failures", description: "Graph indexing failures, by reason code and whether they are retried.");

    private static readonly Counter<long> ExtractionTokens = Meter.CreateCounter<long>(
        "graph_rag.extraction.tokens", unit: "{token}", description: "Tokens spent on graph extraction.");

    private static readonly Counter<long> RejectedExtractions = Meter.CreateCounter<long>(
        "graph_rag.extraction.rejected", description: "Extracted entities and relations rejected by grounding, by reason.");

    private static readonly Counter<long> ResolutionDecisions = Meter.CreateCounter<long>(
        "graph_rag.entity_resolution.decisions", description: "Entity resolution decisions, by method.");

    private static readonly Counter<long> ConcurrencyConflicts = Meter.CreateCounter<long>(
        "graph_rag.store.concurrency_conflicts", description: "Optimistic concurrency conflicts, by record kind.");

    private static readonly Histogram<double> RequestCharge = Meter.CreateHistogram<double>(
        "graph_rag.store.request_charge", unit: "RU", description: "Graph store request charge, by operation.");

    private static readonly Counter<long> Throttled = Meter.CreateCounter<long>(
        "graph_rag.store.throttled", description: "Graph store requests throttled (HTTP 429).");

    private static readonly Histogram<double> TraversalDuration = Meter.CreateHistogram<double>(
        "graph_rag.traversal.duration", unit: "ms", description: "Graph traversal latency.");

    private static readonly Counter<long> RetrievalOutcomes = Meter.CreateCounter<long>(
        "graph_rag.retrieval.outcomes", description: "Graph retrieval outcomes, by status.");

    private static readonly Histogram<double> RetrievalDuration = Meter.CreateHistogram<double>(
        "graph_rag.retrieval.duration", unit: "ms", description: "End-to-end graph augmentation latency.");

    private static readonly Counter<long> AclRejections = Meter.CreateCounter<long>(
        "graph_rag.acl.rejections", description: "Candidate graph assertions rejected by evidence verification, by reason.");

    private static readonly Counter<long> ReconciliationRepairs = Meter.CreateCounter<long>(
        "graph_rag.reconciliation.repairs", description: "Documents reconciliation reprocessed, by kind.");

    public static void Indexed(string outcome) => IndexingDocuments.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public static void IndexingFailed(string code, bool retried) => IndexingFailures.Add(1,
        new KeyValuePair<string, object?>("code", code), new KeyValuePair<string, object?>("retried", retried));

    public static void Tokens(long input, long output)
    {
        ExtractionTokens.Add(input, new KeyValuePair<string, object?>("kind", "input"));
        ExtractionTokens.Add(output, new KeyValuePair<string, object?>("kind", "output"));
    }

    public static void Rejected(string reason, long count = 1)
    {
        if (count > 0)
        {
            RejectedExtractions.Add(count, new KeyValuePair<string, object?>("reason", reason));
        }
    }

    public static void Resolved(string method) => ResolutionDecisions.Add(1, new KeyValuePair<string, object?>("method", method));

    public static void Conflict(string kind) => ConcurrencyConflicts.Add(1, new KeyValuePair<string, object?>("kind", kind));

    public static void Charge(string operation, double requestCharge) =>
        RequestCharge.Record(requestCharge, new KeyValuePair<string, object?>("operation", operation));

    public static void Throttle(string operation) => Throttled.Add(1, new KeyValuePair<string, object?>("operation", operation));

    public static void Traversed(double milliseconds, bool truncated) =>
        TraversalDuration.Record(milliseconds, new KeyValuePair<string, object?>("truncated", truncated));

    public static void Retrieval(string status, double milliseconds)
    {
        RetrievalOutcomes.Add(1, new KeyValuePair<string, object?>("status", status));
        RetrievalDuration.Record(milliseconds, new KeyValuePair<string, object?>("status", status));
    }

    public static void AclRejected(string reason, long count = 1)
    {
        if (count > 0)
        {
            AclRejections.Add(count, new KeyValuePair<string, object?>("reason", reason));
        }
    }

    public static void Repaired(string kind) => ReconciliationRepairs.Add(1, new KeyValuePair<string, object?>("kind", kind));
}
