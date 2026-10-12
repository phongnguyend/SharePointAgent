using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharePointAgent.Infrastructure;

public sealed record MicrosoftDecisionResult(string Model, Dictionary<string, MicrosoftDecisionAnswer> Answers, MicrosoftDecisionTokenUsage Usage);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(MicrosoftDecisionChoiceAnswer), "choice")]
[JsonDerivedType(typeof(MicrosoftDecisionScoreAnswer), "score")]
[JsonDerivedType(typeof(MicrosoftDecisionNoulAnswer), "noul")]
public abstract record MicrosoftDecisionAnswer;

public sealed record MicrosoftDecisionChoiceAnswer(string Choice, Dictionary<string, double> Probabilities, double Confidence) : MicrosoftDecisionAnswer;

public sealed record MicrosoftDecisionScoreAnswer(double Score, Dictionary<string, JsonElement> Legend,
    Dictionary<string, double> Probabilities, double Confidence) : MicrosoftDecisionAnswer;

public sealed record MicrosoftDecisionNoulAnswer(double Noul) : MicrosoftDecisionAnswer;

public sealed record MicrosoftDecisionTokenUsage(
    [property: JsonPropertyName("input_tokens")] long InputTokens,
    [property: JsonPropertyName("output_tokens")] long OutputTokens);

