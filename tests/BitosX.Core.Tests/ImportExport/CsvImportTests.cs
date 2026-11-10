using System.Text;
using BitosX.Core.ImportExport;
using static BitosX.Core.Tests.ImportExport.ImportTesting;

namespace BitosX.Core.Tests.ImportExport;

/// <summary>
/// Comma-separated exports: which columns are read under which names, which exporters are
/// recognised by their header, and what happens to rows and files that are not what they should be.
/// </summary>
public sealed class CsvImportTests
{
    // ---- The exporters whose header is known ----

    [Fact]
    public void Chrome_IsReadAsItWritesIt()
    {
        ParsedImport parsed = Parse(
            "name,url,username,password,note\n" +
            "example.com,https://example.com/,fake.user@example.test,FAKE-chrome-pw-1,\n" +
            "\"Shop, the\",https://shop.example/login,shopper,\"FAKE,\"\"quoted\"\" pw\",\"line one\nline two\"\n" +
            "com.example.app,android://FakeHash==@com.example.app/,app-user,FAKE-app-pw,\n");

        Assert.Equal(ImportFormat.Csv, parsed.Format);
        Assert.Equal("Chrome or Edge (CSV)", parsed.Label);
        Assert.Empty(parsed.IgnoredColumns);
        Assert.Equal(3, parsed.RecordCount);
        Assert.Empty(parsed.Unsupported);

        Assert.Collection(
            parsed.Entries,
            first =>
            {
                Assert.Equal(1, first.Position);
                Assert.Equal("example.com", first.Name);
                Assert.Equal(["https://example.com/"], first.Addresses);
                Assert.Equal("fake.user@example.test", first.Username);
                Assert.Equal("FAKE-chrome-pw-1", first.Password);
                Assert.Equal(string.Empty, first.Notes);
                Assert.False(first.Favorite);
                Assert.Null(first.Problem);
            },
            second =>
            {
                Assert.Equal("Shop, the", second.Name);
                Assert.Equal("FAKE,\"quoted\" pw", second.Password);
                Assert.Equal("line one\nline two", second.Notes);
            },
            third => Assert.Equal(["android://FakeHash==@com.example.app/"], third.Addresses));
    }

    [Fact]
    public void Chrome_BeforeItHadNotes_IsRecognisedToo()
    {
        ParsedImport parsed = Parse("name,url,username,password\r\nexample.com,https://example.com/,u,FAKE-pw\r\n");

        Assert.Equal("Chrome or Edge (CSV)", parsed.Label);
        Assert.Equal("FAKE-pw", Assert.Single(parsed.Entries).Password);
    }

    [Fact]
    public void Firefox_IsReadAsItWritesIt()
    {
        ParsedImport parsed = Parse(
            "\"url\",\"username\",\"password\",\"httpRealm\",\"formActionOrigin\",\"guid\",\"timeCreated\",\"timeLastUsed\",\"timePasswordChanged\"\r\n" +
            "\"https://example.com\",\"fake.user\",\"FAKE-firefox \"\"pw\"\"\",,\"https://example.com\",\"{00000000-0000-4000-8000-000000000001}\",\"1700000000000\",\"1700000000001\",\"1700000000002\"\r\n" +
            "\"chrome://FirefoxAccounts\",\"fake.account@example.test\",\"FAKE-token\",\"Firefox Accounts credentials\",,\"{00000000-0000-4000-8000-000000000002}\",\"1\",\"2\",\"3\"");

        Assert.Equal("Firefox (CSV)", parsed.Label);
        Assert.Equal(
            ["httpRealm", "formActionOrigin", "guid", "timeCreated", "timeLastUsed", "timePasswordChanged"],
            parsed.IgnoredColumns);
        Assert.Collection(
            parsed.Entries,
            first =>
            {
                Assert.Equal(string.Empty, first.Name); // Firefox has no name: one is derived later
                Assert.Equal(["https://example.com"], first.Addresses);
                Assert.Equal("fake.user", first.Username);
                Assert.Equal("FAKE-firefox \"pw\"", first.Password);
            },
            second => Assert.Equal(["chrome://FirefoxAccounts"], second.Addresses));
    }

