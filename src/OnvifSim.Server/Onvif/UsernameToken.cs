using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using OnvifSim.World;

namespace OnvifSim.Onvif;

// WS-Security UsernameToken (OASIS Username Token Profile 1.0), the authentication ONVIF clients send in
// the SOAP header: PasswordDigest = Base64(SHA-1(nonce + created + password)), or the plain password.
//
// Like the lab camera this simulator imitates, it neither remembers nonces nor insists on an exact
// clock: OnvifLib reuses one token for a minute, and that camera accepts tokens created 15 minutes
// off. Tokens further off than ClockTolerance are refused.
public static class UsernameToken
{
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(15);

    private const string Wsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    private const string Wsu = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";
    private const string PasswordTextType = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordText";

    // True when the wsse:Security header carries a token of one of the users.
    public static bool IsValid(XElement security, IReadOnlyList<UserModel> users, DateTime utcNow)
    {
        var token = security.Element(XName.Get("UsernameToken", Wsse));
        var username = token?.Element(XName.Get("Username", Wsse))?.Value.Trim();
        var password = token?.Element(XName.Get("Password", Wsse));
        if (username == null || password == null) return false;
        var user = users.FirstOrDefault(u => u.Username == username);
        if (user == null) return false;

        var type = password.Attribute("Type")?.Value;
        if (type == null || type == PasswordTextType)
            return FixedTimeEquals(password.Value, user.Password);

        var created = token!.Element(XName.Get("Created", Wsu))?.Value.Trim() ?? "";
        if (created.Length > 0 && !IsWithinClockTolerance(created, utcNow)) return false;
        byte[] nonce;
        try
        {
            nonce = Convert.FromBase64String(token.Element(XName.Get("Nonce", Wsse))?.Value.Trim() ?? "");
        }
        catch (FormatException)
        {
            return false;
        }
        var expected = Convert.ToBase64String(SHA1.HashData([.. nonce, .. Encoding.UTF8.GetBytes(created + user.Password)]));
        return FixedTimeEquals(password.Value.Trim(), expected);
    }

    private static bool IsWithinClockTolerance(string created, DateTime utcNow) =>
        DateTime.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time)
        && (time - utcNow).Duration() <= ClockTolerance;

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
