using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace BitosX.LiveTests;

/// <summary>
/// A passkey authenticator in software, for registering a passkey over plain HTTP the way a browser on
/// the web vault does (the desktop has no WebAuthn): one ES256 (P-256) key, a discoverable credential,
/// user presence and user verification asserted, attestation <c>none</c>. It answers
/// <c>navigator.credentials.create</c> with the WebAuthn JSON serialization (base64url strings) that
/// <c>PublicKeyCredential.toJSON()</c> produces. Registration only: the desktop never signs in with it.
/// </summary>
internal sealed class SoftwareAuthenticator : IDisposable
{
    private const int Es256 = -7;

    // Authenticator data flags (WebAuthn §6.1): user present, user verified, attested credential data.
    private const byte UserPresent = 0x01;
    private const byte UserVerified = 0x04;
    private const byte AttestedCredentialData = 0x40;

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(32);

    /// <summary>
    /// The new credential for the server's creation options (<c>PublicKeyCredentialCreationOptionsJSON</c>),
    /// as a page at <paramref name="origin"/> would hand it back. Fails if the options do not allow ES256.
    /// </summary>
    public JsonObject Create(JsonObject options, Uri origin)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(origin);

        string challenge = (string?)options["challenge"] ?? throw new InvalidOperationException("The creation options carry no challenge.");
        string rpId = (string?)options["rp"]?["id"] ?? origin.Host; // absent: the origin's own host, as a browser takes it
        bool es256 = options["pubKeyCredParams"]?.AsArray().Any(p => (string?)p?["type"] == "public-key" && (int?)p?["alg"] == Es256) == true;
        if (!es256)
        {
            throw new InvalidOperationException("The creation options do not allow ES256, the only algorithm of this authenticator.");
        }

        string clientData = new JsonObject
        {
            ["type"] = "webauthn.create",
            ["challenge"] = challenge,
            ["origin"] = origin.GetLeftPart(UriPartial.Authority),
            ["crossOrigin"] = false,
        }.ToJsonString();

        byte[] authenticatorData = AuthenticatorData(rpId);
        byte[] attestationObject = Cbor.Map(
            (Cbor.Text("fmt"), Cbor.Text("none")),
            (Cbor.Text("attStmt"), Cbor.Map()),
            (Cbor.Text("authData"), Cbor.Bytes(authenticatorData)));

        string id = Base64Url(CredentialId);
        return new JsonObject
        {
            ["id"] = id,
            ["rawId"] = id,
            ["type"] = "public-key",
            ["authenticatorAttachment"] = "platform",
            ["response"] = new JsonObject
            {
                ["clientDataJSON"] = Base64Url(Encoding.UTF8.GetBytes(clientData)),
                ["attestationObject"] = Base64Url(attestationObject),
                ["transports"] = new JsonArray("internal"),
            },
            ["clientExtensionResults"] = new JsonObject(),
        };
    }

    /// <summary>rpIdHash, flags, a zero signature counter, and the attested credential: no AAGUID, the id, the COSE key.</summary>
    private byte[] AuthenticatorData(string rpId)
    {
        ECParameters key = _key.ExportParameters(includePrivateParameters: false);
        byte[] coseKey = Cbor.Map(
            (Cbor.Int(1), Cbor.Int(2)),         // kty: EC2
            (Cbor.Int(3), Cbor.Int(Es256)),     // alg: ES256
            (Cbor.Int(-1), Cbor.Int(1)),        // crv: P-256
            (Cbor.Int(-2), Cbor.Bytes(key.Q.X!)),
            (Cbor.Int(-3), Cbor.Bytes(key.Q.Y!)));

        using var data = new MemoryStream();
        data.Write(SHA256.HashData(Encoding.UTF8.GetBytes(rpId)));
        data.WriteByte(UserPresent | UserVerified | AttestedCredentialData);
        data.Write([0, 0, 0, 0]);       // signature counter
        data.Write(new byte[16]);      // AAGUID: none
        data.WriteByte((byte)(CredentialId.Length >> 8));
        data.WriteByte((byte)CredentialId.Length);
        data.Write(CredentialId);
        data.Write(coseKey);
        return data.ToArray();
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose() => _key.Dispose();

    /// <summary>The little of CBOR (RFC 8949) a "none" attestation needs, in the canonical order the items are given in.</summary>
    private static class Cbor
    {
        public static byte[] Int(int value)
            => value >= 0 ? Head(0, (ulong)value) : Head(1, (ulong)(-1 - value));

        public static byte[] Text(string value) => [.. Head(3, (ulong)Encoding.UTF8.GetByteCount(value)), .. Encoding.UTF8.GetBytes(value)];

        public static byte[] Bytes(byte[] value) => [.. Head(2, (ulong)value.Length), .. value];

        public static byte[] Map(params (byte[] Key, byte[] Value)[] entries)
        {
            var map = new List<byte>(Head(5, (ulong)entries.Length));
            foreach ((byte[] key, byte[] value) in entries)
            {
                map.AddRange(key);
                map.AddRange(value);
            }

            return [.. map];
        }

        private static byte[] Head(int major, ulong argument)
        {
            byte type = (byte)(major << 5);
            return argument switch
            {
                < 24 => [(byte)(type | (byte)argument)],
                <= byte.MaxValue => [(byte)(type | 24), (byte)argument],
                <= ushort.MaxValue => [(byte)(type | 25), (byte)(argument >> 8), (byte)argument],
                _ => throw new ArgumentOutOfRangeException(nameof(argument), "Not needed for an attestation."),
            };
        }
    }
}
