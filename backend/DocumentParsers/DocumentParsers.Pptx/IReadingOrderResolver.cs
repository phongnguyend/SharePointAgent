namespace DocumentParsers;

public interface IReadingOrderResolver
{
    IReadOnlyList<DocumentElement> Resolve(IEnumerable<DocumentElement> elements);
}

public sealed class PositionReadingOrderResolver : IReadingOrderResolver
{
    public IReadOnlyList<DocumentElement> Resolve(IEnumerable<DocumentElement> elements) => elements
        .OrderBy(element => element.BoundingBox?.Y ?? double.PositiveInfinity)
        .ThenBy(element => element.BoundingBox?.X ?? double.PositiveInfinity)
        .ToArray();
}
