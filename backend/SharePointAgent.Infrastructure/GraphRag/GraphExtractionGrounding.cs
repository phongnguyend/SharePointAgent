using System.Text.RegularExpressions;
using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure.GraphRag;

public sealed record GroundedEntity(
    string Ref, string Type, string Name, IReadOnlyList<string> Aliases, IReadOnlyDictionary<string, string> Attributes);

public sealed record GroundedRelation(string SubjectRef, string Predicate, string ObjectRef);

public sealed record GroundedExtraction(
    IReadOnlyList<GroundedEntity> Entities,
    IReadOnlyList<GroundedRelation> Relations,
    int RejectedEntities,
    int RejectedRelations);

/// <summary>
/// Accepts only what the target chunk itself supports. Model output is treated as untrusted:
/// <list type="bullet">
/// <item>An entity's name must occur in the chunk.</item>
/// <item>Aliases and attribute values must occur in the chunk too.</item>
/// <item>Attributes must be ones the ontology allows for the type.</item>
/// <item>A relation must use a defined predicate in an allowed direction between grounded entities.</item>
/// <item>A relation must quote, word for word, the sentence of the chunk that states it.</item>
/// </list>
/// Neighbouring context helps the model disambiguate but is never accepted as evidence.
/// </summary>
public static partial class GraphExtractionGrounding
{
    private const int MaxQuoteCharacters = 600;

    private const int MinQuoteCharacters = 8;

    public static GroundedExtraction Ground(
        GraphExtractionResponse? response,
        string chunkText,
        GraphOntology ontology,
        int maxEntities,
        int maxRelations,
        int maxNameLength = 256,
        int maxAliases = 10)
    {
        if (response is null)
        {
            return new GroundedExtraction([], [], 0, 0);
        }

        var text = Fold(chunkText);
        var rejectedEntities = 0;
        var entities = new Dictionary<string, GroundedEntity>(StringComparer.Ordinal);
        foreach (var extracted in response.Entities ?? [])
        {
            if (extracted is null
                || entities.Count >= maxEntities
                || string.IsNullOrEmpty(extracted.Ref) || !RefPattern().IsMatch(extracted.Ref) || entities.ContainsKey(extracted.Ref)
                || !ontology.IsEntityType(extracted.Type)
                || string.IsNullOrWhiteSpace(extracted.Name) || extracted.Name.Length > maxNameLength
                || !Mentions(text, extracted.Name))
            {
                rejectedEntities++;
                continue;
            }

            var name = extracted.Name.Trim();
            var aliases = (extracted.Aliases ?? [])
                .Where(alias => !string.IsNullOrWhiteSpace(alias) && alias.Length <= maxNameLength && Mentions(text, alias))
                .Select(alias => alias.Trim())
                .Where(alias => GraphNormalization.NormalizeName(alias) != GraphNormalization.NormalizeName(name))
                .DistinctBy(GraphNormalization.NormalizeName, StringComparer.Ordinal)
                .Take(maxAliases)
                .ToList();
            var allowed = ontology.EntityTypes[extracted.Type].Attributes;
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var attribute in extracted.Attributes ?? [])
            {
                if (attribute is not null && allowed.Contains(attribute.Key) && !string.IsNullOrWhiteSpace(attribute.Value)
                    && attribute.Value.Length <= 256 && Mentions(text, attribute.Value))
                {
                    attributes.TryAdd(attribute.Key, attribute.Value.Trim());
                }
            }
            entities[extracted.Ref] = new GroundedEntity(extracted.Ref, extracted.Type, name, aliases, attributes);
        }

        var rejectedRelations = 0;
        var relations = new List<GroundedRelation>();
        var seen = new HashSet<(string, string, string)>();
        foreach (var extracted in response.Relations ?? [])
        {
            if (extracted is null || relations.Count >= maxRelations)
            {
                rejectedRelations++;
                continue;
            }

            var predicate = GraphNormalization.NormalizePredicate(extracted.Predicate ?? "");
            if (!entities.TryGetValue(extracted.SubjectRef ?? "", out var subject)
                || !entities.TryGetValue(extracted.ObjectRef ?? "", out var @object)
                || !ontology.TryGetRelation(predicate, out var relation)
                || !ontology.IsAllowedTriple(subject.Type, predicate, @object.Type)
                || (subject.Ref == @object.Ref && !relation.AllowSelfReference)
                || !IsGroundedQuote(text, extracted.EvidenceQuote))
            {
                rejectedRelations++;
                continue;
            }

            if (seen.Add((subject.Ref, predicate, @object.Ref)))
            {
                relations.Add(new GroundedRelation(subject.Ref, predicate, @object.Ref));
            }
        }

        // Entities nothing relates are kept only when they carry an identifier or alias worth resolving later;
        // a bare mention adds nothing to the graph.
        var related = relations.SelectMany(relation => new[] { relation.SubjectRef, relation.ObjectRef }).ToHashSet(StringComparer.Ordinal);
        var kept = entities.Values.Where(entity => related.Contains(entity.Ref) || entity.Attributes.Count > 0 || entity.Aliases.Count > 0).ToList();
        return new GroundedExtraction(kept, relations, rejectedEntities, rejectedRelations);
    }

    private static bool IsGroundedQuote(string foldedText, string? quote)
    {
        if (string.IsNullOrWhiteSpace(quote) || quote.Length > MaxQuoteCharacters)
        {
            return false;
        }

        var folded = Fold(quote);
        return folded.Length >= MinQuoteCharacters && foldedText.Contains(folded, StringComparison.Ordinal);
    }

    private static bool Mentions(string foldedText, string value)
    {
        var folded = Fold(value);
        return folded.Length > 0 && foldedText.Contains(folded, StringComparison.Ordinal);
    }

    /// <summary>
    /// Normalizes for matching only: the name normalization, plus typographic quotes and dashes folded to
    /// their plain forms, because models often straighten quotes when they copy text.
    /// </summary>
    internal static string Fold(string value) => GraphNormalization.NormalizeName(value
        .Replace('‘', '\'').Replace('’', '\'').Replace('“', '"').Replace('”', '"')
        .Replace('–', '-').Replace('—', '-'));

    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex RefPattern();
}
