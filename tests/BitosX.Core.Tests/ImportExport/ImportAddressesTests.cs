using System.Text.RegularExpressions;
using BitosX.Core.ImportExport;

namespace BitosX.Core.Tests.ImportExport;

/// <summary>
/// How an address value is told apart: a web address, a host that is given https://, or something
/// BitosX cannot store. The rules are written by hand in the library; here they are held against
/// the patterns the specification gives.
/// </summary>
public sealed partial class ImportAddressesTests
{
    // The patterns of docs/IMPORT_EXPORT.md, as a regular expression engine reads them.
    private const string Label = "[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?";
    private const string Tld = "[A-Za-z](?:[A-Za-z0-9-]*[A-Za-z0-9])?";
    private const string Octet = "(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])";
    private const string HostPattern =
        @"\A(?:(?:" + Label + @"\.)+" + Tld + "|" + Octet + @"(?:\." + Octet + @"){3})(?::[0-9]{1,5})?(?:[/?#][\s\S]*)?\z";

    private const string SchemePattern = @"\A([A-Za-z][A-Za-z0-9+.-]*)://";

    [GeneratedRegex(HostPattern, RegexOptions.CultureInvariant)]
    private static partial Regex HostRegex();

    [GeneratedRegex(SchemePattern, RegexOptions.CultureInvariant)]
    private static partial Regex SchemeRegex();

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com/login?next=%2F#top")]
    [InlineData("HTTPS://EXAMPLE.COM/Path")]
    [InlineData("hTtP://intranet")]
    [InlineData("https://user:secret@example.com:8443/")]
    [InlineData("https://[::1]:8080/")]
    [InlineData("http://")]
    [InlineData("https://not a host")]
    public void WithTheSchemeHttpOrHttps_ItIsAWebAddress_WhateverFollows(string value)
    {
        // Whether it is a valid one is the vault's rule, and is asked afterwards.
        Assert.Equal(AddressKind.Web, ImportAddresses.Classify(value));
    }

    [Theory]
    [InlineData("android://Hash==@com.example.app/")]
    [InlineData("androidapp://com.example.app")]
    [InlineData("ftp://files.example.com/")]
    [InlineData("chrome://FirefoxAccounts")]
    [InlineData("file:///home/user/secrets.txt")]
    [InlineData("ssh://git@example.com/repo.git")]
    [InlineData("custom+scheme.v2-x://thing")]
    [InlineData("httpss://example.com")]
    [InlineData("example.com://what")]
    public void WithAnyOtherScheme_ItIsNotAnAddressBitosXStores(string value)
    {
        Assert.Equal(AddressKind.Other, ImportAddresses.Classify(value));
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("www.example.com")]
    [InlineData("example.com:8443/path")]
    [InlineData("example.com/login")]
    [InlineData("example.com?x=1")]
    [InlineData("example.com#top")]
    [InlineData("sub.domain.example.co.uk/a b?c=d#e")]
    [InlineData("EXAMPLE.COM")]
    [InlineData("my-site.example")]
    [InlineData("a.b")]
    [InlineData("1password.com")]
    [InlineData("example.xn--p1ai")]
    [InlineData("xn--bcher-kva.de")]
    [InlineData("192.168.1.1")]
    [InlineData("192.168.1.1:8080")]
    [InlineData("10.0.0.1/admin")]
    [InlineData("255.255.255.255")]
    [InlineData("0.0.0.0")]
    [InlineData("example.com:1")]
    [InlineData("example.com:65535")]
    [InlineData("example.com:99999")]
    public void WithoutAScheme_WhatLooksLikeAHost_IsGivenHttps(string value)
    {
        Assert.Equal(AddressKind.HostWithoutScheme, ImportAddresses.Classify(value));
        Assert.Equal("https://" + value, ImportAddresses.WithHttps(value));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("localhost:3000")]
    [InlineData("intranet")]
    [InlineData("My bank")]
    [InlineData("n/a")]
    [InlineData("user@example.com")]
    [InlineData("user:secret@example.com")]
    [InlineData("mailto:user@example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,x")]
    [InlineData("about:blank")]
    [InlineData("http:example.com")]
    [InlineData("//example.com/path")]
    [InlineData("/just/a/path")]
    [InlineData("C:\\Users\\me\\file")]
    [InlineData("v1.2")]
    [InlineData("1.2.3")]
    [InlineData("1.2.3.4.5")]
    [InlineData("256.1.1.1")]
    [InlineData("01.2.3.4")]
    [InlineData("1.2.3.")]
    [InlineData("example.com.")]
    [InlineData(".example.com")]
    [InlineData("example..com")]
    [InlineData("-example.com")]
    [InlineData("example-.com")]
    [InlineData("exa_mple.com")]
    [InlineData("example.c0m-")]
    [InlineData("example.123")]
    [InlineData("example.com:")]
    [InlineData("example.com:port")]
    [InlineData("example.com:123456")]
    [InlineData("example.com:80x")]
    [InlineData("example.com:80:90")]
    [InlineData("example.com path")]
    [InlineData("example .com")]
    [InlineData("[::1]")]
    [InlineData("::1")]
    public void WithoutAScheme_WhatDoesNotLookLikeAHost_IsNotAnAddressBitosXStores(string value)
    {
        Assert.Equal(AddressKind.Other, ImportAddresses.Classify(value));
    }

