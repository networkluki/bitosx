using System.Text;
using BitosX.Core.Domain;
using BitosX.Core.ImportExport;
using BitosX.Core.Validation;
using static BitosX.Core.Tests.ImportExport.ImportTesting;

namespace BitosX.Core.Tests.ImportExport;

/// <summary>
/// From an entry of a file to the item it would be stored as: what is normalised, what is left out,
/// what is refused and why, what counts as a duplicate, and what a preview is told.
/// </summary>
public sealed class ImportPlanTests
{
    // ---- Names ----

    [Fact]
    public void TheName_IsTrimmed_AndUsernameAndPasswordAreNot()
    {
        ImportCandidate candidate = Only(ChromeRow(name: " \t Example \u00A0", username: "  fake user ", password: " FAKE pw "));

        Assert.Equal("Example", candidate.Name);
        Assert.Equal("  fake user ", candidate.Username);
        Assert.Equal(" FAKE pw ", candidate.Password);
        Assert.False(candidate.NameWasDerived);
        Assert.Equal(ImportStatus.Add, candidate.Status);
    }

    [Theory]
    [InlineData("https://www.example.com/login", "fake.user", "www.example.com")]
    [InlineData("HTTPS://Shop.Example:8443/", "fake.user", "shop.example")]
    [InlineData("example.org/path", "fake.user", "example.org")]                 // after https:// is put in front
    [InlineData("android://x@com.example.app/\nhttps://second.example", "fake.user", "second.example")] // the first web address
    [InlineData("", "fake.user@example.test", "fake.user@example.test")]
    [InlineData("android://x@com.example.app/", "  fake.user  ", "fake.user")]   // trimmed, as a name is
    [InlineData("", "", "Untitled")]
    [InlineData("ftp://files.example", "   ", "Untitled")]
    public void AnEntryWithoutAName_IsNamedAfterItsHost_ElseItsUsername_ElseUntitled(string url, string username, string name)
    {
        ImportCandidate candidate = Only(ChromeRow(name: "  ", url: url, username: username, password: "FAKE-pw"));

        Assert.Equal(name, candidate.Name);
        Assert.True(candidate.NameWasDerived);
        Assert.Equal(ImportStatus.Add, candidate.Status);
    }

    [Fact]
    public void ADerivedName_ThatWouldBeTooLong_IsPassedOver_RatherThanCutOrRefused()
    {
        string longHost = string.Join('.', Enumerable.Repeat("a23456789", 21)) + ".example"; // 217 characters
        string longUser = new('u', 201);

        Assert.Equal("fake.user", Only(ChromeRow(url: "https://" + longHost, username: "fake.user", password: "FAKE")).Name);
        Assert.Equal("Untitled", Only(ChromeRow(url: "https://" + longHost, username: longUser, password: "FAKE")).Name);
        Assert.Equal(new string('u', 200), Only(ChromeRow(username: new string('u', 200), password: "FAKE")).Name);
        Assert.Equal(ImportStatus.Add, Only(ChromeRow(username: longUser, password: "FAKE")).Status);
    }

    [Fact]
    public void ANameTheFileGives_IsNeverReplaced_EvenIfItIsTooLong()
    {
        ImportCandidate candidate = Only(ChromeRow(name: new string('n', 201), url: "https://example.com", password: "FAKE"));

        Assert.Equal(201, candidate.Name.Length);
        Assert.False(candidate.NameWasDerived);
        Assert.Equal(ImportReason.NameTooLong, candidate.Reason);
    }

    // ---- Addresses ----

    [Fact]
    public void Addresses_AreTrimmed_SplitAtLineEnds_AndEmptyOnesAreDropped()
    {
        ImportCandidate candidate = Only(ChromeRow(
            name: "Example", url: "  https://a.example/ \r\n\r\n\thttps://b.example\n   \nhttps://c.example\r", password: "FAKE"));

        Assert.Equal(["https://a.example/", "https://b.example", "https://c.example"], candidate.Urls);
        Assert.Empty(candidate.Extras.OtherAddresses);
        Assert.Equal(0, candidate.HttpsAdded);
    }

    [Fact]
    public void WhatLooksLikeAHost_GetsHttps_AndWhatIsNotAWebAddress_IsAnExtra()
    {
        ImportCandidate candidate = Only(ChromeRow(
            name: "Example",
            url: "example.com\nHTTP://Kept.As.It.Is/Path\n192.168.1.1:8080/admin\nandroid://x@com.example.app/\nlocalhost:3000\nmailto:fake@example.test\nMy bank",
            password: "FAKE"));

        Assert.Equal(["https://example.com", "HTTP://Kept.As.It.Is/Path", "https://192.168.1.1:8080/admin"], candidate.Urls);
        Assert.Equal(2, candidate.HttpsAdded);
        Assert.Equal(
            ["android://x@com.example.app/", "localhost:3000", "mailto:fake@example.test", "My bank"],
            candidate.Extras.OtherAddresses);
        Assert.Equal(ImportStatus.Add, candidate.Status);
    }

