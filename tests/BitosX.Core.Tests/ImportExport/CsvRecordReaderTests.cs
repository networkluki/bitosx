using System.Diagnostics;
using System.Text;
using BitosX.Core.ImportExport;

namespace BitosX.Core.Tests.ImportExport;

/// <summary>How a comma-separated file is taken apart into records and values (RFC 4180).</summary>
public sealed class CsvRecordReaderTests
{
    private static List<string[]> Records(string text, bool backslashEscapes = false)
    {
        var reader = new CsvRecordReader(text) { BackslashEscapes = backslashEscapes };
        var records = new List<string[]>();
        var values = new List<string>();
        while (reader.TryReadRecord(values))
        {
            records.Add([.. values]);
        }

        return records;
    }

    private static ImportRefusedException Refused(string text, bool backslashEscapes = false)
        => Assert.Throws<ImportRefusedException>(() => Records(text, backslashEscapes));

    [Fact]
    public void ValuesAreSeparatedByCommas_RecordsByLineEnds()
    {
        Assert.Equal(
            [["a", "b", "c"], ["1", "2", "3"]],
            Records("a,b,c\n1,2,3\n"));
    }

    [Theory]
    [InlineData("a,b\n1,2\n")]
    [InlineData("a,b\r\n1,2\r\n")]
    [InlineData("a,b\r1,2\r")]
    [InlineData("a,b\n1,2")]
    [InlineData("a,b\r\n1,2")]
    public void ALineEnd_IsCrLf_Lf_OrCr_AndTheLastRecordNeedsNone(string text)
    {
        Assert.Equal([["a", "b"], ["1", "2"]], Records(text));
    }

    [Fact]
    public void NothingIsTrimmed()
    {
        Assert.Equal([[" a ", "\tb", "c  "]], Records(" a ,\tb,c  "));
    }

    [Fact]
    public void EmptyValues_AreValues()
    {
        Assert.Equal([["", "", ""], ["a", "", "c"], ["", "b", ""]], Records(",,\na,,c\n,b,"));
    }

    [Fact]
    public void AnEmptyLine_IsARecordOfOneEmptyValue()
    {
        Assert.Equal([["a"], [""], ["b"]], Records("a\n\nb\n"));
        Assert.Equal([[""], [""]], Records("\n\n"));
        Assert.Empty(Records(""));
    }

    [Fact]
    public void AQuotedValue_MayHoldCommas_LineEnds_AndDoubledQuotes()
    {
        Assert.Equal(
            [["a,b", "line one\nline two", "say \"hi\""], ["next", "", ""]],
            Records("\"a,b\",\"line one\nline two\",\"say \"\"hi\"\"\"\nnext,\"\",\n"));
    }

    [Fact]
    public void LineEndsInsideAQuotedValue_AreKeptAsTheyAre()
    {
        Assert.Equal([["a\r\nb\rc\nd"]], Records("\"a\r\nb\rc\nd\""));
    }

    [Fact]
    public void AQuotedValue_KeepsItsSpaces_AndMayBeEmpty()
    {
        Assert.Equal([["  padded  ", "", "\"\""]], Records("\"  padded  \",\"\",\"\"\"\"\"\""));
    }

    [Fact]
    public void AQuoteThatIsNotTheFirstCharacter_IsAnOrdinaryCharacter()
    {
        // Only a value that starts with a quote is a quoted value.
        Assert.Equal([["it's 5\" long", " \"x\"", "a\"b"]], Records("it's 5\" long, \"x\",a\"b"));
    }

    [Fact]
    public void ABackslash_IsAnOrdinaryCharacter()
    {
        Assert.Equal([["C:\\dir\\file", "a\\\"b"]], Records("\"C:\\dir\\file\",a\\\"b"));
        Assert.Equal([["one\\"]], Records("\"one\\\""));
    }

    // ---- Damaged files ----

    [Fact]
    public void AQuotedValueThatIsNeverClosed_IsRefused_WithTheLineItStartsOn()
    {
        ImportRefusedException refused = Refused("name,password\nA,\"closed\"\nB,\"never closed\nC,x\nD,y\n");

        Assert.Equal(ImportRefusal.InvalidCsv, refused.Kind);
        Assert.Equal(3, refused.Line);
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("a,\"")]
    [InlineData("\"abc")]
    [InlineData("\"abc\"\"")]
    [InlineData("\"a\"\"\"\"b")]
    public void EveryWayOfNotClosingAQuote_IsRefused(string text)
    {
        ImportRefusedException refused = Refused(text);

        Assert.Equal(ImportRefusal.InvalidCsv, refused.Kind);
        Assert.Equal(1, refused.Line);
    }

