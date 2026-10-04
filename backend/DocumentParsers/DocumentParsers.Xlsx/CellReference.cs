using System.Globalization;

namespace DocumentParsers;

public static class CellReference
{
    public static (int Row, int Column) Parse(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        var index = 0;
        var column = 0;
        while (index < reference.Length && char.IsAsciiLetter(reference[index]))
        {
            column = checked(column * 26 + char.ToUpperInvariant(reference[index++]) - 'A' + 1);
            if (column > 16384)
            {
                throw new FormatException("Column exceeds Excel's supported range.");
            }
        }
        if (column == 0 || !int.TryParse(reference.AsSpan(index), NumberStyles.None, CultureInfo.InvariantCulture, out var row) || row < 1 || row > 1048576)
        {
            throw new FormatException("Invalid cell reference.");
        }
        return (row - 1, column - 1);
    }

    public static string Format(int row, int column)
    {
        if (row < 0 || row >= 1048576 || column < 0 || column >= 16384)
        {
            throw new ArgumentOutOfRangeException(nameof(row));
        }
        var letters = string.Empty;
        for (var value = column + 1; value > 0; value = (value - 1) / 26)
        {
            letters = (char)('A' + (value - 1) % 26) + letters;
        }
        return letters + (row + 1).ToString(CultureInfo.InvariantCulture);
    }
}
