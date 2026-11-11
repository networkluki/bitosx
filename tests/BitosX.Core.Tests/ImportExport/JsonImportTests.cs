using System.Text;
using BitosX.Core.ImportExport;
using static BitosX.Core.Tests.ImportExport.ImportTesting;

namespace BitosX.Core.Tests.ImportExport;

/// <summary>
/// The two JSON formats: Bitwarden's unencrypted export and BitosX's own. What is read, what is
/// counted and left out, and what is refused.
/// </summary>
public sealed class JsonImportTests
{
    // An export as Bitwarden writes it (two spaces, the members in its order), with made-up content.
    private const string BitwardenExport = """
        {
          "encrypted": false,
          "folders": [
            {
              "id": "00000000-0000-4000-8000-0000000000f1",
              "name": "Work"
            },
            {
              "id": "00000000-0000-4000-8000-0000000000f2",
              "name": "Private/Shops"
            }
          ],
          "items": [
            {
              "passwordHistory": [
                {
                  "lastUsedDate": "2026-01-02T03:04:05.000Z",
                  "password": "FAKE-older-pw"
                }
              ],
              "revisionDate": "2026-02-03T04:05:06.000Z",
              "creationDate": "2025-01-02T03:04:05.000Z",
              "deletedDate": null,
              "archivedDate": null,
              "id": "00000000-0000-4000-8000-000000000001",
              "organizationId": null,
              "folderId": "00000000-0000-4000-8000-0000000000f1",
              "type": 1,
              "reprompt": 0,
              "name": "Example",
              "notes": "first line\nsecond line",
              "favorite": true,
              "fields": [
                {
                  "name": "PIN",
                  "value": "0000",
                  "type": 1,
                  "linkedId": null
                },
                {
                  "name": "Newsletter",
                  "value": "true",
                  "type": 2,
                  "linkedId": null
                },
                {
                  "name": "Linked",
                  "value": null,
                  "type": 3,
                  "linkedId": 100
                }
              ],
              "login": {
                "uris": [
                  {
                    "match": null,
                    "uri": "https://example.com/login"
                  },
                  {
                    "match": 0,
                    "uri": "androidapp://com.example.app"
                  },
                  {
                    "match": null,
                    "uri": null
                  }
                ],
                "fido2Credentials": [],
                "username": "fake.user@example.test",
                "password": "FAKE-bw-json-pw",
                "totp": "otpauth://totp/Example:fake.user?secret=FAKEFAKEFAKEFAKE"
              },
              "collectionIds": null
            },
            {
              "id": "00000000-0000-4000-8000-000000000002",
              "folderId": null,
              "type": 2,
              "name": "A secure note",
              "notes": "the note",
              "favorite": false,
              "secureNote": {
                "type": 0
              },
              "collectionIds": null
            },
            {
              "id": "00000000-0000-4000-8000-000000000003",
              "folderId": null,
              "type": 3,
              "name": "A card",
              "favorite": false,
              "card": {
                "cardholderName": "Fake Person",
                "brand": "Visa",
                "number": "4111111111111111",
                "expMonth": "1",
                "expYear": "2030",
                "code": "000"
              },
              "collectionIds": null
            },
            {
              "id": "00000000-0000-4000-8000-000000000004",
              "folderId": "00000000-0000-4000-8000-0000000000f2",
              "type": 1,
              "name": "Bare login",
              "notes": null,
              "favorite": false,
              "login": {
                "uris": [],
                "username": null,
                "password": "FAKE-second-pw",
                "totp": null
              },
              "collectionIds": null
            }
          ]
        }
        """;

    // A backslash and a "u": what starts an escape in JSON. Written this way so that the escapes in
    // these tests are JSON's, not the compiler's.
    private const string B = "\\";
    private const string U = B + "u";

    private static string Bitwarden(string items, string before = "\"encrypted\": false,")
        => "{" + before + "\"folders\": [], \"items\": [" + items + "]}";

    private static string Login(string members) => "{\"type\": 1, \"name\": \"Example\", " + members + "}";

    // ---- Bitwarden ----