    [Theory]
    [InlineData("b\u00FCcher.de")]
    [InlineData("r\u00E4ksm\u00F6rg\u00E5s.se")]
    [InlineData("\u4F8B\u3048.jp")]
    public void AHostWithLettersOutsideAscii_NeedsItsScheme(string host)
    {
        // Without one it is not taken for a host; with one it is a web address as it stands.
        Assert.Equal(AddressKind.Other, ImportAddresses.Classify(host));
        Assert.Equal(AddressKind.Web, ImportAddresses.Classify("https://" + host));
    }

    [Theory]
    [InlineData("https://example.com/a\tb")]
    [InlineData("https://exam\tple.com")]
    [InlineData("example.com/\u0000")]
    [InlineData("example.com\u007F")]
    [InlineData("\u0001https://example.com")]
    public void AValueWithAControlCharacter_IsNeverAWebAddress(string value)
    {
        // The two clients' own address rules disagree about these; here they never get that far.
        Assert.Equal(AddressKind.Other, ImportAddresses.Classify(value));
    }

    [Fact]
    public void AHostMayBeFollowedByAnything_OnceThePathHasBegun()
    {
        // Including characters a regular expression's dot would stop at.
        Assert.Equal(AddressKind.HostWithoutScheme, ImportAddresses.Classify("example.com/a\u2028b\u2029c\u0085d"));
        Assert.Equal(AddressKind.HostWithoutScheme, ImportAddresses.Classify("example.com/\u65E5\u672C \uD83D\uDE00"));
    }

    [Fact]
    public void TheHandWrittenRules_AgreeWithThePatternsOfTheSpecification()
    {
        // Every string of up to four of these characters: some 70,000 values.
        char[] alphabet = ['a', 'Z', '0', '9', '2', '5', '.', '-', ':', '/', '?', '#', '@', '_', ' ', '+'];
        var values = new List<string> { string.Empty };
        var frontier = new List<string> { string.Empty };
        for (int length = 1; length <= 4; length++)
        {
            var next = new List<string>(frontier.Count * alphabet.Length);
            foreach (string prefix in frontier)
            {
                foreach (char c in alphabet)
                {
                    next.Add(prefix + c);
                }
            }

            values.AddRange(next);
            frontier = next;
        }

        // Longer ones, put together the way real values are: parts of a name with dots between them,
        // then perhaps a port, then perhaps a path. Many are hosts, many are almost hosts.
        string[] labels =
        [
            "a", "ab", "a-b", "a--b", "example", "com", "Co", "x1", "1x", "9", "0", "1", "25", "99", "100", "199",
            "249", "250", "255", "256", "01", "001", "1234", "-a", "a-", "-", "a_b", "", "xn--p1ai", "A0-z",
        ];
        string[] ports = ["", "", "", ":", ":8", ":80", ":8443", ":65535", ":99999", ":123456", ":8x", ":x", ":80:81"];
        string[] tails = ["", "", "", "/", "/path", "?q=1", "#top", "/a b", " ", "x", "@host", "://x", "/a:b@c"];
        string[] fronts = ["", "", "", "", "", "", "http", "https", "ftp", "a+b.c-d", "1a", "+a", " "];
        var random = new Random(20261110);
        for (int i = 0; i < 250_000; i++)
        {
            int count = random.Next(1, 6);
            string value = fronts[random.Next(fronts.Length)];
            if (value.Length > 0 && random.Next(2) == 0)
            {
                value += "://";
            }

            for (int j = 0; j < count; j++)
            {
                value += (j > 0 ? "." : string.Empty) + labels[random.Next(labels.Length)];
            }

            value += ports[random.Next(ports.Length)] + tails[random.Next(tails.Length)];
            values.Add(value);
        }

        int hosts = 0;
        int schemes = 0;
        foreach (string value in values)
        {
            bool expectedHost = HostRegex().IsMatch(value);
            Match scheme = SchemeRegex().Match(value);

            Assert.True(expectedHost == ImportAddresses.LooksLikeHost(value), $"host: '{value}'");
            Assert.True((scheme.Success ? scheme.Groups[1].Length : 0) == ImportAddresses.SchemeLength(value), $"scheme: '{value}'");
            hosts += expectedHost ? 1 : 0;
            schemes += scheme.Success ? 1 : 0;
        }

        // The comparison saw plenty of both answers to both questions.
        Assert.True(hosts > 10_000, $"{hosts} hosts");
        Assert.True(values.Count - hosts > 100_000, $"{values.Count - hosts} that are not hosts");
        Assert.True(schemes > 10_000, $"{schemes} schemes");
    }