    [Fact]
    public void BitwardensCommaSeparatedAddresses_AreTrimmedAndSorted_LikeAnyOthers()
    {
        ImportCandidate candidate = Only(
            "name,login_uri,login_password\nExample,\" https://a.example , example.org,, androidapp://com.example.app \",FAKE-pw\n");

        Assert.Equal(["https://a.example", "https://example.org"], candidate.Urls);
        Assert.Equal(["androidapp://com.example.app"], candidate.Extras.OtherAddresses);
        Assert.Equal(1, candidate.HttpsAdded);
    }

    [Fact]
    public void TheSameAddressTwice_StaysTwice()
    {
        ImportCandidate candidate = Only(ChromeRow(name: "Example", url: "https://example.com\nhttps://example.com", password: "FAKE"));

        Assert.Equal(["https://example.com", "https://example.com"], candidate.Urls);
    }

    // ---- Limits: an entry that breaks one is not imported, and nothing is cut ----

    public static TheoryData<string, string, ImportReason?> AtAndOverEachLimit => new()
    {
        { "a name of 200", ChromeRow(name: new string('n', 200), password: "FAKE"), null },
        { "a name of 201", ChromeRow(name: new string('n', 201), password: "FAKE"), ImportReason.NameTooLong },
        { "a username of 320", ChromeRow(name: "x", username: new string('u', 320), password: "FAKE"), null },
        { "a username of 321", ChromeRow(name: "x", username: new string('u', 321), password: "FAKE"), ImportReason.UsernameTooLong },
        { "a password of 4096", ChromeRow(name: "x", password: new string('p', 4096)), null },
        { "a password of 4097", ChromeRow(name: "x", password: new string('p', 4097)), ImportReason.PasswordTooLong },
        { "notes of 16384", ChromeRow(name: "x", password: "FAKE", note: new string('n', 16384)), null },
        { "notes of 16385", ChromeRow(name: "x", password: "FAKE", note: new string('n', 16385)), ImportReason.NotesTooLong },
        { "an address of 2048", ChromeRow(name: "x", url: "https://example.com/" + new string('a', 2028), password: "FAKE"), null },
        { "an address of 2049", ChromeRow(name: "x", url: "https://example.com/" + new string('a', 2029), password: "FAKE"), ImportReason.UrlTooLong },
        { "a host of 2040, 2048 with https://", ChromeRow(name: "x", url: "example.com/" + new string('a', 2028), password: "FAKE"), null },
        { "a host of 2041, 2049 with https://", ChromeRow(name: "x", url: "example.com/" + new string('a', 2029), password: "FAKE"), ImportReason.UrlTooLong },
        { "64 addresses", ChromeRow(name: "x", url: string.Join('\n', Enumerable.Range(1, 64).Select(i => $"https://example.com/{i}")), password: "FAKE"), null },
        { "65 addresses", ChromeRow(name: "x", url: string.Join('\n', Enumerable.Range(1, 65).Select(i => $"https://example.com/{i}")), password: "FAKE"), ImportReason.TooManyUrls },
        { "an address without a host", ChromeRow(name: "x", url: "https://", password: "FAKE"), ImportReason.UrlInvalid },
        { "an address with a space in its host", ChromeRow(name: "x", url: "https://not a host/", password: "FAKE"), ImportReason.UrlInvalid },
        { "an address with a port that is none", ChromeRow(name: "x", url: "example.com:99999", password: "FAKE"), ImportReason.UrlInvalid },
        { "an address with an empty part in its host", ChromeRow(name: "x", url: "https://example..com/", password: "FAKE"), ImportReason.UrlInvalid },
        { "an address whose host ends in a number", ChromeRow(name: "x", url: "https://example.123/", password: "FAKE"), ImportReason.UrlInvalid },
        { "an address with two at signs", ChromeRow(name: "x", url: "https://user:p@ss@example.com/", password: "FAKE"), ImportReason.UrlInvalid },
        { "an address with an IPv4 host", ChromeRow(name: "x", url: "https://192.168.1.1:8443/", password: "FAKE"), null },
        { "an address with an IPv6 host", ChromeRow(name: "x", url: "https://[2001:db8::1]:8443/", password: "FAKE"), null },
        { "an address with a host of one part", ChromeRow(name: "x", url: "http://intranet/", password: "FAKE"), null },
    };

    [Theory]
    [MemberData(nameof(AtAndOverEachLimit))]
    public void EachLimitOfTheVault_HoldsExactly(string what, string file, ImportReason? reason)
    {
        ImportPlan plan = Plan(file);
        ImportCandidate candidate = Assert.Single(plan.Candidates);

        Assert.True(candidate.Reason == reason, what);
        Assert.Equal(reason is null ? ImportStatus.Add : ImportStatus.NotImported, candidate.Status);
        Assert.Equal(reason is null ? 1 : 0, plan.ToAdd);
        Assert.Equal(reason is null ? 0 : 1, plan.NotImported.Count);
        Assert.Equal(reason is null ? 1 : 0, plan.CreateItems(DateTimeOffset.UnixEpoch).Count);
    }

