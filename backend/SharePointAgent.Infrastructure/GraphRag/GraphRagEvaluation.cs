namespace SharePointAgent.Infrastructure.GraphRag;

/// <summary>One labelled question: the chunks a complete answer has to draw on.</summary>
public sealed record GraphEvaluationCase(string Question, IReadOnlyList<string> ExpectedChunkIds);

/// <summary>What one retrieval path returned for one question, and how long it took.</summary>
public sealed record GraphEvaluationRun(string Question, IReadOnlyList<string> RetrievedChunkIds, double LatencyMilliseconds);

public sealed record GraphEvaluationSummary(
    int Cases,
    double MeanRecall,
    double MeanPrecision,
    double FullRecallRate,
    double P50LatencyMilliseconds,
    double P95LatencyMilliseconds);

/// <summary>
/// Retrieval-level comparison of the baseline and graph-augmented paths on a labelled set of cross-document
/// questions. Recall is the share of the expected chunks a path returned. Full-recall rate is the share of
/// questions whose expected chunks were all returned, which is the multi-hop measure: a question spanning
/// three documents only counts when all three are retrieved. These are retrieval measures; answer
/// correctness and faithfulness need a judged answer set on top.
/// </summary>
public static class GraphRagEvaluation
{
    public static GraphEvaluationSummary Summarize(IReadOnlyList<GraphEvaluationCase> cases, IReadOnlyList<GraphEvaluationRun> runs)
    {
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(runs);
        if (cases.Count == 0)
        {
            return new GraphEvaluationSummary(0, 0, 0, 0, 0, 0);
        }

        var byQuestion = runs.ToDictionary(run => run.Question, StringComparer.Ordinal);
        double recall = 0, precision = 0, full = 0;
        foreach (var @case in cases)
        {
            var expected = @case.ExpectedChunkIds.ToHashSet(StringComparer.Ordinal);
            var retrieved = byQuestion.TryGetValue(@case.Question, out var run)
                ? run.RetrievedChunkIds.ToHashSet(StringComparer.Ordinal)
                : [];
            var found = expected.Count(retrieved.Contains);
            recall += expected.Count == 0 ? 1 : (double)found / expected.Count;
            precision += retrieved.Count == 0 ? (expected.Count == 0 ? 1 : 0) : (double)retrieved.Count(expected.Contains) / retrieved.Count;
            full += found == expected.Count ? 1 : 0;
        }

        var latencies = runs.Select(run => run.LatencyMilliseconds).Order().ToList();
        return new GraphEvaluationSummary(
            cases.Count,
            recall / cases.Count,
            precision / cases.Count,
            full / cases.Count,
            Percentile(latencies, 0.50),
            Percentile(latencies, 0.95));
    }

    /// <summary>Nearest-rank percentile.</summary>
    internal static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var rank = (int)Math.Ceiling(percentile * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }
}