    [Fact]
    public void KeePassXc_IsReadAsItWritesIt()
    {
        ParsedImport parsed = Parse(
            "\"Group\",\"Title\",\"Username\",\"Password\",\"URL\",\"Notes\",\"TOTP\",\"Icon\",\"Last Modified\",\"Created\"\n" +
            "\"Root/Internet\",\"Example\",\"fake.user\",\"FAKE-kpxc-pw\",\"https://example.com\",\"a note\",\"otpauth://totp/Example:fake.user?secret=FAKEFAKEFAKEFAKE&period=30&digits=6&issuer=Example\",\"0\",\"2026-01-02T03:04:05Z\",\"2025-01-02T03:04:05Z\"\n");

        Assert.Equal("KeePassXC (CSV)", parsed.Label);
        Assert.Equal(["Icon", "Last Modified", "Created"], parsed.IgnoredColumns);
        ImportEntry entry = Assert.Single(parsed.Entries);
        Assert.Equal("Example", entry.Name);
        Assert.Equal("fake.user", entry.Username);
        Assert.Equal("FAKE-kpxc-pw", entry.Password);
        Assert.Equal(["https://example.com"], entry.Addresses);
        Assert.Equal("a note", entry.Notes);
        Assert.Equal("Root/Internet", entry.Folder);
        Assert.StartsWith("otpauth://totp/", entry.Totp, StringComparison.Ordinal);
    }

    [Fact]
    public void KeePassXc_BeforeItWroteTotp_IsRecognisedToo()
    {
        ParsedImport parsed = Parse(
            "\"Group\",\"Title\",\"Username\",\"Password\",\"URL\",\"Notes\"\n" +
            "\"Root\",\"Example\",\"fake.user\",\"FAKE-pw\",\"https://example.com\",\"\"\n");

        Assert.Equal("KeePassXC (CSV)", parsed.Label);
        Assert.Empty(parsed.IgnoredColumns);
    }

    public static TheoryData<string> BitwardenHeaders => new()
    {
        "folder,favorite,type,name,notes,fields,reprompt,archivedDate,login_uri,login_username,login_password,login_totp",
        "folder,favorite,type,name,notes,fields,reprompt,login_uri,login_username,login_password,login_totp",
        "collections,type,name,notes,fields,reprompt,archivedDate,login_uri,login_username,login_password,login_totp",
        "collections,type,name,notes,fields,reprompt,login_uri,login_username,login_password,login_totp",
    };

    [Theory]
    [MemberData(nameof(BitwardenHeaders))]
    public void Bitwarden_IsRecognised_WithEachHeaderItHasWritten(string header)
    {
        Assert.Equal("Bitwarden (CSV)", Parse(header + "\n").Label);
    }

