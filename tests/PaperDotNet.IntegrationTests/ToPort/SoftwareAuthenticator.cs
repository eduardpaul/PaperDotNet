using System.Buffers.Text;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaperDotNet.IntegrationTests;

/// <summary>
/// A minimal WebAuthn authenticator (ES256, "none" attestation) for tests: answers
/// creation and request options like a browser + platform authenticator would.
/// </summary>
internal sealed class SoftwareAuthenticator(string origin = "http://localhost") : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _credentialId = RandomNumberGenerator.GetBytes(16);
    private byte[] _userHandle = [];
    private uint _signCount;

    public string CredentialId => Base64Url.EncodeToString(_credentialId);

    /// <summary>Answers <c>navigator.credentials.create()</c> options.</summary>
    public JsonObject Create(JsonElement options)
    {
        var rpId = options.GetProperty("rp").GetProperty("id").GetString()!;
        _userHandle = Base64Url.DecodeFromChars(options.GetProperty("user").GetProperty("id").GetString()!);
        var clientData = ClientData("webauthn.create", options.GetProperty("challenge").GetString()!);

        var parameters = _key.ExportParameters(false);
        var coseKey = new CborWriter();
        coseKey.WriteStartMap(5);
        coseKey.WriteInt32(1);
        coseKey.WriteInt32(2); // kty: EC2
        coseKey.WriteInt32(3);
        coseKey.WriteInt32(-7); // alg: ES256
        coseKey.WriteInt32(-1);
        coseKey.WriteInt32(1); // crv: P-256
        coseKey.WriteInt32(-2);
        coseKey.WriteByteString(parameters.Q.X!);
        coseKey.WriteInt32(-3);
        coseKey.WriteByteString(parameters.Q.Y!);
        coseKey.WriteEndMap();

        var authData = new List<byte>();
        authData.AddRange(SHA256.HashData(Encoding.UTF8.GetBytes(rpId)));
        authData.Add(0x01 | 0x04 | 0x40); // user present, user verified, attested credential data
        authData.AddRange(Counter(_signCount));
        authData.AddRange(new byte[16]); // AAGUID
        authData.Add((byte)(_credentialId.Length >> 8));
        authData.Add((byte)_credentialId.Length);
        authData.AddRange(_credentialId);
        authData.AddRange(coseKey.Encode());

        var attestation = new CborWriter();
        attestation.WriteStartMap(3);
        attestation.WriteTextString("fmt");
        attestation.WriteTextString("none");
        attestation.WriteTextString("attStmt");
        attestation.WriteStartMap(0);
        attestation.WriteEndMap();
        attestation.WriteTextString("authData");
        attestation.WriteByteString([.. authData]);
        attestation.WriteEndMap();

        return new JsonObject
        {
            ["id"] = CredentialId,
            ["rawId"] = CredentialId,
            ["type"] = "public-key",
            ["authenticatorAttachment"] = "platform",
            ["clientExtensionResults"] = new JsonObject(),
            ["response"] = new JsonObject
            {
                ["clientDataJSON"] = Base64Url.EncodeToString(clientData),
                ["attestationObject"] = Base64Url.EncodeToString(attestation.Encode()),
                ["transports"] = new JsonArray("internal"),
            },
        };
    }

    /// <summary>Answers <c>navigator.credentials.get()</c> options.</summary>
    public JsonObject Get(JsonElement options)
    {
        var rpId = options.GetProperty("rpId").GetString()!;
        var clientData = ClientData("webauthn.get", options.GetProperty("challenge").GetString()!);
        _signCount++;
        byte[] authData = [.. SHA256.HashData(Encoding.UTF8.GetBytes(rpId)), 0x01 | 0x04, .. Counter(_signCount)];
        var signature = _key.SignData([.. authData, .. SHA256.HashData(clientData)], HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        return new JsonObject
        {
            ["id"] = CredentialId,
            ["rawId"] = CredentialId,
            ["type"] = "public-key",
            ["authenticatorAttachment"] = "platform",
            ["clientExtensionResults"] = new JsonObject(),
            ["response"] = new JsonObject
            {
                ["clientDataJSON"] = Base64Url.EncodeToString(clientData),
                ["authenticatorData"] = Base64Url.EncodeToString(authData),
                ["signature"] = Base64Url.EncodeToString(signature),
                ["userHandle"] = Base64Url.EncodeToString(_userHandle),
            },
        };
    }

    public void Dispose() => _key.Dispose();

    private byte[] ClientData(string type, string challenge) =>
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["type"] = type, ["challenge"] = challenge, ["origin"] = origin, ["crossOrigin"] = false });

    private static byte[] Counter(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
}
