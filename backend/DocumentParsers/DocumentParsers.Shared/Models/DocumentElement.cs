namespace DocumentParsers;

public abstract record DocumentElement
{
    public int? PageNumber { get; init; }

    public long? Order { get; init; }

    public DocumentBoundingBox? BoundingBox { get; init; }
}