    [Theory]
    [InlineData("https://example.com", "example.com")]
    [InlineData("https://EXAMPLE.com/Path", "example.com")]
    [InlineData("http://www.example.com:8080/login?x=1#y", "www.example.com")]
    [InlineData("https://user:secret@example.com:8443/", "example.com")]
    [InlineData("https://a@b@example.com/", "example.com")]
    [InlineData("https://example.com?next=/a/b", "example.com")]
    [InlineData("https://example.com#frag", "example.com")]
    [InlineData("https://192.168.1.1:8080/admin", "192.168.1.1")]
    [InlineData("https://[2001:DB8::1]:8443/x", "[2001:db8::1]")]
    [InlineData("https://[::1]", "[::1]")]
    [InlineData("https://example.com:/x", "example.com")]
    [InlineData("https://intranet", "intranet")]
    [InlineData("https://B\u00DCCHER.de/", "b\u00DCcher.de")]
    [InlineData("https://xn--bcher-kva.de/", "xn--bcher-kva.de")]
    [InlineData("https:///path", "")]
    [InlineData("https://", "")]
    [InlineData("https://@/", "")]
    public void TheHostOfAWebAddress_IsTakenFromItsText(string address, string host)
    {
        // Lower case for A to Z only; nothing is decoded, converted or looked up.
        Assert.Equal(host, ImportAddresses.HostOf(address));
    }

    // ---- The host rule both clients share ----

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com:8080/login?next=%2F")]
    [InlineData("https://user:secret@example.com:8443/p?q#f")]
    [InlineData("https://a:b:c@example.com/")]
    [InlineData("https://example.com@other.example/")]
    [InlineData("https://intranet")]
    [InlineData("http://localhost:3000")]
    [InlineData("https://my_host.example.com/")]
    [InlineData("https://_dmarc.example.com/")]
    [InlineData("https://-a.example/")]
    [InlineData("https://example.com.")]
    [InlineData("https://example.com./path")]
    [InlineData("https://example.com:")]
    [InlineData("https://example.com:0")]
    [InlineData("https://example.com:99999")]          // not a port, but that is for the vault's rule to say
    [InlineData("https://192.168.1.1")]
    [InlineData("https://192.168.1.1:8080/admin")]
    [InlineData("https://1.2.3.4.")]
    [InlineData("https://1e3.example")]
    [InlineData("https://[::1]")]
    [InlineData("https://[::1]:8080/")]
    [InlineData("https://[2001:DB8::1]:")]
    [InlineData("https://[::ffff:1.2.3.4]/")]
    [InlineData("https://xn--bcher-kva.example/")]
    [InlineData("HTTPS://EXAMPLE.COM/Path")]
    [InlineData("https://example.com/a b?c d#e f")]
    [InlineData("https://example.com/back\\slash\\in\\the\\path")]
    [InlineData("https://example.com/%zz{x}|y^z`<>\"'")]
    public void AHostBothClientsReadTheSameWay_IsPlausible(string address)
    {
        Assert.True(ImportAddresses.HasPlausibleHost(address));
    }

