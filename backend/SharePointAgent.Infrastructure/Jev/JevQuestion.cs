using System.Text.Json.Serialization;

namespace SharePointAgent.Infrastructure;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(JevChoiceQuestion), "choice")]
[JsonDerivedType(typeof(JevScoreQuestion), "score")]
[JsonDerivedType(typeof(JevNoulQuestion), "noul")]
public abstract record JevQuestion(object Instructions);

public sealed record JevChoiceQuestion(object Instructions, IReadOnlyDictionary<string, object?> Criteria)
    : JevQuestion(Instructions);

public sealed record JevScoreQuestion(object Instructions, IReadOnlyList<object> Criteria)
    : JevQuestion(Instructions);

public sealed record JevNoulQuestion(object Instructions,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, object>? Criteria = null)
    : JevQuestion(Instructions);
