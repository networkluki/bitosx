using System.Text;
using BitosX.Core.ImportExport;
using static BitosX.Core.Tests.ImportExport.ImportTesting;

namespace BitosX.Core.Tests.ImportExport;

/// <summary>What a file is taken to be, from its content alone, and the limits that apply to every file.</summary>
public sealed class ImportParserTests
{
    private const string Csv = "name,password\nExample,FAKE-pw\n";
    private const string BitwardenJson = "{\"items\": [{\"type\": 1, \"name\": \"Example\", \"login\": {\"password\": \"FAKE-pw\"}}]}";
    private const string BitosXJson = "{\"format\": \"bitosx-export\", \"version\": 1, \"items\": [{\"type\": \"login\", \"name\": \"Example\", \"password\": \"FAKE-pw\"}]}";
    private const string KeePassXml = "<KeePassFile><Root><Group><Entry><String><Key>Title</Key><Value>Example</Value></String></Entry></Group></Root></KeePassFile>";

    [Theory]
    [InlineData(Csv, ImportFormat.Csv)]
    [InlineData(BitwardenJson, ImportFormat.BitwardenJson)]
    [InlineData(BitosXJson, ImportFormat.BitosXJson)]
    [InlineData(KeePassXml, ImportFormat.KeePassXml)]
    public void TheFormat_IsDetectedFromTheContent(string text, ImportFormat format)
    {
        ParsedImport parsed = Parse(text);

        Assert.Equal(format, parsed.Format);
        Assert.Equal("Example", Assert.Single(parsed.Entries).Name);
    }

    [Theory]
    [InlineData(Csv, ImportFormat.Csv)]
    [InlineData(BitwardenJson, ImportFormat.BitwardenJson)]
    [InlineData(BitosXJson, ImportFormat.BitosXJson)]
    [InlineData(KeePassXml, ImportFormat.KeePassXml)]
    public void AByteOrderMark_AndSpacesTabsAndLineEndsBeforeTheContent_ChangeNothing(string text, ImportFormat format)
    {
        byte[] file = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("\r\n \t\n" + text)];

        ParsedImport parsed = Parse(file);

        Assert.Equal(format, parsed.Format);
        Assert.Equal("Example", Assert.Single(parsed.Entries).Name);
    }

    [Theory]
    [InlineData("{", ImportRefusal.InvalidJson)]              // an opening brace is JSON, whatever follows
    [InlineData("{name,password}\n", ImportRefusal.InvalidJson)]
    [InlineData("[name],password\n", ImportRefusal.InvalidJson)]
    [InlineData("<,password\n", ImportRefusal.InvalidXml)]         // an opening angle bracket is XML
    [InlineData("<name>,password\n", ImportRefusal.NotRecognised)]
    [InlineData("\"{x}\",name,password\nx,Example,FAKE-pw\n", null)] // a quote first: a comma-separated file
    [InlineData("name,password\n{,[\n", null)]
    public void TheFirstCharacter_Decides(string text, ImportRefusal? refusal)
    {
        if (refusal is null)
        {
            Assert.Equal(ImportFormat.Csv, Parse(text).Format);
        }
        else
        {
            Assert.Equal(refusal, RefusalOf(text));
        }
    }

    [Fact]
    public void OnlySpacesTabsAndLineEnds_ArePassedOverBeforeTheFirstCharacter()
    {
        // A no-break space is content: this is a comma-separated file whose header is not known.
        Assert.Equal(ImportRefusal.NoRecognisedColumns, RefusalOf("\u00A0" + BitwardenJson));

        // So is a second byte order mark: only one is taken off the front.
        byte[] twice = [0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(BitwardenJson)];
        Assert.Equal(ImportRefusal.NoRecognisedColumns, RefusalOf(twice));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\r\n\r\n\t  \n")]
    public void AFileWithNothingInIt_IsEmpty(string text)
    {
        Assert.Equal(ImportRefusal.Empty, RefusalOf(text));
    }

    [Fact]
    public void AByteOrderMarkAndNothingElse_IsEmpty()
    {
        Assert.Equal(ImportRefusal.Empty, RefusalOf([0xEF, 0xBB, 0xBF]));
        Assert.Equal(ImportRefusal.Empty, RefusalOf([0xEF, 0xBB, 0xBF, (byte)'\n']));
    }

    [Fact]
    public void AFileOfSixteenMebibytes_IsRead_AndOneByteMoreIsRefused_BeforeItIsLookedAt()
    {
        byte[] header = Encoding.UTF8.GetBytes("name,password\nExample,FAKE-pw\n");
        byte[] largest = new byte[ImportLimits.MaxFileBytes];
        Array.Fill(largest, (byte)'\n');
        header.CopyTo(largest, 0);

        Assert.Equal("Example", Assert.Single(Parse(largest).Entries).Name);

        // One byte more, and not even valid UTF-8: the size is what it is refused for.
        byte[] tooLarge = new byte[ImportLimits.MaxFileBytes + 1];
        Array.Fill(tooLarge, (byte)0xFF);
        var refused = Assert.Throws<ImportRefusedException>(() => Parse(tooLarge));
        Assert.Equal(ImportRefusal.TooLarge, refused.Kind);
        Assert.Contains("16 MiB", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLimits_AreTheOnesBothClientsUse()
    {
        Assert.Equal(16_777_216, ImportLimits.MaxFileBytes);
        Assert.Equal(10_000, ImportLimits.MaxEntries);
        Assert.Equal(10_000, ImportLimits.MaxVaultItems);
        Assert.Equal(64, ImportLimits.MaxJsonDepth);
        Assert.Equal(64, ImportLimits.MaxGroupDepth);
        Assert.Equal(100, ImportLimits.MaxXmlDepth);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFE, (byte)'n', 0x00, (byte)'a', 0x00 }, ImportRefusal.UnsupportedEncoding)]
    [InlineData(new byte[] { 0xFE, 0xFF, 0x00, (byte)'n' }, ImportRefusal.UnsupportedEncoding)]
    [InlineData(new byte[] { (byte)'n', (byte)'a', 0xEF, 0xBF }, ImportRefusal.InvalidUtf8)]
    [InlineData(new byte[] { 0x03, 0xD9, 0xA2, 0x9A, 0x67, 0xFB, 0x4B, 0xB5 }, ImportRefusal.InvalidUtf8)] // how a .kdbx database starts
    public void AFileThatIsNotUtf8Text_IsRefusedAsSuch(byte[] file, ImportRefusal refusal)
    {
        Assert.Equal(refusal, RefusalOf(file));
    }

    [Fact]
    public void Utf16WithoutAByteOrderMark_IsNotRecognised_RatherThanRead()
    {
        // Every second byte is a NUL, which is valid UTF-8: the header just is not one that is known.
        Assert.Equal(ImportRefusal.NoRecognisedColumns, RefusalOf(Encoding.Unicode.GetBytes(Csv)));
    }
}