    [Theory]
    [InlineData("https://b\u00FCcher.example/")]
    [InlineData("https://\u65E5\u672C\u8A9E.example/")]
    [InlineData("https://example.\u65E5\u672C/")]
    public void AHostWithLettersOutsideAscii_IsPlausible_AndLeftToTheVaultsRule(string address)
    {
        Assert.True(ImportAddresses.HasPlausibleHost(address));
    }

    [Theory]
    [InlineData("https://")]                              // no host
    [InlineData("http://")]
    [InlineData("https:///path")]
    [InlineData("https://@/")]
    [InlineData("https://user@")]
    [InlineData("https://:8080")]
    [InlineData("https://user:p@ss@example.com")]        // two @
    [InlineData("https://a@b@c/")]
    [InlineData("https://a..b")]                          // an empty part
    [InlineData("https://.example.com")]
    [InlineData("https://example..com")]
    [InlineData("https://.")]
    [InlineData("https://..")]
    [InlineData("https://example.com..")]
    [InlineData("https://exa mple.com/")]                 // characters a host name does not have
    [InlineData("https://exa!mple.com")]
    [InlineData("https://exa\"mple.com")]
    [InlineData("https://exa'mple.com")]
    [InlineData("https://*.example.com")]
    [InlineData("https://%41.com")]
    [InlineData("https://ex%61mple.com/")]
    [InlineData("https://example.com\\path")]
    [InlineData("https://exam[ple.com")]
    [InlineData("https://example.com:abc")]               // a port that is not digits
    [InlineData("https://example.com:80:90")]
    [InlineData("https://example.com:80a/")]
    [InlineData("https://example.123")]                   // a last part that is a number, and no IPv4 address
    [InlineData("https://v1.2")]
    [InlineData("https://1.2.3")]
    [InlineData("https://1")]
    [InlineData("https://1.2.3.4.5")]
    [InlineData("https://256.1.1.1")]
    [InlineData("https://1.2.3.256")]
    [InlineData("https://01.02.03.04")]
    [InlineData("https://0x7f.1")]
    [InlineData("https://0x7f000001")]
    [InlineData("https://0x.1.1.1")]
    [InlineData("https://2130706433")]
    [InlineData("https://1.2.3.4..")]
    [InlineData("https://[::1")]                          // square brackets that are not an IPv6 address
    [InlineData("https://[]/")]
    [InlineData("https://[zz]/")]
    [InlineData("https://[fe80::1%25eth0]/")]
    [InlineData("https://[::1]x")]
    [InlineData("https://[::1]:80a")]
    [InlineData("not an address at all")]
    public void AHostTheClientsWouldReadDifferently_OrNotAtAll_IsNotPlausible(string address)
    {
        Assert.False(ImportAddresses.HasPlausibleHost(address));
    }

    [Fact]
    public void ClassifyingAVeryLongValue_TakesOnePass()
    {
        // Eight million characters that look like a host until the very last one.
        string almost = string.Concat(Enumerable.Repeat("a-b.", 2_000_000)) + "c_";
        string host = string.Concat(Enumerable.Repeat("a-b.", 2_000_000)) + "com";

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(AddressKind.Other, ImportAddresses.Classify(almost));
        Assert.Equal(AddressKind.HostWithoutScheme, ImportAddresses.Classify(host));
        Assert.Equal(AddressKind.Other, ImportAddresses.Classify(new string('a', 8_000_000)));
        Assert.Equal(AddressKind.Other, ImportAddresses.Classify(new string('a', 8_000_000) + "://x"));
        Assert.False(ImportAddresses.HasPlausibleHost("https://" + string.Concat(Enumerable.Repeat("a.", 4_000_000)) + "9"));
        Assert.True(ImportAddresses.HasPlausibleHost("https://" + string.Concat(Enumerable.Repeat("a.", 4_000_000)) + "com"));
        Assert.False(ImportAddresses.HasPlausibleHost("https://" + new string('@', 8_000_000)));
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Took {watch.Elapsed}.");
    }
}