    [Fact]
    public void Bitwarden_IsReadAsItWritesIt()
    {
        ParsedImport parsed = Parse(BitwardenExport);

        Assert.Equal(ImportFormat.BitwardenJson, parsed.Format);
        Assert.Equal("Bitwarden (JSON)", parsed.Label);
        Assert.Equal(4, parsed.RecordCount);
        Assert.Equal(0, parsed.InTrash);
        Assert.Empty(parsed.IgnoredColumns);
        Assert.Equal(1, parsed.Unsupported[ImportUnsupportedKind.SecureNote]);
        Assert.Equal(1, parsed.Unsupported[ImportUnsupportedKind.Card]);
        Assert.Equal(2, parsed.Unsupported.Count);

        Assert.Collection(
            parsed.Entries,
            first =>
            {
                Assert.Equal(1, first.Position);
                Assert.Equal("Example", first.Name);
                Assert.Equal("fake.user@example.test", first.Username);
                Assert.Equal("FAKE-bw-json-pw", first.Password);
                Assert.Equal("first line\nsecond line", first.Notes);
                Assert.True(first.Favorite);
                Assert.Equal(["https://example.com/login", "androidapp://com.example.app"], first.Addresses);
                Assert.Equal("otpauth://totp/Example:fake.user?secret=FAKEFAKEFAKEFAKE", first.Totp);
                Assert.Equal("Work", first.Folder);
                Assert.Equal(
                    [("PIN", "0000"), ("Newsletter", "true"), ("Linked", "")],
                    first.CustomFields.Select(f => (f.Name, f.Value)));
                Assert.Null(first.Problem);
            },
            second =>
            {
                Assert.Equal(4, second.Position);
                Assert.Equal("Bare login", second.Name);
                Assert.Equal(string.Empty, second.Username);
                Assert.Equal("FAKE-second-pw", second.Password);
                Assert.Equal(string.Empty, second.Notes);
                Assert.Empty(second.Addresses);
                Assert.Equal(string.Empty, second.Totp);
                Assert.Equal("Private/Shops", second.Folder);
                Assert.Empty(second.CustomFields);
            });
    }

    [Theory]
    [InlineData(2, ImportUnsupportedKind.SecureNote)]
    [InlineData(3, ImportUnsupportedKind.Card)]
    [InlineData(4, ImportUnsupportedKind.Identity)]
    [InlineData(5, ImportUnsupportedKind.SshKey)]
    [InlineData(6, ImportUnsupportedKind.BankAccount)]
    [InlineData(7, ImportUnsupportedKind.DriversLicense)]
    [InlineData(8, ImportUnsupportedKind.Passport)]
    [InlineData(9, ImportUnsupportedKind.Other)]
    [InlineData(0, ImportUnsupportedKind.Other)]
    [InlineData(-1, ImportUnsupportedKind.Other)]
    public void Bitwarden_ItemsThatAreNotLogins_AreCountedByKind(int type, ImportUnsupportedKind kind)
    {
        ParsedImport parsed = Parse(Bitwarden($"{{\"type\": {type}, \"name\": \"x\"}}, {{\"type\": {type}, \"name\": \"y\"}}"));

        Assert.Empty(parsed.Entries);
        Assert.Equal(2, parsed.RecordCount);
        Assert.Equal(2, Assert.Single(parsed.Unsupported, pair => pair.Key == kind).Value);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1e0")]
    [InlineData("10e-1")]
    public void Bitwarden_ALoginIsTheNumberOne_HoweverItIsWritten(string type)
    {
        Assert.Single(Parse(Bitwarden($"{{\"type\": {type}, \"name\": \"x\"}}")).Entries);
    }

    [Theory]
    [InlineData("\"type\": \"1\", \"name\": \"x\"")]
    [InlineData("\"type\": null, \"name\": \"x\"")]
    [InlineData("\"type\": true, \"name\": \"x\"")]
    [InlineData("\"type\": 1.5, \"name\": \"x\"")]
    [InlineData("\"type\": 1e400, \"name\": \"x\"")]
    [InlineData("\"name\": \"no type at all\"")]
    public void Bitwarden_AnItemWhoseTypeIsNotOneOfItsNumbers_IsCountedAsOther(string members)
    {
        ParsedImport parsed = Parse(Bitwarden("{" + members + "}"));

        Assert.Empty(parsed.Entries);
        Assert.Equal(1, parsed.Unsupported[ImportUnsupportedKind.Other]);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"a string\"")]
    [InlineData("[]")]
    [InlineData("true")]
    public void Bitwarden_AnElementThatIsNotAnObject_IsCountedAsOther(string element)
    {
        ParsedImport parsed = Parse(Bitwarden(element + ", " + Login("\"login\": {\"password\": \"FAKE-pw\"}")));

        ImportEntry entry = Assert.Single(parsed.Entries);
        Assert.Equal(2, entry.Position);
        Assert.Equal(1, parsed.Unsupported[ImportUnsupportedKind.Other]);
        Assert.Equal(2, parsed.RecordCount);
    }