    [Fact]
    public void Bitwarden_IsReadAsItWritesIt()
    {
        ParsedImport parsed = Parse(
            "folder,favorite,type,name,notes,fields,reprompt,archivedDate,login_uri,login_username,login_password,login_totp\r\n" +
            "Work,1,login,Example,a note,\"PIN: 0000\nRecovery: FAKE-recovery\",0,,\"https://example.com/login,https://example.org\",fake.user,FAKE-bw-pw,FAKEFAKEFAKEFAKE\r\n" +
            ",,note,A secure note,the text of the note,,0,,,,,\r\n" +
            ",,login,No folder,,,1,2026-01-02T03:04:05.000Z,https://example.net,,FAKE-other,\r\n");

        Assert.Equal("Bitwarden (CSV)", parsed.Label);
        Assert.Equal(["reprompt", "archivedDate"], parsed.IgnoredColumns);
        Assert.Equal(3, parsed.RecordCount);
        Assert.Equal(1, parsed.Unsupported[ImportUnsupportedKind.SecureNote]);

        Assert.Collection(
            parsed.Entries,
            first =>
            {
                Assert.Equal(1, first.Position);
                Assert.Equal("Example", first.Name);
                Assert.True(first.Favorite);
                Assert.Equal("Work", first.Folder);
                Assert.Equal("a note", first.Notes);
                Assert.Equal(["https://example.com/login", "https://example.org"], first.Addresses);
                Assert.Equal("fake.user", first.Username);
                Assert.Equal("FAKE-bw-pw", first.Password);
                Assert.Equal("FAKEFAKEFAKEFAKE", first.Totp);
                Assert.Collection(
                    first.CustomFields,
                    pin =>
                    {
                        Assert.Equal("PIN", pin.Name);
                        Assert.Equal("0000", pin.Value);
                    },
                    recovery =>
                    {
                        Assert.Equal("Recovery", recovery.Name);
                        Assert.Equal("FAKE-recovery", recovery.Value);
                    });
            },
            third =>
            {
                Assert.Equal(3, third.Position); // the note between them has number two
                Assert.Equal("No folder", third.Name);
                Assert.False(third.Favorite);
                Assert.Equal(string.Empty, third.Folder);
            });
    }

    [Fact]
    public void KeePass1_IsRead_WithItsOwnWayOfQuoting()
    {
        // KeePass writes \" for a quote and \\ for a backslash in this layout, and nothing doubled.
        ParsedImport parsed = Parse(
            "\"Account\",\"Login Name\",\"Password\",\"Web Site\",\"Comments\"\r\n" +
            "\"Example\",\"fake.user\",\"FAKE \\\"quoted\\\" pw\",\"https://example.com\",\"a note\"\r\n" +
            "\"Share\",\"DOMAIN\\\\fake.user\",\"FAKE\\\\pw\",\"\",\"line one\r\nline two\"\r\n");

        Assert.Equal("KeePass 1.x (CSV)", parsed.Label);
        Assert.Empty(parsed.IgnoredColumns);
        Assert.Collection(
            parsed.Entries,
            first =>
            {
                Assert.Equal("Example", first.Name);
                Assert.Equal("fake.user", first.Username);
                Assert.Equal("FAKE \"quoted\" pw", first.Password);
                Assert.Equal(["https://example.com"], first.Addresses);
                Assert.Equal("a note", first.Notes);
            },
            second =>
            {
                Assert.Equal("DOMAIN\\fake.user", second.Username);
                Assert.Equal("FAKE\\pw", second.Password); // one backslash, not the two in the file
                Assert.Equal("line one\r\nline two", second.Notes);
            });
    }

    [Fact]
    public void TheSameColumnNames_InAnotherOrder_AreAnOrdinaryCsvFile()
    {
        // Only KeePass's own header switches to its way of quoting.
        ParsedImport parsed = Parse(
            "\"Login Name\",\"Account\",\"Password\",\"Web Site\",\"Comments\"\n" +
            "\"fake.user\",\"Example\",\"FAKE\\\\pw\",\"https://example.com\",\"\"\n");

        Assert.Equal("CSV", parsed.Label);
        ImportEntry entry = Assert.Single(parsed.Entries);
        Assert.Equal("Example", entry.Name);
        Assert.Equal("fake.user", entry.Username);
        Assert.Equal("FAKE\\\\pw", entry.Password); // as written: a backslash means nothing in CSV
    }

    [Fact]
    public void AKnownHeader_WithOneMoreColumn_IsAnOrdinaryCsvFile_AndIsReadAllTheSame()
    {
        ParsedImport parsed = Parse("name,url,username,password,note,tags\nExample,https://example.com,u,FAKE-pw,n,work\n");

        Assert.Equal("CSV", parsed.Label);
        Assert.Equal(["tags"], parsed.IgnoredColumns);
        Assert.Equal("FAKE-pw", Assert.Single(parsed.Entries).Password);
    }

    // ---- The names a column may have ----

