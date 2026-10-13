using System.Security.Cryptography;
using System.Text;
using OnvifSim.World;

namespace OnvifSim.Streaming.Rtsp;

// HTTP-style authentication for RTSP (RFC 2326 section 14.2 via RFC 2617): Digest MD5 and Basic, checked
// against the camera's users. The Digest challenge offers no qop because live555-based clients (VLC)
// do not implement it; a client that answers with qop=auth anyway is still verified correctly.
//
// Nonces are stateless: random bytes plus an HMAC under a per-process key, so any connection may reuse
// a nonce it received earlier and nothing has to be remembered or expired. Replay protection is not a
// goal for a simulator.
public sealed class RtspAuthenticator
{
    public const string Realm = "OnvifSim";

    private const int NonceRandomBytes = 16;
    private const int NonceMacBytes = 8;

    private readonly byte[] _nonceKey = RandomNumberGenerator.GetBytes(32);

    // The WWW-Authenticate values of a 401 response, strongest first.
    public IEnumerable<string> Challenges() =>
    [
        $"Digest realm=\"{Realm}\", nonce=\"{NewNonce()}\", algorithm=MD5",
        $"Basic realm=\"{Realm}\"",
    ];

    // True when the camera needs no credentials or the Authorization header matches one of its users.
    public bool IsAuthorized(string method, string? authorization, IReadOnlyList<UserModel> users)
    {
        if (users.Count == 0) return true;
        if (string.IsNullOrWhiteSpace(authorization)) return false;

        int space = authorization.IndexOf(' ');
        if (space <= 0) return false;
        var scheme = authorization[..space];
        var credentials = authorization[(space + 1)..].Trim();
        if (scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase))
            return IsValidDigest(method, ParseParameters(credentials), users);
        if (scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase))
            return IsValidBasic(credentials, users);
        return false;
    }

    private bool IsValidDigest(string method, Dictionary<string, string> parameters, IReadOnlyList<UserModel> users)
    {
        if (!parameters.TryGetValue("username", out var username)
            || !parameters.TryGetValue("nonce", out var nonce)
            || !parameters.TryGetValue("uri", out var uri)
            || !parameters.TryGetValue("response", out var response)
            || parameters.GetValueOrDefault("realm") != Realm
            || !IsOurNonce(nonce))
            return false;
        if (parameters.TryGetValue("algorithm", out var algorithm) && !algorithm.Equals("MD5", StringComparison.OrdinalIgnoreCase))
            return false;

        var user = users.FirstOrDefault(u => u.Username == username);
        if (user == null) return false;

        string ha1 = Md5Hex($"{user.Username}:{Realm}:{user.Password}");
        string ha2 = Md5Hex($"{method}:{uri}");
        string expected = parameters.TryGetValue("qop", out var qop)
            ? Md5Hex($"{ha1}:{nonce}:{parameters.GetValueOrDefault("nc")}:{parameters.GetValueOrDefault("cnonce")}:{qop}:{ha2}")
            : Md5Hex($"{ha1}:{nonce}:{ha2}");
        return FixedTimeEquals(expected, response.ToLowerInvariant());
    }

    private static bool IsValidBasic(string credentials, IReadOnlyList<UserModel> users)
    {
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(credentials));
        }
        catch (FormatException)
        {
            return false;
        }
        int colon = decoded.IndexOf(':');
        if (colon < 0) return false;
        var username = decoded[..colon];
        var password = decoded[(colon + 1)..];
        var user = users.FirstOrDefault(u => u.Username == username);
        return user != null && FixedTimeEquals(user.Password, password);
    }

    private string NewNonce()
    {
        var random = RandomNumberGenerator.GetBytes(NonceRandomBytes);
        return Convert.ToHexStringLower(random) + Convert.ToHexStringLower(NonceMac(random));
    }

    private bool IsOurNonce(string nonce)
    {
        if (nonce.Length != 2 * (NonceRandomBytes + NonceMacBytes)) return false;
        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(nonce);
        }
        catch (FormatException)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(NonceMac(bytes.AsSpan(0, NonceRandomBytes)), bytes.AsSpan(NonceRandomBytes));
    }

    private byte[] NonceMac(ReadOnlySpan<byte> random) => HMACSHA256.HashData(_nonceKey, random)[..NonceMacBytes];

    // Parses `name=value, name="quoted, value"` lists; names are case-insensitive.
    private static Dictionary<string, string> ParseParameters(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int pos = 0;
        while (pos < text.Length)
        {
            while (pos < text.Length && (text[pos] == ',' || char.IsWhiteSpace(text[pos]))) pos++;
            int equals = text.IndexOf('=', pos);
            if (equals < 0) break;
            var name = text[pos..equals].Trim();
            pos = equals + 1;
            string value;
            if (pos < text.Length && text[pos] == '"')
            {
                int close = text.IndexOf('"', pos + 1);
                if (close < 0) close = text.Length;
                value = text[(pos + 1)..close];
                pos = close + 1;
            }
            else
            {
                int comma = text.IndexOf(',', pos);
                if (comma < 0) comma = text.Length;
                value = text[pos..comma].Trim();
                pos = comma;
            }
            result[name] = value;
        }
        return result;
    }

    private static string Md5Hex(string text) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text)));

    private static bool FixedTimeEquals(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
}
