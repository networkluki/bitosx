using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BitosX.LiveTests;

/// <summary>
/// A test against the live stack. Without its configuration it is reported as skipped with the
/// reason <see cref="LiveStack.NotRunReason"/> — never as passed, which would say that something was
/// checked. Uses the <see cref="FactAttribute.Skip"/> of xunit 2, decided when the tests are
/// discovered, as the repository's other conditional facts do.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (!LiveStack.IsConfigured)
        {
            Skip = LiveStack.NotRunReason;
        }
    }
}

/// <summary>
/// The live stack the tests run against, as the client variables of bitosx-api's
/// <c>tools/devstack</c> describe it: the API's origin and the command that reads a mail token from
/// its outbox. Nothing else about the stack is known here.
/// </summary>
internal static class LiveStack
{
    public const string ApiUrlVariable = "BITOSX_LIVE_API_URL";
    public const string MailCommandVariable = "BITOSX_LIVE_MAIL_CMD";
    public const string WebOriginVariable = "BITOSX_LIVE_WEB_ORIGIN";
    public const string NotRunReason = "not run: set BITOSX_LIVE_API_URL and BITOSX_LIVE_MAIL_CMD";

    /// <summary>The account passwords of the tests: long enough for the server's policy (12 to 128).</summary>
    public static string NewPassword() => "live-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

    /// <summary>A fresh address for every account: the database is shared and never assumed empty.</summary>
    public static string NewEmail() => "e2e-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant() + "@example.test";

    private static string? ApiUrl => Environment.GetEnvironmentVariable(ApiUrlVariable);

    private static string? MailCommand => Environment.GetEnvironmentVariable(MailCommandVariable);

    /// <summary>
    /// Whether the live tests run. Only when neither variable is set are they skipped: one without
    /// the other is a mistake in the setup, and the tests then fail and say so rather than pretend.
    /// </summary>
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(ApiUrl) || !string.IsNullOrWhiteSpace(MailCommand);