    [Fact]
    public void OnlyWebAddresses_CountTowardsTheLimitOf64()
    {
        string urls = string.Join('\n', Enumerable.Range(1, 64).Select(i => $"https://example.com/{i}"))
            + "\n" + string.Join('\n', Enumerable.Range(1, 30).Select(i => $"android://x@app{i}/"));

        ImportCandidate candidate = Only(ChromeRow(name: "x", url: urls, password: "FAKE"));

        Assert.Equal(ImportStatus.Add, candidate.Status);
        Assert.Equal(30, candidate.Extras.OtherAddresses.Count);
    }

    [Fact]
    public void LengthsAreCountedInUtf16Units_AsTheVaultCountsThem()
    {
        // 100 characters outside the basic plane are 200 units: the longest name there is.
        string hundred = string.Concat(Enumerable.Repeat(char.ConvertFromUtf32(0x1F600), 100));

        Assert.Null(Only(ChromeRow(name: hundred, password: "FAKE")).Reason);
        Assert.Equal(ImportReason.NameTooLong, Only(ChromeRow(name: hundred + "x", password: "FAKE")).Reason);
    }

    [Fact]
    public void OfSeveralBrokenLimits_TheFirstInAFixedOrderIsTheReason()
    {
        string tooManyTooLongAndInvalid =
            string.Join('\n', Enumerable.Range(1, 65).Select(i => $"https://example.com/{i}")) + "\nhttps://example.com/" + new string('a', 2100) + "\nhttps://";

        ImportReason?[] reasons =
        [
            Only(ChromeRow(name: new string('n', 201), url: tooManyTooLongAndInvalid, username: new string('u', 321), password: new string('p', 4097), note: new string('n', 16385))).Reason,
            Only(ChromeRow(name: "x", url: tooManyTooLongAndInvalid, username: new string('u', 321), password: new string('p', 4097), note: new string('n', 16385))).Reason,
            Only(ChromeRow(name: "x", url: tooManyTooLongAndInvalid, password: new string('p', 4097), note: new string('n', 16385))).Reason,
            Only(ChromeRow(name: "x", url: tooManyTooLongAndInvalid, password: "FAKE", note: new string('n', 16385))).Reason,
            Only(ChromeRow(name: "x", url: tooManyTooLongAndInvalid, password: "FAKE")).Reason,
            Only(ChromeRow(name: "x", url: "https://\nhttps://example.com/" + new string('a', 2100), password: "FAKE")).Reason,
            Only(ChromeRow(name: "x", url: "https://", password: "FAKE")).Reason,
        ];

        Assert.Equal(
            [
                ImportReason.NameTooLong, ImportReason.UsernameTooLong, ImportReason.PasswordTooLong, ImportReason.NotesTooLong,
                ImportReason.TooManyUrls, ImportReason.UrlTooLong, ImportReason.UrlInvalid,
            ],
            reasons);
    }

    [Fact]
    public void AFaultInHowTheEntryIsWritten_ComesBeforeAnyLimit()
    {
        ImportPlan plan = Plan("name,password\n" + new string('n', 300) + ",FAKE,extra value\n");

        Assert.Equal(ImportReason.RowHasExtraValues, Assert.Single(plan.NotImported).Reason);

        ImportPlan xml = Plan(
            "<KeePassFile><Root><Group><Entry>" +
            "<String><Key>Title</Key><Value>Twice</Value></String><String><Key>Title</Key><Value>Again</Value></String>" +
            "</Entry></Group></Root></KeePassFile>");
        ImportCandidate twice = Assert.Single(xml.NotImported);
        Assert.Equal(ImportReason.FieldTwice, twice.Reason);
        Assert.Equal("Twice", twice.Name);
    }

    [Fact]
    public void AnEntryThatIsNotImported_KeepsItsValuesWhole()
    {
        string password = new('p', 5000);

        ImportCandidate candidate = Only(ChromeRow(name: "Too long", password: password));

        Assert.Equal(ImportStatus.NotImported, candidate.Status);
        Assert.Equal(password, candidate.Password); // not shortened to fit
    }

    // ---- Entries with nothing in them ----

    [Theory]
    [InlineData("name,url,username,password,note,x\n,,,,,something in a column that is not read\n")]
    [InlineData("name,url,username,password,note,x\n  , \t ,  ,\" \", \u00A0 ,y\n")]
    [InlineData("name,password,folder,favorite,fields,x\n,,Work,1,\"PIN: \nEmpty:   \",y\n")]
    public void AnEntryWithNothingInIt_IsCounted_AndPassedOver(string file)
    {
        ImportPlan plan = Plan(file);

        Assert.Empty(plan.Candidates);
        Assert.Equal(1, plan.EmptyEntries);
        Assert.Equal(0, plan.ToAdd);
        Assert.Single(plan.Source.Entries);
    }

