namespace SharePointAgent.Domain;

/// <summary>A snapshot of the source file's label and protection, before local decryption.</summary>
public sealed record FileSensitivity(
    string? LabelId, string? LabelName, bool IsLabeled, bool IsEncrypted, DateTimeOffset CheckedAtUtc);

public sealed record ReadableFileContent(byte[] Content, FileSensitivity? Sensitivity);