    [Theory]
    [InlineData("\"2026-01-02T03:04:05.000Z\"")]
    [InlineData("\"\"")]
    [InlineData("0")]
    [InlineData("false")]
    public void Bitwarden_AnItemInTheTrash_IsCountedAndLeftOut_WhateverItIs(string deletedDate)
    {
        ParsedImport parsed = Parse(Bitwarden(
            $"{{\"type\": 1, \"name\": \"Deleted login\", \"deletedDate\": {deletedDate}, \"login\": {{\"password\": \"FAKE-pw\"}}}}, " +
            $"{{\"type\": 3, \"name\": \"Deleted card\", \"deletedDate\": {deletedDate}}}, " +
            "{\"type\": 1, \"name\": \"Kept\", \"deletedDate\": null}"));

        Assert.Equal("Kept", Assert.Single(parsed.Entries).Name);
        Assert.Equal(2, parsed.InTrash);
        Assert.Empty(parsed.Unsupported);
        Assert.Equal(3, parsed.RecordCount);
    }

    [Fact]
    public void Bitwarden_AnArchivedItem_IsStillALogin()
    {
        ParsedImport parsed = Parse(Bitwarden(Login("\"archivedDate\": \"2026-01-02T03:04:05.000Z\", \"login\": {\"password\": \"FAKE-pw\"}")));

        Assert.Single(parsed.Entries);
    }

    [Fact]
    public void Bitwarden_AMemberThatIsAbsent_Null_OrOfAnotherType_CountsAsAbsent()
    {
        ParsedImport parsed = Parse(Bitwarden(
            "{\"type\": 1}, " +
            "{\"type\": 1, \"name\": null, \"notes\": null, \"favorite\": null, \"fields\": null, \"login\": null, \"folderId\": null}, " +
            "{\"type\": 1, \"name\": 5, \"notes\": [\"x\"], \"favorite\": \"true\", \"fields\": {\"name\": \"x\"}, \"login\": \"x\", \"folderId\": 7}, " +
            "{\"type\": 1, \"name\": \"Odd login\", \"favorite\": 1, \"login\": {\"username\": 12, \"password\": {\"x\": 1}, \"totp\": false, \"uris\": \"https://example.com\"}}, " +
            "{\"type\": 1, \"name\": \"Odd lists\", \"login\": {\"uris\": [\"https://a.example\", 3, null, {\"uri\": 4}, {\"other\": \"x\"}, {\"uri\": \"https://kept.example\"}]}, " +
            "\"fields\": [\"x\", null, {\"name\": 1, \"value\": 2}, {\"name\": \" Kept \", \"value\": \" as it is \"}]}"));

        Assert.Equal(5, parsed.Entries.Count);
        foreach (ImportEntry entry in parsed.Entries.Take(4))
        {
            Assert.Equal(string.Empty, entry.Username);
            Assert.Equal(string.Empty, entry.Password);
            Assert.Equal(string.Empty, entry.Notes);
            Assert.Equal(string.Empty, entry.Totp);
            Assert.Equal(string.Empty, entry.Folder);
            Assert.False(entry.Favorite);
            Assert.Empty(entry.Addresses);
            Assert.Empty(entry.CustomFields);
        }

        Assert.Equal(["", "", "", "Odd login", "Odd lists"], parsed.Entries.Select(e => e.Name));
        Assert.Equal(["https://kept.example"], parsed.Entries[4].Addresses);
        Assert.Equal([("", ""), ("Kept", " as it is ")], parsed.Entries[4].CustomFields.Select(f => (f.Name, f.Value)));
    }