    [Theory]
    [InlineData("name,password,x\nOnly a name,,y\n", "Only a name")]
    [InlineData("name,username,password\n,only a username,\n", "only a username")]
    [InlineData("name,password\n,FAKE-only-a-password\n", "Untitled")]
    [InlineData("name,password,notes\n,,only notes\n", "Untitled")]
    [InlineData("name,url,password\n,https://only.an.address.example,\n", "only.an.address.example")]
    [InlineData("name,url,password\n,only-an-address-that-is-none,\n", "Untitled")]
    [InlineData("name,password,totp\n,,FAKEFAKE\n", "Untitled")]
    [InlineData("name,password,fields\n,,PIN: 0000\n", "Untitled")]
    public void AnEntryWithAnythingInIt_IsAnEntry(string file, string name)
    {
        ImportPlan plan = Plan(file);

        Assert.Equal(name, Assert.Single(plan.Candidates).Name);
        Assert.Equal(0, plan.EmptyEntries);
    }

    // ---- Extras ----

    private const string BitwardenRowWithExtras =
        "folder,favorite,type,name,notes,fields,reprompt,login_uri,login_username,login_password,login_totp\n" +
        " Work/Mail ,1,login,Example,my own notes,\"PIN: 0000\nEmpty: \nno separator\n Recovery : FAKE: recovery \",0,\"https://example.com,androidapp://com.example.app, ftp://files.example\",fake.user,FAKE-pw,  FAKEFAKEFAKEFAKE \n";

    [Fact]
    public void Extras_AreLeftOut_AndCounted()
    {
        ImportPlan plan = Plan(BitwardenRowWithExtras);
        ImportCandidate candidate = Assert.Single(plan.Candidates);

        Assert.Equal("my own notes", candidate.Notes); // nothing was added to them
        Assert.Equal("FAKEFAKEFAKEFAKE", candidate.Extras.Totp);
        Assert.Equal("Work/Mail", candidate.Extras.Folder);
        Assert.Equal(["androidapp://com.example.app", "ftp://files.example"], candidate.Extras.OtherAddresses);
        Assert.Equal(
            [("PIN", "0000"), ("", "no separator"), ("Recovery : FAKE", "recovery ")],
            candidate.Extras.CustomFields.Select(f => (f.Name, f.Value))); // the one without a value is not one

        Assert.Equal(1, plan.Extras[ImportExtraKind.Totp]);
        Assert.Equal(1, plan.Extras[ImportExtraKind.Folder]);
        Assert.Equal(2, plan.Extras[ImportExtraKind.OtherAddress]);
        Assert.Equal(3, plan.Extras[ImportExtraKind.CustomField]);
        Assert.True(candidate.Favorite);
    }

    [Fact]
    public void WithTheChoiceToCopyThem_ExtrasAreAppendedToTheNotes_OneLabelledLineEach()
    {
        ImportCandidate candidate = Only(BitwardenRowWithExtras, new ImportOptions(CopyExtrasToNotes: true));

        Assert.Equal(
            "my own notes\n" +
            "\n" +
            "Authenticator key (TOTP): FAKEFAKEFAKEFAKE\n" +
            "Folder: Work/Mail\n" +
            "Other address: androidapp://com.example.app\n" +
            "Other address: ftp://files.example\n" +
            "PIN: 0000\n" +
            "Field: no separator\n" +
            "Recovery : FAKE: recovery ",
            candidate.Notes);
        Assert.Equal(ImportStatus.Add, candidate.Status);

        // The extras are still told apart, and still counted: the preview says what was copied.
        Assert.Equal("FAKEFAKEFAKEFAKE", candidate.Extras.Totp);
        Assert.Equal(3, candidate.Extras.CustomFields.Count);
    }

    [Fact]
    public void WithoutNotesOfItsOwn_TheCopiedLinesAreTheNotes()
    {
        ImportCandidate candidate = Only("name,password,totp\nExample,FAKE-pw,FAKEFAKE\n", new ImportOptions(CopyExtrasToNotes: true));

        Assert.Equal("Authenticator key (TOTP): FAKEFAKE", candidate.Notes);
    }

    [Fact]
    public void WithoutExtras_TheChoiceChangesNothing()
    {
        string file = ChromeRow(name: "Example", url: "https://example.com", password: "FAKE", note: "my notes\n");

        Assert.Equal("my notes\n", Only(file, new ImportOptions(CopyExtrasToNotes: true)).Notes);
        Assert.Equal("my notes\n", Only(file).Notes);
    }

    [Fact]
    public void AValueOfSeveralLines_IsCopiedAsItIs()
    {
        ImportCandidate candidate = Only(
            "{\"items\": [{\"type\": 1, \"name\": \"Example\", \"notes\": \"own\", \"fields\": [{\"name\": \"Answers\", \"value\": \"first\\nsecond\"}, {\"name\": \"\", \"value\": \"nameless\"}]}]}",
            new ImportOptions(CopyExtrasToNotes: true));

        Assert.Equal("own\n\nAnswers: first\nsecond\nField: nameless", candidate.Notes);
    }