    [Theory]
    [InlineData("name")]
    [InlineData("title")]
    [InlineData("account")]
    public void TheName_IsReadFrom(string column)
    {
        Assert.Equal("Example", Assert.Single(Parse($"{column},password\nExample,FAKE-pw\n").Entries).Name);
    }

    [Theory]
    [InlineData("url")]
    [InlineData("urls")]
    [InlineData("login_uri")]
    [InlineData("uri")]
    [InlineData("website")]
    [InlineData("web site")]
    public void TheAddress_IsReadFrom(string column)
    {
        Assert.Equal(["https://example.com"], Assert.Single(Parse($"{column},password\nhttps://example.com,FAKE-pw\n").Entries).Addresses);
    }

    [Theory]
    [InlineData("username")]
    [InlineData("login_username")]
    [InlineData("user name")]
    [InlineData("login name")]
    public void TheUsername_IsReadFrom(string column)
    {
        Assert.Equal("fake.user", Assert.Single(Parse($"{column},password\nfake.user,FAKE-pw\n").Entries).Username);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("login_password")]
    public void ThePassword_IsReadFrom(string column)
    {
        Assert.Equal("FAKE-pw", Assert.Single(Parse($"name,{column}\nExample,FAKE-pw\n").Entries).Password);
    }

    [Theory]
    [InlineData("note")]
    [InlineData("notes")]
    [InlineData("comments")]
    [InlineData("extra")]
    public void TheNotes_AreReadFrom(string column)
    {
        Assert.Equal("a note", Assert.Single(Parse($"name,password,{column}\nExample,FAKE-pw,a note\n").Entries).Notes);
    }

    [Theory]
    [InlineData("totp")]
    [InlineData("login_totp")]
    [InlineData("otpauth")]
    public void TheAuthenticatorKey_IsReadFrom(string column)
    {
        Assert.Equal("FAKEFAKE", Assert.Single(Parse($"name,password,{column}\nExample,FAKE-pw,FAKEFAKE\n").Entries).Totp);
    }

    [Theory]
    [InlineData("folder")]
    [InlineData("group")]
    [InlineData("grouping")]
    public void TheFolder_IsReadFrom(string column)
    {
        Assert.Equal("Work", Assert.Single(Parse($"name,password,{column}\nExample,FAKE-pw,Work\n").Entries).Folder);
    }

    [Theory]
    [InlineData("favorite")]
    [InlineData("fav")]
    public void TheFavoriteMark_IsReadFrom(string column)
    {
        Assert.True(Assert.Single(Parse($"name,password,{column}\nExample,FAKE-pw,1\n").Entries).Favorite);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData(" 1 ", true)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("yes", false)]
    [InlineData("2", false)]
    [InlineData("x", false)]
    public void AFavorite_IsARowThatSays1OrTrue(string cell, bool favorite)
    {
        Assert.Equal(favorite, Assert.Single(Parse($"name,password,favorite\nExample,FAKE-pw,{cell}\n").Entries).Favorite);
    }

    [Fact]
    public void ColumnNames_AreCompared_Trimmed_AndWithoutRegardToAsciiCase()
    {
        ParsedImport parsed = Parse("  TITLE , Login_URI,USER NAME ,\"  PassWord\",  Comments\nExample,https://example.com,fake.user,FAKE-pw,a note\n");

        ImportEntry entry = Assert.Single(parsed.Entries);
        Assert.Equal("Example", entry.Name);
        Assert.Equal(["https://example.com"], entry.Addresses);
        Assert.Equal("fake.user", entry.Username);
        Assert.Equal("FAKE-pw", entry.Password);
        Assert.Equal("a note", entry.Notes);
        Assert.Empty(parsed.IgnoredColumns);
    }

    [Fact]
    public void OnlyAsciiCaseIsIgnored()
    {
        // U+212A is the Kelvin sign: it lower-cases to "k" under the rules of a language, but it is not K.
        Assert.Equal(ImportRefusal.NoRecognisedColumns, RefusalOf("name,PASSWORD\u0131\nExample,FAKE\n"));
        ParsedImport parsed = Parse("name,password,boo\u212Amarks\nExample,FAKE-pw,x\n");

        Assert.Equal(["boo\u212Amarks"], parsed.IgnoredColumns);
    }