    [Fact]
    public void Bitwarden_AFolder_IsFoundByItsId_AndOfTwoWithOneIdTheFirstCounts()
    {
        ParsedImport parsed = Parse(
            "{\"folders\": [{\"id\": \"f1\", \"name\": \"First\"}, {\"id\": \"f1\", \"name\": \"Second\"}, {\"id\": \"\", \"name\": \"No id\"}, " +
            "{\"name\": \"No id either\"}, \"x\", {\"id\": \"f2\", \"name\": 5}], " +
            "\"items\": [" +
            "{\"type\": 1, \"name\": \"a\", \"folderId\": \"f1\"}, {\"type\": 1, \"name\": \"b\", \"folderId\": \"F1\"}, " +
            "{\"type\": 1, \"name\": \"c\", \"folderId\": \"f2\"}, {\"type\": 1, \"name\": \"d\", \"folderId\": \"\"}, {\"type\": 1, \"name\": \"e\", \"folderId\": \"nowhere\"}]}");

        Assert.Equal(["First", "", "", "", ""], parsed.Entries.Select(e => e.Folder));
    }

    [Fact]
    public void Bitwarden_WithoutSayingWhetherItIsEncrypted_OrWithoutFolders_IsRead()
    {
        ParsedImport parsed = Parse("{\"items\": [" + Login("\"login\": {\"password\": \"FAKE-pw\"}") + "]}");

        Assert.Equal(ImportFormat.BitwardenJson, parsed.Format);
        Assert.Equal("FAKE-pw", Assert.Single(parsed.Entries).Password);
    }

    [Fact]
    public void Bitwarden_AnOrganisationExport_IsRead_WithoutItsCollections()
    {
        ParsedImport parsed = Parse(
            "{\"encrypted\": false, \"collections\": [{\"id\": \"c1\", \"organizationId\": \"o1\", \"name\": \"Shared\"}], " +
            "\"items\": [{\"type\": 1, \"name\": \"Shared login\", \"collectionIds\": [\"c1\"], \"login\": {\"password\": \"FAKE-pw\"}}]}");

        ImportEntry entry = Assert.Single(parsed.Entries);
        Assert.Equal("Shared login", entry.Name);
        Assert.Equal(string.Empty, entry.Folder);
    }

    public static TheoryData<string, string> EncryptedExports => new()
    {
        {
            "encrypted with the account's key",
            """
            {
              "encrypted": true,
              "encKeyValidation_DO_NOT_EDIT": "2.FAKEFAKEFAKEFAKEFAKEFA==|FAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFA==|FAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAE=",
              "folders": [],
              "items": [
                {
                  "id": "00000000-0000-4000-8000-000000000001",
                  "type": 1,
                  "name": "2.FAKEFAKEFAKEFAKEFAKEFA==|FAKEFAKEFAKEFAKEFAKEFA==|FAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAE=",
                  "login": {
                    "password": "2.FAKEFAKEFAKEFAKEFAKEFA==|FAKEFAKEFAKEFAKEFAKEFA==|FAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAE="
                  }
                }
              ]
            }
            """
        },
        {
            "protected by a password",
            """
            {
              "encrypted": true,
              "passwordProtected": true,
              "salt": "FAKEFAKEFAKEFAKEFAKEFA==",
              "kdfType": 0,
              "kdfIterations": 600000,
              "encKeyValidation_DO_NOT_EDIT": "2.FAKEFAKEFAKEFAKEFAKEFA==|FAKEFAKEFAKEFAKEFAKEFA==|FAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAE=",
              "data": "2.FAKEFAKEFAKEFAKEFAKEFA==|FAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFA==|FAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAE="
            }
            """
        },
        { "encrypted, and nothing else", "{\"encrypted\": true}" },
    };

