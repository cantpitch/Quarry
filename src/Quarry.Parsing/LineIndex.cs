namespace Quarry.Parsing;

/// <summary>Maps character offsets to zero-based line numbers.</summary>
public sealed class LineIndex
{
    private readonly List<int> _lineStarts = [0];

    public LineIndex(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                _lineStarts.Add(i + 1);
            else if (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n'))
                _lineStarts.Add(i + 1);
        }
    }

    public int LineCount => _lineStarts.Count;

    public int LineStart(int line) => _lineStarts[line];

    public int GetLine(int offset)
    {
        int index = _lineStarts.BinarySearch(offset);
        return index >= 0 ? index : ~index - 1;
    }

    public int GetColumn(int offset) => offset - _lineStarts[GetLine(offset)];
}