    [Fact]
    public void OfSeveralNamesForOneField_TheFirstOfTheListIsRead_AndTheOthersAreIgnoredColumns()
    {
        ParsedImport parsed = Parse(
            "title,account,name,website,url,login_username,username,login_password,password,notes,note,fav,favorite\n" +
            "the title,the account,the name,https://website.example,https://url.example,login-user,user,FAKE-login-pw,FAKE-pw,the notes,the note,0,1\n");

        ImportEntry entry = Assert.Single(parsed.Entries);
        Assert.Equal("the name", entry.Name);
        Assert.Equal(["https://url.example"], entry.Addresses);
        Assert.Equal("user", entry.Username);
        Assert.Equal("FAKE-pw", entry.Password);
        Assert.Equal("the note", entry.Notes);
        Assert.True(entry.Favorite);
        Assert.Equal(["title", "account", "website", "login_username", "login_password", "notes", "fav"], parsed.IgnoredColumns);
    }

    [Fact]
    public void OfTwoColumnsWithOneName_TheLeftOneIsRead_AndTheOtherIsAnIgnoredColumn()
    {
        ParsedImport parsed = Parse("name,password,Password,url,URL\nExample,FAKE-left,FAKE-right,https://left.example,https://right.example\n");

        ImportEntry entry = Assert.Single(parsed.Entries);
        Assert.Equal("FAKE-left", entry.Password);
        Assert.Equal(["https://left.example"], entry.Addresses);
        Assert.Equal(["Password", "URL"], parsed.IgnoredColumns);
    }

    [Fact]
    public void ColumnsThatAreNotKnown_AreIgnored_AndNamedAsTheHeaderWritesThem()
    {
        ParsedImport parsed = Parse("Name,Password, Custom Column ,,  ,Tags\nExample,FAKE-pw,x,y,z,w\n");

        // A column without a name has nothing to be named by.
        Assert.Equal(["Custom Column", "Tags"], parsed.IgnoredColumns);
        Assert.Null(Assert.Single(parsed.Entries).Problem);
    }

    // ---- A file needs columns that are known ----

    [Theory]
    [InlineData("name,url,username\nExample,https://example.com,u\n")]       // no password column
    [InlineData("password\nFAKE-pw\n")]                                       // nothing to tell a login by
    [InlineData("password,notes,totp,folder\nFAKE-pw,n,t,f\n")]
    [InlineData("site,user,pass\nexample.com,u,FAKE-pw\n")]                   // names that are not known
    [InlineData("name;url;username;password\nExample;https://example.com;u;FAKE-pw\n")] // not commas
    [InlineData("name\turl\tusername\tpassword\nExample\thttps://example.com\tu\tFAKE-pw\n")]
    [InlineData("Dear reader,\nthis is a letter, not an export.\n")]
    [InlineData(",,,\n,,\n")]
    public void AFileWithoutAPasswordColumnAndOneToTellALoginBy_IsRefused(string text)
    {
        Assert.Equal(ImportRefusal.NoRecognisedColumns, RefusalOf(text));
    }

    [Theory]
    [InlineData("name,password\n")]
    [InlineData("url,password")]
    [InlineData("username,password\n\n\n")]
    public void AHeaderAndNoRows_IsAFileWithNoEntries(string text)
    {
        ParsedImport parsed = Parse(text);

        Assert.Empty(parsed.Entries);
        Assert.Equal(0, parsed.RecordCount);
    }

    [Fact]
    public void TheHeader_IsTheFirstLineThatIsNotEmpty()
    {
        ParsedImport parsed = Parse("\n\r\n,,\n  \t\n , \nname,password\n\nExample,FAKE-pw\n\n");

        Assert.Equal("Example", Assert.Single(parsed.Entries).Name);
    }