    [Theory]
    [MemberData(nameof(EncryptedExports))]
    public void Bitwarden_AnEncryptedExport_IsRefused_AndSaysToExportUnencrypted(string what, string export)
    {
        var refused = Assert.Throws<ImportRefusedException>(() => Parse(export));

        Assert.True(refused.Kind == ImportRefusal.EncryptedExport, what);
        Assert.Contains("unencrypted", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("false")]
    public void Bitwarden_OnlyTheValueTrue_MeansEncrypted(string encrypted)
    {
        ParsedImport parsed = Parse(Bitwarden(Login("\"login\": {\"password\": \"FAKE-pw\"}"), $"\"encrypted\": {encrypted},"));

        Assert.Single(parsed.Entries);
    }

    [Fact]
    public void Bitwarden_TenThousandItems_AreRead_AndOneMoreIsRefused()
    {
        string item = Login("\"login\": {\"password\": \"FAKE-pw\"}");
        string tenThousand = string.Join(',', Enumerable.Repeat(item, 10_000));

        Assert.Equal(10_000, Parse(Bitwarden(tenThousand)).Entries.Count);
        Assert.Equal(ImportRefusal.TooManyEntries, RefusalOf(Bitwarden(tenThousand + "," + item)));

        // Items of other kinds, and items in the trash, count as well.
        string mixed = string.Join(',', Enumerable.Repeat("{\"type\": 3}, {\"type\": 1, \"deletedDate\": \"x\"}", 5_000));
        Assert.Empty(Parse(Bitwarden(mixed)).Entries);
        Assert.Equal(ImportRefusal.TooManyEntries, RefusalOf(Bitwarden(mixed + ",null")));
    }

    // ---- BitosX ----

    private const string BitosXExport = """
        {
          "format": "bitosx-export",
          "version": 1,
          "exportedAt": "2026-11-10T12:00:00Z",
          "items": [
            {
              "type": "login",
              "name": "Example",
              "username": "fake.user@example.test",
              "password": "FAKE-bitosx-pw",
              "urls": [
                "https://example.com/login",
                "https://example.org"
              ],
              "notes": "first line\nsecond line",
              "favorite": true,
              "createdAt": "2025-01-02T03:04:05.1234567Z",
              "updatedAt": "2026-02-03T04:05:06Z"
            },
            {
              "type": "login",
              "name": "Bare",
              "username": "",
              "password": "",
              "urls": [],
              "notes": "",
              "favorite": false,
              "createdAt": "2025-01-02T03:04:05Z",
              "updatedAt": "2025-01-02T03:04:05Z"
            }
          ]
        }
        """;

    private static string BitosX(string items, string version = "1")
        => "{\"format\": \"bitosx-export\", \"version\": " + version + ", \"exportedAt\": \"2026-11-10T12:00:00Z\", \"items\": [" + items + "]}";

    [Fact]
    public void BitosX_IsReadAsItWritesIt()
    {
        ParsedImport parsed = Parse(BitosXExport);

        Assert.Equal(ImportFormat.BitosXJson, parsed.Format);
        Assert.Equal("BitosX (JSON)", parsed.Label);
        Assert.Equal(2, parsed.RecordCount);
        Assert.Empty(parsed.Unsupported);
        Assert.Collection(
            parsed.Entries,
            first =>
            {
                Assert.Equal(1, first.Position);
                Assert.Equal("Example", first.Name);
                Assert.Equal("fake.user@example.test", first.Username);
                Assert.Equal("FAKE-bitosx-pw", first.Password);
                Assert.Equal(["https://example.com/login", "https://example.org"], first.Addresses);
                Assert.Equal("first line\nsecond line", first.Notes);
                Assert.True(first.Favorite);
                Assert.Equal(string.Empty, first.Totp);
                Assert.Equal(string.Empty, first.Folder);
                Assert.Empty(first.CustomFields);
            },
            second =>
            {
                Assert.Equal("Bare", second.Name);
                Assert.Equal(string.Empty, second.Password);
                Assert.Empty(second.Addresses);
                Assert.False(second.Favorite);
            });
    }

    [Theory]
    [InlineData("2")]
    [InlineData("0")]
    [InlineData("1.5")]
    [InlineData("\"1\"")]
    [InlineData("null")]
    [InlineData("[1]")]
    public void BitosX_AnyVersionButOne_IsRefused_AsAFileOfANewerVersion(string version)
    {
        Assert.Equal(ImportRefusal.UnsupportedVersion, RefusalOf(BitosX(string.Empty, version)));
    }

    [Fact]
    public void BitosX_WithoutAVersion_IsRefusedTheSameWay()
    {
        Assert.Equal(ImportRefusal.UnsupportedVersion, RefusalOf("{\"format\": \"bitosx-export\", \"items\": []}"));
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1e0")]
    public void BitosX_VersionOne_IsTheNumberOne_HoweverItIsWritten(string version)
    {
        Assert.Empty(Parse(BitosX(string.Empty, version)).Entries);
    }

    [Theory]
    [InlineData("{\"format\": \"bitosx-export\", \"version\": 1}")]
    [InlineData("{\"format\": \"bitosx-export\", \"version\": 1, \"items\": null}")]
    [InlineData("{\"format\": \"bitosx-export\", \"version\": 1, \"items\": {}}")]
    public void BitosX_WithoutItsItems_IsNotRecognised(string text)
    {
        Assert.Equal(ImportRefusal.NotRecognised, RefusalOf(text));
    }

    [Fact]
    public void BitosX_AnItemThatIsNotALogin_IsCountedAsOther()
    {
        ParsedImport parsed = Parse(BitosX(
            "{\"type\": \"card\", \"name\": \"x\"}, {\"name\": \"no type\"}, {\"type\": \"Login\", \"name\": \"wrong case\"}, 7, null, " +
            "{\"type\": \"login\", \"name\": \"Kept\", \"password\": \"FAKE-pw\"}"));

        ImportEntry entry = Assert.Single(parsed.Entries);
        Assert.Equal("Kept", entry.Name);
        Assert.Equal(6, entry.Position);
        Assert.Equal(5, parsed.Unsupported[ImportUnsupportedKind.Other]);
        Assert.Equal(6, parsed.RecordCount);
    }

    [Fact]
    public void BitosX_AMemberThatIsAbsent_Null_OrOfAnotherType_CountsAsAbsent()
    {
        ParsedImport parsed = Parse(BitosX(
            "{\"type\": \"login\"}, " +
            "{\"type\": \"login\", \"name\": null, \"username\": null, \"password\": null, \"urls\": null, \"notes\": null, \"favorite\": null}, " +
            "{\"type\": \"login\", \"name\": 1, \"username\": [], \"password\": {}, \"urls\": \"https://example.com\", \"notes\": true, \"favorite\": \"true\"}, " +
            "{\"type\": \"login\", \"name\": \"Odd list\", \"urls\": [\"https://kept.example\", 1, null, [\"https://nested.example\"], {\"uri\": \"x\"}, \"\"]}"));

        Assert.Equal(4, parsed.Entries.Count);
        foreach (ImportEntry entry in parsed.Entries.Take(3))
        {
            Assert.Equal(string.Empty, entry.Name);
            Assert.Equal(string.Empty, entry.Username);
            Assert.Equal(string.Empty, entry.Password);
            Assert.Equal(string.Empty, entry.Notes);
            Assert.False(entry.Favorite);
            Assert.Empty(entry.Addresses);
        }

        Assert.Equal(["https://kept.example", ""], parsed.Entries[3].Addresses);
    }

    [Fact]
    public void BitosX_MembersItDoesNotKnow_AreIgnored()
    {
        ParsedImport parsed = Parse(
            "{\"format\": \"bitosx-export\", \"version\": 1, \"somethingNew\": {\"a\": [1, 2]}, \"items\": [" +
            "{\"type\": \"login\", \"name\": \"Example\", \"password\": \"FAKE-pw\", \"totp\": \"FAKEFAKE\", \"id\": \"x\"}]}");

        ImportEntry entry = Assert.Single(parsed.Entries);
        Assert.Equal("FAKE-pw", entry.Password);
        Assert.Equal(string.Empty, entry.Totp); // not a member of this format
    }

    [Fact]
    public void BitosX_TenThousandItems_AreRead_AndOneMoreIsRefused()
    {
        const string item = "{\"type\": \"login\", \"name\": \"x\", \"password\": \"FAKE-pw\"}";
        string tenThousand = string.Join(',', Enumerable.Repeat(item, 10_000));

        Assert.Equal(10_000, Parse(BitosX(tenThousand)).Entries.Count);
        Assert.Equal(ImportRefusal.TooManyEntries, RefusalOf(BitosX(tenThousand + ",null")));
    }

    [Fact]
    public void AFileThatSaysItIsBitosX_IsReadAsBitosX_WhateverElseItSays()
    {
        // "encrypted" belongs to the other format and means nothing here.
        ParsedImport parsed = Parse("{\"encrypted\": true, \"format\": \"bitosx-export\", \"version\": 1, \"items\": []}");

        Assert.Equal(ImportFormat.BitosXJson, parsed.Format);
    }

    // ---- JSON that is neither ----

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"items\": {}}")]
    [InlineData("{\"items\": null}")]
    [InlineData("{\"format\": \"something-else\", \"version\": 1}")]
    [InlineData("{\"format\": \"BITOSX-EXPORT\", \"version\": 1}")]
    [InlineData("{\"logins\": [{\"name\": \"x\"}]}")]
    [InlineData("[]")]
    [InlineData("[{\"name\": \"Example\", \"password\": \"FAKE-pw\"}]")]
    [InlineData("[[[]]]")]
    public void JsonThatIsNeitherFormat_IsNotRecognised(string text)
    {
        Assert.Equal(ImportRefusal.NotRecognised, RefusalOf(text));
    }

