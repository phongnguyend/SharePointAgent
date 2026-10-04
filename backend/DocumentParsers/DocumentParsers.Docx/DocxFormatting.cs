using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace DocumentParsers;

internal sealed class DocxFormatting(MainDocumentPart main, List<DocumentParseWarning> warnings)
{
    private readonly Dictionary<(int NumberId, int Level), int> _counters = [];

    internal IEnumerable<W.Style> Styles(string? id)
    {
        var visited = new HashSet<string>();
        while (id is not null && visited.Add(id))
        {
            var style = main.StyleDefinitionsPart?.Styles?.Elements<W.Style>().FirstOrDefault(value => value.StyleId?.Value == id);
            if (style is null)
            {
                yield break;
            }
            yield return style;
            id = style.BasedOn?.Val?.Value;
        }
    }

    private string? ParagraphStyle(W.Paragraph paragraph) => paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value
        ?? main.StyleDefinitionsPart?.Styles?.Elements<W.Style>().FirstOrDefault(style =>
            style.Type?.Value == W.StyleValues.Paragraph && style.Default?.Value == true)?.StyleId?.Value;

    internal bool IsBold(W.Run run, W.Paragraph paragraph)
    {
        var bold = main.StyleDefinitionsPart?.Styles?.DocDefaults?.RunPropertiesDefault?.RunPropertiesBaseStyle?.GetFirstChild<W.Bold>();
        var enabled = bold is not null && (bold.Val?.Value ?? true);
        foreach (var style in Styles(ParagraphStyle(paragraph)).Reverse().Concat(Styles(run.RunProperties?.RunStyle?.Val?.Value).Reverse()))
        {
            var toggle = style.StyleRunProperties?.GetFirstChild<W.Bold>();
            if (toggle is not null && (toggle.Val?.Value ?? true))
            {
                enabled = !enabled;
            }
        }
        var direct = run.RunProperties?.Bold;
        return direct is null ? enabled : direct.Val?.Value ?? true;
    }

    internal string ListPrefix(W.Paragraph paragraph)
    {
        var properties = new[] { paragraph.ParagraphProperties?.NumberingProperties }
            .Concat(Styles(ParagraphStyle(paragraph)).Select(style => style.StyleParagraphProperties?.NumberingProperties)).ToArray();
        var id = properties.Select(value => value?.NumberingId?.Val?.Value).FirstOrDefault(value => value is not null);
        if (id is null or 0)
        {
            return string.Empty;
        }
        var depth = Math.Clamp(properties.Select(value => value?.NumberingLevelReference?.Val?.Value)
            .FirstOrDefault(value => value is not null) ?? 0, 0, 8);
        var instance = main.NumberingDefinitionsPart?.Numbering?.Elements<W.NumberingInstance>().FirstOrDefault(value => value.NumberID?.Value == id);
        var definition = main.NumberingDefinitionsPart?.Numbering?.Elements<W.AbstractNum>()
            .FirstOrDefault(value => value.AbstractNumberId?.Value == instance?.AbstractNumId?.Val?.Value);
        W.Level? Level(int index) => instance?.Elements<W.LevelOverride>().FirstOrDefault(value => value.LevelIndex?.Value == index)?.Level
            ?? definition?.Elements<W.Level>().FirstOrDefault(value => value.LevelIndex?.Value == index);
        var level = Level(depth);
        if (level is null)
        {
            warnings.Add(new("MissingNumberingDefinition", "A paragraph's list definition could not be resolved."));
            return string.Empty;
        }
        var format = level.NumberingFormat?.Val?.Value;
        if (format == W.NumberFormatValues.None)
        {
            return string.Empty;
        }
        for (var deeper = depth + 1; deeper < 9; deeper++)
        {
            var restart = Level(deeper)?.LevelRestart?.Val?.Value;
            if (restart != 0 && (restart is null || restart == depth + 1))
            {
                _counters.Remove((id.Value, deeper));
            }
        }
        var start = instance?.Elements<W.LevelOverride>().FirstOrDefault(value => value.LevelIndex?.Value == depth)
            ?.StartOverrideNumberingValue?.Val?.Value ?? level.StartNumberingValue?.Val?.Value ?? 1;
        var key = (id.Value, depth);
        _counters[key] = _counters.TryGetValue(key, out var current) ? current + 1 : start;
        var marker = format == W.NumberFormatValues.Bullet ? "- " : _counters[key].ToString(CultureInfo.InvariantCulture) + ". ";
        // Use enough indentation for nested items even after a multi-digit parent marker.
        var indentation = 0;
        for (var parent = 0; parent < depth; parent++)
        {
            indentation += _counters.TryGetValue((id.Value, parent), out var number)
                ? Math.Max(4, number.ToString(CultureInfo.InvariantCulture).Length + 2) : 4;
        }
        return new string(' ', indentation) + marker;
    }
}

internal sealed class DocxInlineText
{
    private readonly StringBuilder _output = new();
    private readonly StringBuilder _segment = new();
    private bool _bold;

    internal void Append(string text, bool bold)
    {
        if (_bold != bold)
        {
            FlushSegment();
            _bold = bold;
        }
        _segment.Append(text);
    }

    internal string Take()
    {
        FlushSegment();
        var value = _output.ToString();
        _output.Clear();
        return value;
    }

    private void FlushSegment()
    {
        var text = _segment.ToString();
        _segment.Clear();
        if (!_bold || string.IsNullOrWhiteSpace(text))
        {
            _output.Append(text);
            return;
        }
        // Markdown strong delimiters cannot enclose leading/trailing whitespace.
        var start = text.Length - text.TrimStart().Length;
        var end = text.TrimEnd().Length;
        _output.Append(text.AsSpan(0, start)).Append("**")
            .Append(text.AsSpan(start, end - start)).Append("**").Append(text.AsSpan(end));
    }
}