    // ---- Rows ----

    [Fact]
    public void UsernameAndPassword_AreTakenExactlyAsTheyAre()
    {
        ParsedImport parsed = Parse("name,username,password\nExample,  spaced user  ,\"  FAKE pw with spaces  \"\n");

        ImportEntry entry = Assert.Single(parsed.Entries);
        Assert.Equal("  spaced user  ", entry.Username);
        Assert.Equal("  FAKE pw with spaces  ", entry.Password);
    }

    [Fact]
    public void EmptyLines_AndRowsOfNothingButSpace_AreNotRows()
    {
        ParsedImport parsed = Parse("name,password\n\nOne,FAKE-1\n,\n\r\n\"\",\"\"\n   \n \t , \u00A0,\"  \"\nTwo,FAKE-2\n,,,,\n");

        Assert.Equal(["One", "Two"], parsed.Entries.Select(e => e.Name));
        Assert.Equal([1, 2], parsed.Entries.Select(e => e.Position));
        Assert.Equal(2, parsed.RecordCount);
    }

    [Fact]
    public void ARowThatIsShort_HasEmptyValuesWhereItEnds()
    {
        ParsedImport parsed = Parse("name,url,username,password,note\nExample,https://example.com\n");

        ImportEntry entry = Assert.Single(parsed.Entries);
        Assert.Equal("Example", entry.Name);
        Assert.Equal(string.Empty, entry.Password);
        Assert.Null(entry.Problem);
    }

    [Fact]
    public void ARowWithAValueBeyondTheLastColumn_IsMarked_BecauseItsValuesMayHaveMoved()
    {
        // The comma in the password was not quoted: "FAKE" is all that is left of it.
        ParsedImport parsed = Parse("name,url,username,password\nExample,https://example.com,u,FAKE,pw\nFine,https://example.org,u,FAKE-pw\n");

        Assert.Equal(ImportReason.RowHasExtraValues, parsed.Entries[0].Problem);
        Assert.Null(parsed.Entries[1].Problem);
    }

    [Fact]
    public void EmptyValuesBeyondTheLastColumn_AreNothing()
    {
        ParsedImport parsed = Parse("name,password\nExample,FAKE-pw,,,\n");

        Assert.Null(Assert.Single(parsed.Entries).Problem);
    }

    [Theory]
    [InlineData("note")]
    [InlineData("NOTE")]
    [InlineData(" Note ")]
    public void ARowOfTypeNote_IsASecureNote_AndNotALogin(string type)
    {
        ParsedImport parsed = Parse($"type,name,password\n{type},A note,\nlogin,A login,FAKE-pw\n");

        Assert.Equal("A login", Assert.Single(parsed.Entries).Name);
        Assert.Equal(1, parsed.Unsupported[ImportUnsupportedKind.SecureNote]);
        Assert.Equal(2, parsed.RecordCount);
    }

    [Theory]
    [InlineData("login")]
    [InlineData("")]
    [InlineData("password")]
    [InlineData("card")]
    [InlineData("anything else")]
    public void ARowOfAnyOtherType_IsALogin(string type)
    {
        // "note" is the one value known to mean something else; what another exporter writes here is not known.
        ParsedImport parsed = Parse($"type,name,password\n{type},Example,FAKE-pw\n");

        Assert.Single(parsed.Entries);
        Assert.Empty(parsed.Unsupported);
    }

    [Theory]
    [InlineData("login_uri")]
    [InlineData("uri")]
    public void BitwardensAddressCell_IsSplitAtItsCommas(string column)
    {
        ParsedImport parsed = Parse($"name,{column},password\nExample,\"https://a.example, https://b.example,,example.org\",FAKE-pw\n");

        // Trimming and dropping the empty one happen when the entry is normalised.
        Assert.Equal(["https://a.example", " https://b.example", "", "example.org"], Assert.Single(parsed.Entries).Addresses);
    }