    // ---- JSON that is refused as JSON ----

    [Theory]
    [InlineData("{")]
    [InlineData("{\"items\": [")]
    [InlineData("{\"items\": []")]
    [InlineData("{\"items\": [],}")]
    [InlineData("{\"items\": [1,]}")]
    [InlineData("{'items': []}")]
    [InlineData("{items: []}")]
    [InlineData("{\"items\": [] /* a comment */}")]
    [InlineData("{\"items\": []} // a comment")]
    [InlineData("{\"items\": []} {\"items\": []}")]
    [InlineData("{\"items\": []} x")]
    [InlineData("{\"items\": [NaN]}")]
    [InlineData("{\"items\": [01]}")]
    [InlineData("{\"items\": [\"tab\tin a string\"]}")]
    [InlineData("{\"items\": [\"bad \\x escape\"]}")]
    [InlineData("{\"items\": [\"\\u12G4\"]}")]
    [InlineData("[")]
    [InlineData("[1, 2")]
    public void DamagedJson_IsRefused(string text)
    {
        Assert.Equal(ImportRefusal.InvalidJson, RefusalOf(text));
    }

    [Fact]
    public void ARefusalOfJson_DoesNotQuoteTheFile()
    {
        var refused = Assert.Throws<ImportRefusedException>(() => Parse("{\"items\": [{\"password\": \"FAKE-secret-marker\" oops}]}"));

        Assert.DoesNotContain("FAKE-secret-marker", refused.ToString(), StringComparison.Ordinal);
        Assert.Null(refused.InnerException);
    }

