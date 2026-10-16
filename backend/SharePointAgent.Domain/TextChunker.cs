namespace SharePointAgent.Domain;

public static class TextChunker
{
    public const string Version = "words-v1";

    public static IReadOnlyList<string> Split(string text, int size, int overlap)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        if (overlap < 0 || overlap >= size)
        {
            throw new ArgumentOutOfRangeException(nameof(overlap), "Overlap must be smaller than chunk size.");
        }

        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0)
        {
            return [];
        }

        var chunks = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var end = start + Math.Min(size, text.Length - start);
            if (end < text.Length && text[end] != ' ')
            {
                var boundary = text.LastIndexOf(' ', end - 1, end - start);
                // A single oversized word stays intact: size is a target, not a reason to cut a word.
                end = boundary > start ? boundary : text.IndexOf(' ', end);
                if (end < 0)
                {
                    end = text.Length;
                }
            }
            chunks.Add(text[start..end].TrimEnd());
            if (end == text.Length)
            {
                break;
            }

            var next = Math.Max(start + 1, end - overlap);
            // Round the overlap forward to a word start, preserving progress and never skipping new text.
            while (next < end && text[next - 1] != ' ')
            {
                next++;
            }
            while (next < text.Length && text[next] == ' ')
            {
                next++;
            }
            start = next;
        }
        return chunks;
    }
}