    [Fact]
    public void NotesThatFit_ButNotWithTheExtras_MeanTheEntryIsNotImported_WithThatReason()
    {
        string file = "name,password,notes,totp\nExample,FAKE-pw," + new string('n', 16384 - 40) + ",FAKEFAKEFAKEFAKE\n";

        ImportPlan left = Plan(file);
        ImportPlan copied = Plan(file, new ImportOptions(CopyExtrasToNotes: true));

        Assert.Equal(1, left.ToAdd);
        Assert.Equal(ImportReason.NotesTooLongWithExtras, Assert.Single(copied.NotImported).Reason);
        Assert.Equal(0, copied.ToAdd);
        Assert.Empty(copied.Extras); // nothing is copied for an entry that is not imported
    }

    [Fact]
    public void NotesThatAreExactlyAtTheLimitWithTheExtras_AreImported()
    {
        // "\n\nAuthenticator key (TOTP): FAKE" is 32 characters.
        string file = "name,password,notes,totp\nExample,FAKE-pw," + new string('n', 16384 - 32) + ",FAKE\n";

        ImportCandidate candidate = Only(file, new ImportOptions(CopyExtrasToNotes: true));

        Assert.Equal(ImportStatus.Add, candidate.Status);
        Assert.Equal(16384, candidate.Notes.Length);
    }

    [Fact]
    public void NotesThatAreTooLongAnyway_SayThat_NotTheExtras()
    {
        string file = "name,password,notes,totp\nExample,FAKE-pw," + new string('n', 16385) + ",FAKE\n";

        Assert.Equal(ImportReason.NotesTooLong, Only(file, new ImportOptions(CopyExtrasToNotes: true)).Reason);
    }

    // ---- Duplicates ----

    [Fact]
    public void AnEntryEqualToAnItemOfTheVault_IsADuplicate_AndSkipped()
    {
        LoginItem existing = Login("Example", "fake.user", "FAKE-pw", "https://example.com", "https://example.org");

        ImportPlan plan = Plan(
            ChromeRow(name: "Example", url: "https://example.com\nhttps://example.org", username: "fake.user", password: "FAKE-pw", note: "other notes"),
            ImportOptions.Default,
            existing);

        ImportCandidate candidate = Assert.Single(plan.Candidates);
        Assert.Equal(ImportStatus.Duplicate, candidate.Status);
        Assert.True(candidate.IsDuplicate);
        Assert.Null(candidate.Reason);
        Assert.Equal(1, plan.Duplicates);
        Assert.Equal(0, plan.ToAdd);
        Assert.Empty(plan.CreateItems(DateTimeOffset.UnixEpoch));
    }

    public static TheoryData<string, LoginItem> AlmostTheSame => new()
    {
        { "another name", Login("Example 2", "fake.user", "FAKE-pw", "https://example.com") },
        { "a name in another case", Login("example", "fake.user", "FAKE-pw", "https://example.com") },
        { "another username", Login("Example", "other.user", "FAKE-pw", "https://example.com") },
        { "another password", Login("Example", "fake.user", "FAKE-pw2", "https://example.com") },
        { "a password with a space more", Login("Example", "fake.user", "FAKE-pw ", "https://example.com") },
        { "no address", Login("Example", "fake.user", "FAKE-pw") },
        { "another address", Login("Example", "fake.user", "FAKE-pw", "https://example.com/") },
        { "one address more", Login("Example", "fake.user", "FAKE-pw", "https://example.com", "https://example.org") },
        { "the address in another case", Login("Example", "fake.user", "FAKE-pw", "https://EXAMPLE.com") },
    };

    [Theory]
    [MemberData(nameof(AlmostTheSame))]
    public void AnEntryThatDiffersInOneOfTheFourValues_IsNotADuplicate(string what, LoginItem existing)
    {
        ImportPlan plan = Plan(
            ChromeRow(name: "Example", url: "https://example.com", username: "fake.user", password: "FAKE-pw"), ImportOptions.Default, existing);

        Assert.True(plan.ToAdd == 1, what);
        Assert.Equal(0, plan.Duplicates);
    }

    [Fact]
    public void TheOrderOfTheAddresses_Counts()
    {
        LoginItem existing = Login("Example", "u", "FAKE", "https://b.example", "https://a.example");

        ImportPlan plan = Plan(ChromeRow(name: "Example", url: "https://a.example\nhttps://b.example", username: "u", password: "FAKE"), ImportOptions.Default, existing);

        Assert.Equal(1, plan.ToAdd);
    }

    [Fact]
    public void NoValueAndAnEmptyValue_AreTheSame()
    {
        LoginItem[] existing =
        [
            new() { Name = "No username", Username = null, Password = "FAKE-1" },
            new() { Name = "No password", Username = "u", Password = null },
            new() { Name = "Empty both", Username = "", Password = "" },
        ];

        ImportPlan plan = Plan("name,username,password\nNo username,,FAKE-1\nNo password,u,\nEmpty both,,\n", ImportOptions.Default, existing);

        Assert.Equal(3, plan.Duplicates);
        Assert.Equal(0, plan.ToAdd);
    }

    [Fact]
    public void NotesAndTheFavoriteMark_AreNotCompared()
    {
        LoginItem existing = Login("Example", "u", "FAKE") with { Notes = "the vault's notes", Favorite = true };

        ImportPlan plan = Plan("name,username,password,notes,favorite\nExample,u,FAKE,the file's notes,0\n", ImportOptions.Default, existing);

        Assert.Equal(1, plan.Duplicates);
    }

