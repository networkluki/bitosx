using System.Buffers;
using System.Text;

namespace BitosX.Core.ImportExport;

/// <summary>
/// Reads the records of a comma-separated file as RFC 4180 describes them, one at a time and in one
/// pass over the text.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Values are separated by commas, records by CR LF, LF or CR.</item>
/// <item>A value whose first character is a double quote runs to the closing quote and may hold
/// commas and line ends; two double quotes inside it stand for one. After the closing quote only a
/// comma, a line end or the end of the text may follow.</item>
/// <item>Any other value is taken as it is, up to the next comma or line end. Nothing is trimmed.</item>
/// <item>With <see cref="BackslashEscapes"/> (the KeePass 1.x layout) a backslash inside a quoted
/// value makes the character after it part of the value, and a double quote always closes it.</item>
/// </list>
/// A quoted value that is never closed, or that is followed by anything else, is a refusal
/// (<see cref="ImportRefusal.InvalidCsv"/>): guessing what was meant could turn one password into
/// another.
/// </remarks>
internal sealed class CsvRecordReader
{
    private static readonly SearchValues<char> UnquotedEnd = SearchValues.Create(",\r\n");
    private static readonly SearchValues<char> QuotedSpecial = SearchValues.Create("\"\r\n");
    private static readonly SearchValues<char> QuotedSpecialWithBackslash = SearchValues.Create("\"\r\n\\");

    private readonly string _text;
    private readonly StringBuilder _value = new();
    private int _position;
    private int _line = 1;

    public CsvRecordReader(string text)
    {
        _text = text;
    }

    /// <summary>Whether a backslash inside a quoted value escapes the character after it.</summary>
    public bool BackslashEscapes { get; set; }

    /// <summary>
    /// Reads the next record into <paramref name="values"/>, which is cleared first. Returns
    /// <see langword="false"/> at the end of the text. An empty line is a record of one empty value.
    /// </summary>
    public bool TryReadRecord(List<string> values)
    {
        values.Clear();
        if (_position >= _text.Length)
        {
            return false;
        }

        while (true)
        {
            values.Add(ReadValue());

            if (_position >= _text.Length)
            {
                return true;
            }

            char next = _text[_position];
            if (next == ',')
            {
                _position++;
                if (_position >= _text.Length)
                {
                    values.Add(string.Empty); // the text ends after a comma: one more, empty, value
                    return true;
                }

                continue;
            }

            ConsumeLineEnd();
            return true;
        }
    }

    private string ReadValue()
    {
        if (_text[_position] == '"')
        {
            return ReadQuotedValue();
        }

        int length = _text.AsSpan(_position).IndexOfAny(UnquotedEnd);
        int end = length < 0 ? _text.Length : _position + length;
        string value = _text[_position..end];
        _position = end;
        return value;
    }

    private string ReadQuotedValue()
    {
        int openedOnLine = _line;
        SearchValues<char> special = BackslashEscapes ? QuotedSpecialWithBackslash : QuotedSpecial;
        _value.Clear();
        _position++; // the opening quote

        while (true)
        {
            int length = _text.AsSpan(_position).IndexOfAny(special);
            if (length < 0)
            {
                throw new ImportRefusedException(ImportRefusal.InvalidCsv, openedOnLine); // never closed
            }

            _value.Append(_text, _position, length);
            _position += length;
            char c = _text[_position];

            if (c == '"')
            {
                if (!BackslashEscapes && _position + 1 < _text.Length && _text[_position + 1] == '"')
                {
                    _value.Append('"');
                    _position += 2;
                    continue;
                }

                _position++; // the closing quote
                break;
            }

            if (c == '\\')
            {
                if (_position + 1 >= _text.Length)
                {
                    throw new ImportRefusedException(ImportRefusal.InvalidCsv, openedOnLine);
                }

                // The character after the backslash, whatever it is; a line end is still a line end.
                _position++;
                if (_text[_position] is not ('\r' or '\n'))
                {
                    _value.Append(_text[_position]);
                    _position++;
                    continue;
                }

                c = _text[_position];
            }

            // A line end inside the value: kept as it is, and counted.
            if (c == '\r' && _position + 1 < _text.Length && _text[_position + 1] == '\n')
            {
                _value.Append("\r\n");
                _position += 2;
            }
            else
            {
                _value.Append(c);
                _position++;
            }

            _line++;
        }

        if (_position < _text.Length && _text[_position] is not (',' or '\r' or '\n'))
        {
            throw new ImportRefusedException(ImportRefusal.InvalidCsv, _line); // something after the closing quote
        }

        return _value.ToString();
    }

    private void ConsumeLineEnd()
    {
        if (_text[_position] == '\r' && _position + 1 < _text.Length && _text[_position + 1] == '\n')
        {
            _position += 2;
        }
        else
        {
            _position++;
        }

        _line++;
    }
}