    [Theory]
    [InlineData("url")]
    [InlineData("urls")]
    [InlineData("website")]
    [InlineData("web site")]
    public void AnyOtherAddressCell_IsNotSplitAtCommas(string column)
    {
        ParsedImport parsed = Parse($"name,{column},password\nExample,\"https://example.com/?ids=1,2,3\",FAKE-pw\n");

        Assert.Equal(["https://example.com/?ids=1,2,3"], Assert.Single(parsed.Entries).Addresses);
    }

    [Fact]
    public void CustomFields_AreOnePerLine_SplitAtTheLastColonAndSpace()
    {
        ParsedImport parsed = Parse(
            "name,password,fields\n" +
            "Example,FAKE-pw,\"PIN: 0000\r\nURL note: see: https://example.com\n\n  \nno separator here\n Spaced name : value \nEmpty: \n: nameless\"\n");

        Assert.Equal(
            [("PIN", "0000"), ("URL note: see", "https://example.com"), ("", "no separator here"), ("Spaced name", "value "), ("Empty", ""), ("", "nameless")],
            Assert.Single(parsed.Entries).CustomFields.Select(f => (f.Name, f.Value)));
    }

    // ---- Limits ----

    [Fact]
    public void TenThousandRows_AreRead()
    {
        var text = new StringBuilder("name,url,username,password\n");
        for (int i = 1; i <= 10_000; i++)
        {
            text.Append("Example ").Append(i).Append(",https://example.com/,fake.user,FAKE-pw-").Append(i).Append('\n');
        }

        ParsedImport parsed = Parse(text.ToString());

        Assert.Equal(10_000, parsed.Entries.Count);
        Assert.Equal(10_000, parsed.Entries[^1].Position);
    }

    [Fact]
    public void TenThousandAndOneRows_AreRefused()
    {
        var text = new StringBuilder("name,url,username,password\n");
        for (int i = 1; i <= 10_001; i++)
        {
            text.Append("Example ").Append(i).Append(",https://example.com/,fake.user,FAKE-pw-").Append(i).Append('\n');
        }

        Assert.Equal(ImportRefusal.TooManyEntries, RefusalOf(text.ToString()));
    }

    [Fact]
    public void RowsThatAreNotLogins_CountTowardsTheLimit_AndEmptyLinesDoNot()
    {
        var text = new StringBuilder("type,name,password\n");
        for (int i = 1; i <= 10_000; i++)
        {
            text.Append(i % 2 == 0 ? "note" : "login").Append(",Example,FAKE-pw\n\n\n");
        }

        ParsedImport parsed = Parse(text.ToString());
        Assert.Equal(5_000, parsed.Entries.Count);
        Assert.Equal(10_000, parsed.RecordCount);

        text.Append("note,One more,\n");
        Assert.Equal(ImportRefusal.TooManyEntries, RefusalOf(text.ToString()));
    }

    [Fact]
    public void ADamagedFile_IsRefused_WithTheLine()
    {
        var refused = Assert.Throws<ImportRefusedException>(
            () => Parse("name,password\nOne,FAKE-1\nTwo,\"FAKE never closed\nThree,FAKE-3\n"));

        Assert.Equal(ImportRefusal.InvalidCsv, refused.Kind);
        Assert.Equal(3, refused.Line);
    }

    [Fact]
    public void AHeaderWithAMillionColumns_AndRowsToMatch_AreReadInOnePass()
    {
        // Each row is looked at for the columns that are read, not for every column of the header.
        string header = "name,password" + string.Concat(Enumerable.Repeat(",x", 500_000));
        string row = "Example,FAKE-pw" + new string(',', 500_000);
        string text = header + "\n" + string.Concat(Enumerable.Repeat(row + "\n", 20));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        ParsedImport parsed = Parse(text);
        watch.Stop();

        Assert.Equal(20, parsed.Entries.Count);
        Assert.Equal(500_000, parsed.IgnoredColumns.Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Took {watch.Elapsed}.");
    }
}