    [Theory]
    [InlineData("\"a\"b,c", 1)]
    [InlineData("\"a\" ,c", 1)]
    [InlineData("x\n\"a\"b", 2)]
    [InlineData("x\r\n\"two\nlines\"junk", 3)]
    [InlineData("\"a\"\"b\"c\"", 1)]
    public void AnythingButACommaOrALineEndAfterAClosingQuote_IsRefused_WithItsLine(string text, int line)
    {
        ImportRefusedException refused = Refused(text);

        Assert.Equal(ImportRefusal.InvalidCsv, refused.Kind);
        Assert.Equal(line, refused.Line);
    }

    [Fact]
    public void LinesAreCounted_WithACrLfAsOne_AlsoInsideQuotedValues()
    {
        // Lines 1 to 3 are one record; line 4 is blank; the fault is on line 6.
        ImportRefusedException refused = Refused("\"a\r\nb\rc\"\r\n\r\nok\n\"bad\"x\n");

        Assert.Equal(6, refused.Line);
    }

    // ---- The KeePass 1.x layout ----

    [Fact]
    public void WithBackslashEscapes_ABackslashMakesTheNextCharacterPartOfTheValue()
    {
        Assert.Equal(
            [["say \"hi\"", "C:\\dir", "a\\\"b", "x"]],
            Records("\"say \\\"hi\\\"\",\"C:\\\\dir\",\"a\\\\\\\"b\",\"\\x\"", backslashEscapes: true));
    }

    [Fact]
    public void WithBackslashEscapes_AQuoteAlwaysClosesTheValue()
    {
        // Two quotes are an empty value, not one quote; and a doubled quote inside a value is a fault.
        Assert.Equal([["", "a"]], Records("\"\",\"a\"", backslashEscapes: true));
        Assert.Equal(ImportRefusal.InvalidCsv, Refused("\"a\"\"b\"", backslashEscapes: true).Kind);
    }

    [Fact]
    public void WithBackslashEscapes_ALineEndAfterABackslashIsStillALineEnd()
    {
        ImportRefusedException refused = Refused("\"a\\\nb\"\n\"c\"x", backslashEscapes: true);

        Assert.Equal(3, refused.Line);
        Assert.Equal([["a\nb"]], Records("\"a\\\nb\"", backslashEscapes: true));
        Assert.Equal([["a\r\nb"]], Records("\"a\\\r\nb\"", backslashEscapes: true));
    }

    [Fact]
    public void WithBackslashEscapes_ABackslashAtTheEnd_IsRefused()
    {
        Assert.Equal(ImportRefusal.InvalidCsv, Refused("\"abc\\", backslashEscapes: true).Kind);
        Assert.Equal(ImportRefusal.InvalidCsv, Refused("\"abc\\\"", backslashEscapes: true).Kind);
    }

    [Fact]
    public void WithBackslashEscapes_AValueThatIsNotQuoted_IsTakenAsItIs()
    {
        Assert.Equal([["a\\b", "c"]], Records("a\\b,c", backslashEscapes: true));
    }

    // ---- Cost ----

    [Fact]
    public void AHugeValue_IsReadInOnePass()
    {
        // 15 MiB in one quoted value, with a doubled quote every 64 characters, and as much again
        // in a value that is not quoted: seconds if read character by character into a new string.
        string chunk = new string('x', 62) + "\"\"";
        string quoted = string.Concat(Enumerable.Repeat(chunk, 15 * 1024 * 1024 / 64));
        string plain = new('y', 15 * 1024 * 1024);
        string text = "\"" + quoted + "\"," + plain + "\n";

        var watch = Stopwatch.StartNew();
        List<string[]> records = Records(text);
        watch.Stop();

        string[] record = Assert.Single(records);
        Assert.Equal(quoted.Length - (15 * 1024 * 1024 / 64), record[0].Length);
        Assert.Equal(plain.Length, record[1].Length);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Took {watch.Elapsed}.");
    }

    [Fact]
    public void ManyShortRecords_AndManyEmptyLines_AreReadInOnePass()
    {
        var text = new StringBuilder();
        for (int i = 0; i < 300_000; i++)
        {
            text.Append("a,b,\"c\"\n\n");
        }

        var watch = Stopwatch.StartNew();
        List<string[]> records = Records(text.ToString());
        watch.Stop();

        Assert.Equal(600_000, records.Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Took {watch.Elapsed}.");
    }

    [Fact]
    public void AMillionCommasOnOneLine_AreAMillionAndOneValues()
    {
        string[] record = Assert.Single(Records(new string(',', 1_000_000)));

        Assert.Equal(1_000_001, record.Length);
        Assert.All(record, value => Assert.Empty(value));
    }
}
