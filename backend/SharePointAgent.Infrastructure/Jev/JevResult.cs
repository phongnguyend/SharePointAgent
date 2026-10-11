using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharePointAgent.Infrastructure;

public sealed record JevResult(string Model, Dictionary<string, JevAnswer> Answers, JevTokenUsage Usage);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(JevChoiceAnswer), "choice")]
[JsonDerivedType(typeof(JevScoreAnswer), "score")]
[JsonDerivedType(typeof(JevNoulAnswer), "noul")]
public abstract record JevAnswer;

public sealed record JevChoiceAnswer(string Choice, Dictionary<string, double> Probabilities, double Confidence) : JevAnswer;

public sealed record JevScoreAnswer(double Score, Dictionary<string, JsonElement> Legend,
    Dictionary<string, double> Probabilities, double Confidence) : JevAnswer;

public sealed record JevNoulAnswer(double Noul) : JevAnswer;

public sealed record JevTokenUsage(
    [property: JsonPropertyName("input_tokens")] long InputTokens,
    [property: JsonPropertyName("output_tokens")] long OutputTokens);

public sealed record JevModel(string Name, string Description,
    [property: JsonPropertyName("release_date")] string ReleaseDate);

public sealed record JevModelsResult(IReadOnlyList<JevModel> Models);