    [Theory]
    [InlineData("{\"items\": [], \"items\": []}")]
    [InlineData("{\"items\": [{\"type\": 1, \"name\": \"a\", \"name\": \"b\"}]}")]
    [InlineData("{\"items\": [], \"unread\": {\"deep\": [{\"x\": 1, \"x\": 2}]}}")]
    [InlineData("{\"items\": [{\"type\": 1, \"login\": {\"password\": \"FAKE-1\", \"password\": \"FAKE-2\"}}]}")]
    [InlineData("{\"encrypted\": true, \"encrypted\": false, \"items\": []}")]
    [InlineData("{\"items\": [], \"" + U + "0069tems\": []}")]
    [InlineData("{\"format\": \"bitosx-export\", \"version\": 1, \"version\": 2, \"items\": []}")]
    public void ANameTwiceInOneObject_IsRefused_WhereverItIs(string text)
    {
        // One of the two values would have to be chosen, and two readers may choose differently.
        Assert.Equal(ImportRefusal.InvalidJson, RefusalOf(text));
    }

    [Fact]
    public void TheSameNameInTwoObjects_IsNothingSpecial()
    {
        Assert.Equal(2, Parse(Bitwarden(Login("\"notes\": \"a\"") + "," + Login("\"notes\": \"b\""))).Entries.Count);
    }

    [Fact]
    public void SixtyFourLevels_AreRead_AndSixtyFiveAreRefused()
    {
        // The object itself is one level, the member's arrays the others.
        static string Nested(int levels) => "{\"items\": [], \"deep\": " + new string('[', levels - 1) + new string(']', levels - 1) + "}";

        Assert.Empty(Parse(Nested(64)).Entries);
        Assert.Equal(ImportRefusal.InvalidJson, RefusalOf(Nested(65)));
        Assert.Equal(ImportRefusal.InvalidJson, RefusalOf(new string('[', 65) + new string(']', 65)));
        Assert.Equal(ImportRefusal.NotRecognised, RefusalOf(new string('[', 64) + new string(']', 64)));
    }