    [Fact]
    public void AnItemOfTheVault_IsComparedTrimmedTheWayAnEntryIs()
    {
        // The vault accepts a name or an address with a space around it; what is read back from an
        // export of such an item is trimmed, and is still the same item.
        LoginItem existing = Login(" Example ", "u", "FAKE", " https://example.com ");

        ImportPlan plan = Plan(ChromeRow(name: "Example", url: "https://example.com", username: "u", password: "FAKE"), ImportOptions.Default, existing);

        Assert.Equal(1, plan.Duplicates);
    }

    [Fact]
    public void AnEntryEqualToAnEarlierEntryOfTheFile_IsADuplicate()
    {
        ImportPlan plan = Plan(
            "name,url,username,password,note\n" +
            "Example,https://example.com,u,FAKE,first\n" +
            "Other,https://example.org,u,FAKE,\n" +
            " Example ,example.com,u,FAKE,the same once it is normalised\n" +
            "Example,https://example.com/,u,FAKE,another address\n" +
            "Example,https://example.com,u,FAKE,fifth\n");

        Assert.Equal(
            [ImportStatus.Add, ImportStatus.Add, ImportStatus.Duplicate, ImportStatus.Add, ImportStatus.Duplicate],
            plan.Candidates.Select(c => c.Status));
        Assert.Equal(2, plan.Duplicates);
        Assert.Equal(3, plan.ToAdd);
    }

    [Fact]
    public void TheComparison_IsOfWhatWouldBeStored()
    {
        // A derived name, and https:// put in front, are what the entry is compared with.
        ImportPlan plan = Plan(
            "name,url,username,password\n,example.com,u,FAKE\nexample.com,https://example.com,u,FAKE\n",
            ImportOptions.Default,
            Login("other", "x", "y"));

        Assert.Equal([ImportStatus.Add, ImportStatus.Duplicate], plan.Candidates.Select(c => c.Status));
    }

    [Fact]
    public void AnEntryThatIsNotImported_IsNoOnesDuplicate_AndNothingIsADuplicateOfIt()
    {
        string tooLong = new('n', 16385);

        ImportPlan plan = Plan(
            "name,username,password,note\n" +
            $"Example,u,FAKE,{tooLong}\n" +   // not imported: its notes are too long
            "Example,u,FAKE,fits\n" +         // the same four values, and importable
            $"Example,u,FAKE,{tooLong}\n");   // not imported, and not called a duplicate either

        Assert.Equal(
            [ImportStatus.NotImported, ImportStatus.Add, ImportStatus.NotImported],
            plan.Candidates.Select(c => c.Status));
        Assert.Equal(0, plan.Duplicates);
        Assert.All(plan.Candidates, c => Assert.False(c.IsDuplicate));
    }

    [Fact]
    public void WithTheChoiceToImportDuplicates_TheyAreAdded_AndStillCounted()
    {
        LoginItem existing = Login("Example", "u", "FAKE");
        string file = "name,username,password\nExample,u,FAKE\nExample,u,FAKE\nOther,u,FAKE\n";

        ImportPlan plan = Plan(file, new ImportOptions(ImportDuplicates: true), existing);

        Assert.All(plan.Candidates, c => Assert.Equal(ImportStatus.Add, c.Status));
        Assert.Equal([true, true, false], plan.Candidates.Select(c => c.IsDuplicate));
        Assert.Equal(2, plan.Duplicates);
        Assert.Equal(3, plan.ToAdd);
        Assert.Equal(3, plan.CreateItems(DateTimeOffset.UnixEpoch).Count);
    }

    [Fact]
    public void CopyingExtrasIntoTheNotes_DoesNotChangeWhatIsADuplicate()
    {
        LoginItem existing = Login("Example", "u", "FAKE");
        string file = "name,username,password,totp\nExample,u,FAKE,FAKEFAKE\n";

        Assert.Equal(1, Plan(file, new ImportOptions(CopyExtrasToNotes: true), existing).Duplicates);
    }

    [Fact]
    public void ValuesThatRunIntoOneAnother_AreNotMistakenForTheSame()
    {
        // The four values are compared one by one, not as one text.
        ImportPlan plan = Plan(
            "name,username,password\nab,c,FAKE\na,bc,FAKE\n\"a,b\",c,FAKE\n", ImportOptions.Default, Login("a", "b,c", "FAKE"), Login("abc", null, "FAKE"));

        Assert.Equal(3, plan.ToAdd);
        Assert.Equal(0, plan.Duplicates);
    }