    /// <summary>The API's origin. Fails unless both variables are set, and unless the API is on this machine.</summary>
    public static Uri Api
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ApiUrl) || string.IsNullOrWhiteSpace(MailCommand))
            {
                throw new InvalidOperationException(
                    $"The live tests are half configured: set both {ApiUrlVariable} and {MailCommandVariable} (or neither, to skip them).");
            }

            var uri = new Uri(ApiUrl.TrimEnd('/') + "/", UriKind.Absolute);

            // These tests create and delete accounts. They are for a stack of one's own, never a shared server.
            if (!uri.IsLoopback)
            {
                throw new InvalidOperationException($"{ApiUrlVariable} must be an API on this machine (loopback), not {uri.Host}.");
            }

            return uri;
        }
    }

    /// <summary>
    /// The web vault's origin as the stack configures it (<see cref="WebOriginVariable"/>, which the stack's
    /// client variables include): the origin a passkey is registered from, as a browser there would.
    /// Needed only by the passkey test; fails, and says so, when it is not set.
    /// </summary>
    public static Uri WebOrigin
    {
        get
        {
            string? value = Environment.GetEnvironmentVariable(WebOriginVariable);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    $"The passkey test needs {WebOriginVariable}, the web vault origin of the stack (tools/devstack/.state/env has it).");
            }

            var uri = new Uri(value.Trim(), UriKind.Absolute);
            if (!uri.IsLoopback)
            {
                throw new InvalidOperationException($"{WebOriginVariable} must be an origin on this machine (loopback), not {uri.Host}.");
            }

            return uri;
        }
    }

    /// <summary>A client for the calls the desktop application never makes: registration, email verification, TOTP enrollment.</summary>
    public static HttpClient RawClient()
    {
        var http = new HttpClient { BaseAddress = Api, Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return http;
    }

    /// <summary>
    /// The token of the latest pending mail of <paramref name="kind"/> (<c>verify</c> or <c>reset</c>)
    /// for <paramref name="email"/>, read with the stack's mail command: what follows <c>#token=</c>
    /// in the link the mail carries.
    /// </summary>
    public static async Task<string> MailTokenAsync(string kind, string email)
    {
        _ = Api; // both variables, and a loopback API
        var start = new ProcessStartInfo(MailCommand!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("latest");
        start.ArgumentList.Add(kind);
        start.ArgumentList.Add(email);

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("The mail command did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        string token = (await output.ConfigureAwait(false)).Trim();
        string complaint = (await error.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0 || token.Length == 0 || token.Contains('\n', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"The mail command found no {kind} token for the test account (exit {process.ExitCode}): {complaint}"));
        }

        return token;
    }

    /// <summary>Registers a fresh account and verifies it with the token from the outbox, which sets its password.</summary>
    public static async Task<(string Email, string Password)> NewAccountAsync()
    {
        string email = NewEmail();
        string password = NewPassword();
        using HttpClient http = RawClient();
        await ExpectAsync(http.PostAsJsonAsync("v1/auth/register", new { email }), HttpStatusCode.Accepted, "register").ConfigureAwait(false);
        string token = await MailTokenAsync("verify", email).ConfigureAwait(false);
        await ExpectAsync(http.PostAsJsonAsync("v1/auth/verify-email", new { token, password }), HttpStatusCode.OK, "verify-email").ConfigureAwait(false);
        return (email, password);
    }

    /// <summary>
    /// Turns on two-factor authentication for the account, as vault.bitosx.se does (the desktop only
    /// shows the state): enroll with the password, confirm with a first code. Returns the
    /// authenticator and the ten recovery codes.
    /// </summary>
    public static async Task<(Totp Authenticator, IReadOnlyList<string> RecoveryCodes)> EnableTwoFactorAsync(string email, string password)
    {
        using HttpClient http = RawClient();
        JsonObject login = await ExpectAsync(http.PostAsJsonAsync("v1/auth/login", new { email, password }), HttpStatusCode.OK, "login").ConfigureAwait(false);
        string access = (string)login["accessToken"]!;

        using var enroll = new HttpRequestMessage(HttpMethod.Post, "v1/account/totp/enroll") { Content = JsonContent.Create(new { password }) };
        enroll.Headers.Authorization = new("Bearer", access);
        JsonObject enrolled = await ExpectAsync(http.SendAsync(enroll), HttpStatusCode.OK, "TOTP enroll").ConfigureAwait(false);
        if ((int)enrolled["digits"]! != 6 || (int)enrolled["periodSeconds"]! != 30)
        {
            throw new InvalidOperationException("The server's TOTP is not 6 digits every 30 seconds, which Totp assumes.");
        }

        var totp = new Totp((string)enrolled["secret"]!);
        using var confirm = new HttpRequestMessage(HttpMethod.Post, "v1/account/totp/confirm")
        {
            Content = JsonContent.Create(new { code = await totp.NextCodeAsync().ConfigureAwait(false) }),
        };
        confirm.Headers.Authorization = new("Bearer", access);
        JsonObject confirmed = await ExpectAsync(http.SendAsync(confirm), HttpStatusCode.OK, "TOTP confirm").ConfigureAwait(false);
        string[] recovery = [.. confirmed["recoveryCodes"]!.AsArray().Select(c => (string)c!)];

        using var logout = new HttpRequestMessage(HttpMethod.Post, "v1/auth/logout");
        logout.Headers.Authorization = new("Bearer", access);
        await http.SendAsync(logout).ConfigureAwait(false);
        return (totp, recovery);
    }

    /// <summary>
    /// Registers a passkey for an account that has two-factor on, as the web vault does in a browser
    /// (the desktop only shows how many there are): sign in with <paramref name="signInCode"/>, ask for
    /// creation options with the password and <paramref name="confirmCode"/>, answer them with
    /// <paramref name="authenticator"/> from <see cref="WebOrigin"/>, and register the result as
    /// <paramref name="name"/>. Either code may be a TOTP or a recovery code.
    /// </summary>
    public static async Task RegisterPasskeyAsync(
        string email, string password, string signInCode, string confirmCode, SoftwareAuthenticator authenticator, string name)
    {
        ArgumentNullException.ThrowIfNull(authenticator);
        Uri origin = WebOrigin;
        using HttpClient http = RawClient();
        JsonObject challenge = await ExpectAsync(http.PostAsJsonAsync("v1/auth/login", new { email, password }), HttpStatusCode.Accepted, "login").ConfigureAwait(false);
        JsonObject login = await ExpectAsync(
            http.PostAsJsonAsync("v1/auth/login/totp", new { mfaToken = (string)challenge["mfaToken"]!, code = signInCode }), HttpStatusCode.OK, "login/totp").ConfigureAwait(false);
        string access = (string)login["accessToken"]!;

        using var begin = new HttpRequestMessage(HttpMethod.Post, "v1/account/passkeys/registration/options") { Content = JsonContent.Create(new { password, code = confirmCode }) };
        begin.Headers.Authorization = new("Bearer", access);
        JsonObject ceremony = await ExpectAsync(http.SendAsync(begin), HttpStatusCode.OK, "passkey registration options").ConfigureAwait(false);
        JsonObject options = ceremony["options"]?.AsObject() ?? throw new InvalidOperationException("The registration options carry no options.");

        JsonObject credential = authenticator.Create(options, origin);
        using var finish = new HttpRequestMessage(HttpMethod.Post, "v1/account/passkeys/registration")
        {
            Content = JsonContent.Create(new JsonObject
            {
                ["registrationId"] = (string)ceremony["registrationId"]!,
                ["name"] = name,
                ["credential"] = credential,
            }),
        };
        finish.Headers.Authorization = new("Bearer", access);
        await ExpectAsync(http.SendAsync(finish), HttpStatusCode.Created, "passkey registration").ConfigureAwait(false);

        using var logout = new HttpRequestMessage(HttpMethod.Post, "v1/auth/logout");
        logout.Headers.Authorization = new("Bearer", access);
        await http.SendAsync(logout).ConfigureAwait(false);
    }

    private static async Task<JsonObject> ExpectAsync(Task<HttpResponseMessage> call, HttpStatusCode expected, string what)
    {
        using HttpResponseMessage response = await call.ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (response.StatusCode != expected)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"{what}: expected HTTP {(int)expected}, got {(int)response.StatusCode}: {body[..Math.Min(body.Length, 300)]}"));
        }

        return body.Length > 0 && JsonNode.Parse(body) is JsonObject json ? json : [];
    }
}

