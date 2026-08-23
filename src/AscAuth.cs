using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

/// <summary>
/// The App Store Connect bearer token, signed locally.
///
/// Apple takes an ES256 JWT made from the .p8 key of an api key: 'kid' in the header, the issuer
/// and a short lifetime in the payload. The token is cached and re-signed when fewer than two
/// minutes of its fifteen remain, so one instance serves a whole run.
/// </summary>
public class AscAuth
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RenewMargin = TimeSpan.FromMinutes(2);

    private readonly string _keyId;
    private readonly string _issuerId;
    private readonly string _privateKeyPem;

    private string _token = "";
    private DateTimeOffset _expires = DateTimeOffset.MinValue;

    /// <summary>privateKeyPem is the content of the .p8 file, the 'BEGIN PRIVATE KEY' block</summary>
    public AscAuth(string keyId, string issuerId, string privateKeyPem)
    {
        _keyId = keyId;
        _issuerId = issuerId;

        // a key pasted without its BEGIN/END lines still works: put them back
        _privateKeyPem = privateKeyPem.Contains("PRIVATE KEY")
            ? privateKeyPem
            : $"-----BEGIN PRIVATE KEY-----\n{privateKeyPem.Trim()}\n-----END PRIVATE KEY-----";
    }

    /// <summary>the current jwt, re-signed when it is about to expire</summary>
    public string Token
    {
        get
        {
            if (_token.Length == 0 || DateTimeOffset.UtcNow >= _expires - RenewMargin)
                Sign();

            return _token;
        }
    }

    private void Sign()
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now + Lifetime;

        var header = new JsonObject
        {
            ["alg"] = "ES256",
            ["kid"] = _keyId,
            ["typ"] = "JWT",
        };

        var payload = new JsonObject
        {
            ["iss"] = _issuerId,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = expires.ToUnixTimeSeconds(),
            ["aud"] = "appstoreconnect-v1",
        };

        var signingInput = Base64Url(Encoding.UTF8.GetBytes(header.ToJsonString()))
            + "." + Base64Url(Encoding.UTF8.GetBytes(payload.ToJsonString()));

        using var key = ECDsa.Create();
        key.ImportFromPem(_privateKeyPem);

        // ES256 wants the raw r||s pair, not the DER sequence SignData produces by default
        var signature = key.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation
        );

        _token = signingInput + "." + Base64Url(signature);
        _expires = expires;
    }

    /// <summary>base64 the way jwt wants it: url safe alphabet, no padding</summary>
    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
