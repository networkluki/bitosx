using System.Text;
using BitosX.Core.ImportExport;

namespace BitosX.Core.Tests.ImportExport;

/// <summary>
/// The text operations of the import specification. They are written out because the web vault has
/// to do exactly the same, and the runtime's own trimming and case folding differ from JavaScript's.
/// </summary>
public sealed class ImportTextTests
{
    // Every character "trimmed" removes: what .NET and JavaScript trim, taken together.
    private static readonly int[] Trimmed =
    [
        0x0009, 0x000A, 0x000B, 0x000C, 0x000D, 0x0020, 0x0085, 0x00A0, 0x1680,
        0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005, 0x2006, 0x2007, 0x2008, 0x2009, 0x200A,
        0x2028, 0x2029, 0x202F, 0x205F, 0x3000, 0xFEFF,
    ];

    [Fact]
    public void Trimmed_AreExactlyTheCharactersOfTheSpecification()
    {
        int[] actual = [.. Enumerable.Range(0, 0x10000).Where(c => ImportText.IsTrimmed((char)c))];

        Assert.Equal(Trimmed, actual);
    }

    [Fact]
    public void Trimmed_IncludeEverythingTheRuntimeCallsWhiteSpace()
    {
        // So that a name that is not empty after trimming is never one the vault calls blank.
        foreach (int c in Enumerable.Range(0, 0x10000).Where(c => char.IsWhiteSpace((char)c)))
        {
            Assert.True(ImportText.IsTrimmed((char)c), $"U+{c:X4}");
        }
    }

    [Fact]
    public void Trim_RemovesThemFromBothEnds_AndNowhereElse()
    {
        string all = string.Concat(Trimmed.Select(c => (char)c));

        Assert.Equal("a b", ImportText.Trim(all + "a b" + all));
        Assert.Equal("a" + all + "b", ImportText.Trim("a" + all + "b"));
        Assert.Equal(string.Empty, ImportText.Trim(all));
        Assert.Equal(string.Empty, ImportText.Trim(string.Empty));
        Assert.Same("untouched", ImportText.Trim("untouched"));
    }

    [Fact]
    public void Trim_LeavesWhatLooksEmptyButIsNotWhiteSpace()
    {
        // A zero width space and a word joiner are not white space to either runtime.
        Assert.Equal("\u200B", ImportText.Trim("\u200B"));
        Assert.Equal("\u2060x", ImportText.Trim(" \u2060x "));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("  \t\r\n", true)]
    [InlineData("\u00A0\uFEFF\u0085", true)]
    [InlineData(" a ", false)]
    [InlineData("\u200B", false)]
    public void IsBlank_IsWhetherNothingIsLeftAfterTrimming(string value, bool blank)
    {
        Assert.Equal(blank, ImportText.IsBlank(value));
        Assert.Equal(blank, ImportText.Trim(value).Length == 0);
    }

    [Fact]
    public void ToLowerAscii_ChangesAToZ_AndNothingElse()
    {
        Assert.Equal("login_uri", ImportText.ToLowerAscii("LOGIN_URI"));
        Assert.Equal("last modified", ImportText.ToLowerAscii("Last Modified"));
        Assert.Same("already", ImportText.ToLowerAscii("already"));

        // No language rules: these stay as they are, where the runtime's lower-casing would change them.
        Assert.Equal("\u00C4\u0130\u212A", ImportText.ToLowerAscii("\u00C4\u0130\u212A"));

        for (int c = 0; c < 0x10000; c++)
        {
            string lowered = ImportText.ToLowerAscii(((char)c).ToString());
            char expected = c is >= 'A' and <= 'Z' ? (char)(c + 32) : (char)c;
            Assert.Equal(expected, lowered[0]);
        }
    }

    [Theory]
    [InlineData("one", new[] { "one" })]
    [InlineData("", new[] { "" })]
    [InlineData("a\nb", new[] { "a", "b" })]
    [InlineData("a\r\nb", new[] { "a", "b" })]
    [InlineData("a\rb", new[] { "a", "b" })]
    [InlineData("a\n\rb", new[] { "a", "", "b" })]
    [InlineData("a\r\n\r\nb\n", new[] { "a", "", "b", "" })]
    public void SplitLines_SplitsAtEveryLineEnd_ACrLfBeingOne(string value, string[] lines)
    {
        Assert.Equal(lines, ImportText.SplitLines(value));
    }

    [Fact]
    public void SplitLines_DoesNotSplitAtTheUnicodeLineSeparators()
    {
        // Only CR and LF end a line, in both clients.
        Assert.Single(ImportText.SplitLines("a\u2028b\u2029c\u0085d"));
    }

    [Theory]
    [InlineData("plain", false)]
    [InlineData("tab\there", true)]
    [InlineData("nul\u0000", true)]
    [InlineData("unit\u001F", true)]
    [InlineData("del\u007F", true)]
    [InlineData("space and \u00A0 and \u0080", false)]
    public void HasControlCharacter_IsAboutTheAsciiOnes(string value, bool expected)
    {
        Assert.Equal(expected, ImportText.HasControlCharacter(value));
    }

    // ---- Decoding ----