/// <summary>
/// An authenticator app: RFC 6238 with HMAC-SHA1, 6 digits and 30-second steps, as the server says it
/// uses. The server accepts one step either side and refuses a step that was used once, so every code
/// handed out is for a step after the last one, waiting for the clock when that is needed.
/// </summary>
internal sealed class Totp(string base32Secret)
{
    private readonly byte[] _key = Base32(base32Secret);
    private long _lastStep = long.MinValue;

    private static long CurrentStep => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;

    public async Task<string> NextCodeAsync()
    {
        long step = Math.Max(CurrentStep, _lastStep + 1);
        while (step > CurrentStep + 1)
        {
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }

        _lastStep = step;
        return CodeFor(step);
    }

    /// <summary>A code the server cannot accept now: for a step far outside its window.</summary>
    public string WrongCode() => CodeFor(CurrentStep + 20);

    private string CodeFor(long step)
    {
        byte[] counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counter);
        }

#pragma warning disable CA5350 // RFC 6238 as the server implements it is HMAC-SHA1; this is the authenticator side.
        byte[] mac = HMACSHA1.HashData(_key, counter);
#pragma warning restore CA5350
        int offset = mac[^1] & 0x0F;
        int binary = ((mac[offset] & 0x7F) << 24) | (mac[offset + 1] << 16) | (mac[offset + 2] << 8) | mac[offset + 3];
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private static byte[] Base32(string text)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0;
        int bits = 0;
        foreach (char c in text.TrimEnd('=').ToUpperInvariant())
        {
            int value = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (value < 0)
            {
                throw new FormatException("The TOTP secret is not Base32.");
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)(buffer >> bits));
                buffer &= (1 << bits) - 1;
            }
        }

        return [.. bytes];
    }
}
