using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure.GraphRag;

public sealed record GraphEntityLinkResult(IReadOnlyList<string> EntityIds, int AmbiguousPhrases);

/// <summary>
/// Links phrases in a question to entities by exact normalized alias, within the reader's tenant. A phrase
/// that names more than one entity is not linked at all: guessing would start the traversal from the wrong
/// entity, and the baseline search already covers the question.
/// </summary>
public sealed class GraphEntityLinker(IEntityAliasIndex aliases)
{
    private const int MaxPhraseWords = 4;

    // Enough for every 1- to 4-word phrase of a 30-word question; shorter phrases come first if it is reached.
    private const int MaxPhrases = 120;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "can", "do", "does", "for", "from", "has", "have", "how", "i",
        "if", "in", "is", "it", "its", "of", "on", "or", "our", "that", "the", "their", "this", "to", "was", "we",
        "what", "when", "where", "which", "who", "why", "will", "with", "would", "you", "your", "any", "all",
        "depend", "depends", "depending", "dependency", "dependencies", "impact", "impacts", "affect", "affects",
        "own", "owns", "owner", "owned", "related", "relationship", "relationships", "system", "systems",
        "project", "projects", "policy", "policies", "team", "teams", "document", "documents"
    };

    private static readonly char[] TrimCharacters = ['.', ',', '?', '!', ';', ':', '"', '\'', '(', ')', '[', ']', '{', '}', '“', '”', '‘', '’'];

    public async Task<GraphEntityLinkResult> LinkAsync(string tenantId, string question, CancellationToken cancellationToken)
    {
        var phrases = Phrases(question);
        if (phrases.Count == 0)
        {
            return new GraphEntityLinkResult([], 0);
        }

        var matches = await aliases.FindAsync(tenantId, null, phrases, cancellationToken);
        var entityIds = new List<string>();
        var ambiguous = 0;

        // Longer phrases first, so "data retention policy" wins over a separate match on "retention".
        foreach (var phrase in phrases.OrderByDescending(phrase => phrase.Length))
        {
            if (!matches.TryGetValue(phrase, out var ids) || ids.Count == 0)
            {
                continue;
            }

            var distinct = ids.Distinct(StringComparer.Ordinal).ToList();
            if (distinct.Count > 1)
            {
                ambiguous++;
                continue;
            }

            if (!entityIds.Contains(distinct[0]))
            {
                entityIds.Add(distinct[0]);
            }
        }
        return new GraphEntityLinkResult(entityIds, ambiguous);
    }

    /// <summary>Word n-grams of the question in normalized form, without phrases made only of stop words.</summary>
    internal static IReadOnlyList<string> Phrases(string? question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return [];
        }

        var words = GraphNormalization.NormalizeName(question)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Trim(TrimCharacters))
            .Where(word => word.Length > 0)
            .ToList();
        var phrases = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var length = 1; length <= Math.Min(MaxPhraseWords, words.Count); length++)
        {
            for (var start = 0; start + length <= words.Count; start++)
            {
                var slice = words.GetRange(start, length);
                if (slice.All(StopWords.Contains) || (length == 1 && slice[0].Length < 2))
                {
                    continue;
                }

                var phrase = string.Join(' ', slice);
                if (seen.Add(phrase))
                {
                    phrases.Add(phrase);
                }

                if (phrases.Count >= MaxPhrases)
                {
                    return phrases;
                }
            }
        }
        return phrases;
    }
}
