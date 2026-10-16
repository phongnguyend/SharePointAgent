namespace SharePointAgent.Application;

public sealed record ContentSafetyCategoryScore(string Category, int Severity, int Threshold);

public sealed record ContentSafetyAssessment(
    Guid AssessmentId, string Status, IReadOnlyList<ContentSafetyCategoryScore> Categories,
    int CharacterCount, long ElapsedMs, string? TraceId);