    [Fact]
    public void TenThousandEntries_AgainstTenThousandItems_AreComparedInOnePass()
    {
        VaultItem[] existing = [.. Enumerable.Range(0, 10_000).Select(i => Login("Existing " + i, "u", "FAKE-" + i, "https://example.com/" + i))];
        var file = new StringBuilder("name,url,username,password\n");
        for (int i = 0; i < 10_000; i++)
        {
            // Every second one is in the vault already.
            file.Append(i % 2 == 0 ? "Existing " : "New ").Append(i).Append(",https://example.com/").Append(i).Append(",u,FAKE-").Append(i).Append('\n');
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        ImportPlan plan = Plan(Parse(file.ToString()), ImportOptions.Default, existing);
        watch.Stop();

        Assert.Equal(5_000, plan.Duplicates);
        Assert.Equal(5_000, plan.ToAdd);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Took {watch.Elapsed}.");
    }

    // ---- What a preview is told ----

    [Fact]
    public void TheCounts_DescribeTheWholeFile()
    {
        string file =
            "folder,favorite,type,name,notes,fields,reprompt,login_uri,login_username,login_password,login_totp\n" +
            "Work,1,login,Added,,PIN: 0000,0,example.com,u,FAKE-1,FAKEFAKE\n" +                         // added: https, a folder, a field, a key
            ",,login,,,,0,\"https://derived.example,android://x@app/\",u,FAKE-2,\n" +                    // added: name derived, one other address
            ",,note,A note,text,,0,,,,\n" +                                                              // not a login
            ",,login,In the vault,,,0,,u,FAKE-3,\n" +                                                    // a duplicate
            ",,login,Too long,,,0,,u," + new string('p', 4097) + ",FAKEFAKE\n" +                         // not imported
            ",,login,,,,0,,,,\n" +                                                                       // nothing in it
            ",,login,Added,,PIN: 0000,0,example.com,u,FAKE-1,FAKEFAKE\n";                                // a duplicate of the first row

        ImportPlan plan = Plan(file, ImportOptions.Default, Login("In the vault", "u", "FAKE-3"), Login("Unrelated", "x", "y"));

        Assert.Equal("Bitwarden (CSV)", plan.Source.Label);
        Assert.Equal(7, plan.Source.RecordCount);
        Assert.Equal(1, plan.Source.Unsupported[ImportUnsupportedKind.SecureNote]);
        Assert.Equal(["reprompt"], plan.Source.IgnoredColumns);

        Assert.Equal(5, plan.Candidates.Count);
        Assert.Equal(2, plan.ToAdd);
        Assert.Equal(2, plan.Duplicates);
        Assert.Equal("Too long", Assert.Single(plan.NotImported).Name);
        Assert.Equal(1, plan.EmptyEntries);

        // What was normalised, and what is left out, of the two that are added: not of the duplicates.
        Assert.Equal(1, plan.HttpsAdded);
        Assert.Equal(1, plan.NamesDerived);
        Assert.Equal(1, plan.Extras[ImportExtraKind.Totp]);
        Assert.Equal(1, plan.Extras[ImportExtraKind.Folder]);
        Assert.Equal(1, plan.Extras[ImportExtraKind.OtherAddress]);
        Assert.Equal(1, plan.Extras[ImportExtraKind.CustomField]);

        Assert.Equal(2, plan.VaultItemsBefore);
        Assert.Equal(4, plan.VaultItemsAfter);
        Assert.False(plan.ExceedsVaultItemLimit);
        Assert.Same(ImportOptions.Default, plan.Options);
    }

    [Fact]
    public void AKindWithNothingOfIt_IsAbsentFromTheCounts()
    {
        ImportPlan plan = Plan(ChromeRow(name: "Example", url: "https://example.com", password: "FAKE"));

        Assert.Empty(plan.Extras);
        Assert.Empty(plan.Source.Unsupported);
    }

    [Fact]
    public void AnImportThatWouldPassTheLimitOfAVault_SaysSo()
    {
        VaultItem[] existing = [.. Enumerable.Range(0, 9_999).Select(i => Login("Existing " + i))];
        string one = "name,password\nNew 1,FAKE\n";
        string two = "name,password\nNew 1,FAKE\nNew 2,FAKE\n";
        string twoOfWhichOneIsThere = "name,password\nNew 1,FAKE\nExisting 5,\n";

        Assert.False(Plan(Parse(one), ImportOptions.Default, existing).ExceedsVaultItemLimit);            // exactly 10,000
        Assert.True(Plan(Parse(two), ImportOptions.Default, existing).ExceedsVaultItemLimit);             // 10,001
        Assert.False(Plan(Parse(twoOfWhichOneIsThere), ImportOptions.Default, existing).ExceedsVaultItemLimit);
        Assert.True(Plan(Parse(twoOfWhichOneIsThere), new ImportOptions(ImportDuplicates: true), existing).ExceedsVaultItemLimit);
    }

    [Fact]
    public void PlanningTwice_WithOtherChoices_GivesTwoPlansThatDoNotShareAnything()
    {
        ParsedImport parsed = Parse("name,username,password,totp\nExample,u,FAKE,FAKEFAKE\nExample,u,FAKE,FAKEFAKE\n");

        ImportPlan first = Plan(parsed);
        ImportPlan second = Plan(parsed, new ImportOptions(CopyExtrasToNotes: true, ImportDuplicates: true));
        ImportPlan third = Plan(parsed);

        Assert.Equal([ImportStatus.Add, ImportStatus.Duplicate], first.Candidates.Select(c => c.Status));
        Assert.Equal([ImportStatus.Add, ImportStatus.Add], second.Candidates.Select(c => c.Status));
        Assert.Equal([ImportStatus.Add, ImportStatus.Duplicate], third.Candidates.Select(c => c.Status));
        Assert.Equal(string.Empty, first.Candidates[0].Notes);
        Assert.Equal("Authenticator key (TOTP): FAKEFAKE", second.Candidates[0].Notes);
    }

    // ---- The items ----

    [Fact]
    public void TheItems_AreNew_AndCarryTheTimeOfTheImport()
    {
        var importedAt = new DateTimeOffset(2026, 11, 10, 12, 30, 45, TimeSpan.Zero);
        ImportPlan plan = Plan(
            "name,url,username,password,note,favorite\n" +
            "Example,\"https://example.com\nexample.org\",fake.user,FAKE-pw,a note,1\n" +
            "Bare,,,,,\n" +
            "Second,,,FAKE-2,,\n");

        IReadOnlyList<LoginItem> items = plan.CreateItems(importedAt);

        Assert.Collection(
            items,
            first =>
            {
                Assert.Equal("Example", first.Name);
                Assert.Equal("fake.user", first.Username);
                Assert.Equal("FAKE-pw", first.Password);
                Assert.Equal(["https://example.com", "https://example.org"], first.Urls);
                Assert.Equal("a note", first.Notes);
                Assert.True(first.Favorite);
            },
            bare =>
            {
                // What the file leaves empty is stored the way the editor stores it: as nothing.
                Assert.Equal("Bare", bare.Name);
                Assert.Null(bare.Username);
                Assert.Null(bare.Password);
                Assert.Null(bare.Notes);
                Assert.Empty(bare.Urls);
                Assert.False(bare.Favorite);
            },
            second => Assert.Equal("FAKE-2", second.Password));

        Assert.All(items, item =>
        {
            Assert.Equal(importedAt, item.CreatedAt);
            Assert.Equal(importedAt, item.UpdatedAt);
            Assert.NotEqual(Guid.Empty, item.Id);
            VaultValidation.Validate(item);
        });
        Assert.Equal(3, items.Select(i => i.Id).Distinct().Count());

        // Asked again, they are other items: an identifier is never taken from a file, nor used twice.
        Assert.Empty(plan.CreateItems(importedAt).Select(i => i.Id).Intersect(items.Select(i => i.Id)));
    }

    [Fact]
    public void EveryItemAPlanWouldAdd_IsOneTheVaultAccepts()
    {
        // Whatever the file holds: an entry is either refused with a reason, or valid.
        string[] names = ["Example", "", " ", "\u0085", "\uFEFF", "\u200B", new string('n', 200), new string('n', 201), "a\nb", "\u0000"];
        string[] urls =
        [
            "", "https://example.com", "example.com", "https://", "https://\u00A0/", "http://a b/", "https://[::1]/", "HTTPS://X",
            "https://example.com/" + new string('a', 2040), "x.y:99999", "https://\u0085.example/", "https://exa_mple.com", "ftp://x", "https://-", "https://.",
        ];
        string[] usernames = ["", "u", " ", new string('u', 320), new string('u', 321), "\u00A0"];
        var file = new StringBuilder("name,url,username,password,note\n");
        foreach (string name in names)
        {
            foreach (string url in urls)
            {
                foreach (string username in usernames)
                {
                    file.Append(Quote(name)).Append(',').Append(Quote(url)).Append(',').Append(Quote(username)).Append(",FAKE,\n");
                }
            }
        }

        ImportPlan plan = Plan(Parse(file.ToString()), new ImportOptions(ImportDuplicates: true));
        IReadOnlyList<LoginItem> items = plan.CreateItems(DateTimeOffset.UtcNow);

        Assert.True(items.Count > 300, $"{items.Count} items");
        Assert.True(plan.NotImported.Count > 100, $"{plan.NotImported.Count} not imported");
        Assert.All(items, VaultValidation.Validate);

        var vault = new Vault();
        vault.AddRange([.. items]); // as the vault service adds them
        Assert.Equal(items.Count, vault.Items.Count);
    }

    [Fact]
    public void NothingAboutAnEntry_PrintsItsSecrets()
    {
        ImportPlan plan = Plan(
            "name,username,password,notes,totp,fields\nExample,fake.user,FAKE-secret-marker,FAKE-note-marker,FAKE-totp-marker,PIN: FAKE-field-marker\n",
            new ImportOptions(CopyExtrasToNotes: true));
        ImportCandidate candidate = Assert.Single(plan.Candidates);

        // What a log line or a debugger shows when handed the object.
        string[] printed =
        [
            candidate.ToString()!, candidate.Extras.ToString()!, candidate.Extras.CustomFields[0].ToString()!,
            plan.ToString()!, plan.Source.ToString()!, plan.Source.Entries[0].ToString()!,
        ];

        Assert.All(printed, text => Assert.DoesNotContain("FAKE-", text, StringComparison.Ordinal));
    }
}