    [Fact]
    public void AMillionLevels_AreRefusedAtOnce()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(ImportRefusal.InvalidJson, RefusalOf(new string('[', 1_000_000)));
        Assert.Equal(ImportRefusal.InvalidJson, RefusalOf("{\"items\":" + string.Concat(Enumerable.Repeat("{\"a\":", 1_000_000))));
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Took {watch.Elapsed}.");
    }

    [Theory]
    [InlineData("{\"items\": [], \"unread\": \"" + U + "D83D\"}")]                       // a high half and nothing after it
    [InlineData("{\"items\": [], \"unread\": \"" + U + "DE00\"}")]                       // a low half on its own
    [InlineData("{\"items\": [], \"unread\": \"" + U + "DE00" + U + "D83D\"}")]             // the two halves the wrong way round
    [InlineData("{\"items\": [], \"unread\": \"" + U + "D83Dx" + U + "DE00\"}")]            // something between them
    [InlineData("{\"items\": [], \"unread\": \"" + U + "D83D\\n" + U + "DE00\"}")]
    [InlineData("{\"items\": [], \"unread\": \"" + U + "D83D" + U + "D83D" + U + "DE00\"}")]   // two high halves
    [InlineData("{\"items\": [], \"" + U + "D83D\": 1}")]                              // in a name
    [InlineData("{\"items\": [{\"type\": 1, \"name\": \"half " + U + "D800\"}]}")]
    [InlineData("{\"format\": \"bitosx-export\", \"version\": 1, \"items\": [{\"type\": \"login\", \"password\": \"FAKE " + U + "DBFF\"}]}")]
    public void HalfOfASurrogatePair_IsRefused_WhereverItIs(string text)
    {
        // Such text cannot be stored: the vault is written as UTF-8.
        Assert.Equal(ImportRefusal.InvalidJson, RefusalOf(text));
    }

    [Fact]
    public void AWholeSurrogatePair_WrittenAsEscapes_IsRead()
    {
        ParsedImport parsed = Parse(Bitwarden(
            "{\"type\": 1, \"name\": \"smile " + U + "D83D" + U + "DE00 and " + B + B + "uD83D as text\", \"login\": {\"password\": \"FAKE-" + U + "00E9-" + U + "D83D" + U + "DE00" + U + "D83D" + U + "DE01\"}}"));

        ImportEntry entry = Assert.Single(parsed.Entries);
        Assert.Equal("smile " + char.ConvertFromUtf32(0x1F600) + " and \\uD83D as text", entry.Name);
        Assert.Equal("FAKE-" + (char)0xE9 + "-" + char.ConvertFromUtf32(0x1F600) + char.ConvertFromUtf32(0x1F601), entry.Password);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("plain", false)]
    [InlineData("\\n\\t\\\"\\\\", false)]
    [InlineData("\\\\uD83D", false)]                 // an escaped backslash, then the letters
    [InlineData("\\u00e9\\u0041", false)]
    [InlineData("\\uD83D\\uDE00", false)]
    [InlineData("\\ud83d\\ude00\\uD83D\\uDE01", false)]
    [InlineData("\\uD83D", true)]
    [InlineData("\\uDE00", true)]
    [InlineData("\\uD83Dx\\uDE00", true)]
    [InlineData("\\uD83D\\\\uDE00", true)]
    [InlineData("\\uD83D\\u0041", true)]
    [InlineData("x\\uDBFF", true)]
    [InlineData("\\uDC00\\uD800", true)]
    public void TheCheckForHalfPairs_ReadsEscapesTheWayJsonDoes(string escaped, bool unpaired)
    {
        Assert.Equal(unpaired, JsonImportReader.HasUnpairedSurrogateEscape(Encoding.ASCII.GetBytes(escaped)));
    }

    [Fact]
    public void JsonMayStartWithAByteOrderMarkAndWhiteSpace()
    {
        byte[] file = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(" \r\n\t" + Bitwarden(Login("\"login\": {\"password\": \"FAKE-pw\"}")) + "\n")];

        Assert.Equal("FAKE-pw", Assert.Single(Parse(file).Entries).Password);
    }
}
