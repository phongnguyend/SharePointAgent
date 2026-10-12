using System.Text.Json.Serialization;

namespace SharePointAgent.Infrastructure;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(MicrosoftDecisionChoiceQuestion), "choice")]
[JsonDerivedType(typeof(MicrosoftDecisionScoreQuestion), "score")]
[JsonDerivedType(typeof(MicrosoftDecisionNoulQuestion), "noul")]
public abstract record MicrosoftDecisionQuestion(object Instructions);

public sealed record MicrosoftDecisionChoiceQuestion(object Instructions, IReadOnlyDictionary<string, object?> Criteria)
    : MicrosoftDecisionQuestion(Instructions);

public sealed record MicrosoftDecisionScoreQuestion(object Instructions, IReadOnlyList<object> Criteria)
    : MicrosoftDecisionQuestion(Instructions);

public sealed record MicrosoftDecisionNoulQuestion(object Instructions,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, object>? Criteria = null)
    : MicrosoftDecisionQuestion(Instructions);