    [Fact]
    public void Utf8_IsDecoded_WithOrWithoutAByteOrderMark()
    {
        byte[] plain = Encoding.UTF8.GetBytes("na\u00EFve \u65E5\u672C");
        byte[] marked = [0xEF, 0xBB, 0xBF, .. plain];

        Assert.Equal("na\u00EFve \u65E5\u672C", ImportText.Decode(plain, out ReadOnlyMemory<byte> first));
        Assert.Equal(plain, first.ToArray());

        Assert.Equal("na\u00EFve \u65E5\u672C", ImportText.Decode(marked, out ReadOnlyMemory<byte> second));
        Assert.Equal(plain, second.ToArray()); // the mark is not part of the text
    }

    [Fact]
    public void OnlyOneByteOrderMark_IsRemoved()
    {
        byte[] twice = [0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF, (byte)'x'];

        Assert.Equal("\uFEFFx", ImportText.Decode(twice, out _));
    }

    public static TheoryData<string, byte[]> OtherEncodings => new()
    {
        { "UTF-16 little endian", [0xFF, 0xFE, (byte)'a', 0x00] },
        { "UTF-16 big endian", [0xFE, 0xFF, 0x00, (byte)'a'] },
        { "UTF-32 little endian", [0xFF, 0xFE, 0x00, 0x00, (byte)'a', 0x00, 0x00, 0x00] },
        { "UTF-32 big endian", [0x00, 0x00, 0xFE, 0xFF, 0x00, 0x00, 0x00, (byte)'a'] },
        { "UTF-16 mark and nothing else", [0xFF, 0xFE] },
    };

    [Theory]
    [MemberData(nameof(OtherEncodings))]
    public void AUtf16OrUtf32ByteOrderMark_IsRefused_AsAnEncoding(string what, byte[] file)
    {
        var refused = Assert.Throws<ImportRefusedException>(() => ImportText.Decode(file, out _));

        Assert.True(refused.Kind == ImportRefusal.UnsupportedEncoding, what);
        Assert.Contains("UTF-16", refused.Message, StringComparison.Ordinal);
        Assert.Contains("UTF-8", refused.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, byte[]> NotUtf8 => new()
    {
        { "a lone continuation byte", [(byte)'a', 0x80, (byte)'b'] },
        { "a sequence cut short", [(byte)'a', 0xC3] },
        { "an overlong slash", [0xC0, 0xAF] },
        { "an overlong NUL", [0xC0, 0x80] },
        { "a surrogate half", [0xED, 0xA0, 0x80] },
        { "beyond U+10FFFF", [0xF4, 0x90, 0x80, 0x80] },
        { "Latin-1 text", [(byte)'c', (byte)'a', (byte)'f', 0xE9] },
        { "0xFF", [0xFF] },
        { "a ZIP file's first bytes", [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x08, 0x08, 0xA4, 0xB3] },
    };

    [Theory]
    [MemberData(nameof(NotUtf8))]
    public void BytesThatAreNotUtf8_AreRefused_NeverReplaced(string what, byte[] file)
    {
        var refused = Assert.Throws<ImportRefusedException>(() => ImportText.Decode(file, out _));

        Assert.True(refused.Kind == ImportRefusal.InvalidUtf8, what);
    }

    [Fact]
    public void EveryCodeAndText_IsDefined_AndNoTwoCodesAreTheSame()
    {
        string[] refusals = [.. Enum.GetValues<ImportRefusal>().Select(ImportTexts.Code)];
        string[] reasons = [.. Enum.GetValues<ImportReason>().Select(ImportTexts.Code)];
        string[] extras = [.. Enum.GetValues<ImportExtraKind>().Select(ImportTexts.Code)];
        string[] kinds = [.. Enum.GetValues<ImportUnsupportedKind>().Select(ImportTexts.Code)];
        string[] formats = [.. Enum.GetValues<ImportFormat>().Select(ImportTexts.Code)];
        string[] statuses = [.. Enum.GetValues<ImportStatus>().Select(ImportTexts.Code)];

        foreach (string[] codes in new[] { refusals, reasons, extras, kinds, formats, statuses })
        {
            Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
            Assert.All(codes, code => Assert.Matches("^[a-z][a-z0-9_]*$", code));
        }

        Assert.All(Enum.GetValues<ImportRefusal>(), kind => Assert.EndsWith(".", ImportTexts.Text(kind), StringComparison.Ordinal));
        Assert.All(Enum.GetValues<ImportReason>(), reason => Assert.EndsWith(".", ImportTexts.Text(reason), StringComparison.Ordinal));
        Assert.All(Enum.GetValues<ImportExtraKind>(), kind => Assert.NotEmpty(ImportTexts.Text(kind)));
        Assert.All(Enum.GetValues<ImportUnsupportedKind>(), kind => Assert.NotEmpty(ImportTexts.Text(kind)));
    }

    [Fact]
    public void ARefusal_SaysItsKind_ItsCode_AndItsFixedText()
    {
        var refused = new ImportRefusedException(ImportRefusal.InvalidCsv, line: 12);

        Assert.Equal(ImportRefusal.InvalidCsv, refused.Kind);
        Assert.Equal("invalid_csv", refused.Code);
        Assert.Equal(12, refused.Line);
        Assert.Equal(ImportTexts.Text(ImportRefusal.InvalidCsv), refused.Message);
        Assert.Null(new ImportRefusedException(ImportRefusal.Empty).Line);
    }
}
