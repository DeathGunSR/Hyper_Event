using System.Security.Cryptography;
using System.Text;

namespace EbiCampaign.Api;

public static class Security
{
    public const int MaxClockSkewSeconds = 300;

    public static string Sha256Hex(byte[] data)
    {
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }

    public static string Sha256Hex(string text)
    {
        return Sha256Hex(Encoding.UTF8.GetBytes(text));
    }

    public static string HmacHex(string secret, string canonical)
    {
        using HMACSHA256 h = new(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    /// <summary>رشتهٔ امضا: agentId \n timestamp \n METHOD \n path \n sha256(body)</summary>
    public static string Canonical(string agentId, string timestamp, string method, string path, string bodyHash)
    {
        return agentId + "\n" + timestamp + "\n" + method.ToUpperInvariant() + "\n" + path + "\n" + bodyHash;
    }

    public static bool Verify(string secret, string canonical, string signature)
    {
        if (string.IsNullOrWhiteSpace(signature)) return false;
        string expected = HmacHex(secret, canonical);
        byte[] a = Encoding.ASCII.GetBytes(expected);
        byte[] b = Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    public static bool FixedEquals(string a, string b)
    {
        byte[] x = Encoding.UTF8.GetBytes(a ?? "");
        byte[] y = Encoding.UTF8.GetBytes(b ?? "");
        return x.Length == y.Length && CryptographicOperations.FixedTimeEquals(x, y);
    }

    public static bool TimestampFresh(string raw)
    {
        if (!long.TryParse(raw, out long unix)) return false;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return Math.Abs(now - unix) <= MaxClockSkewSeconds;
    }

    public static string MaskMobile(string? mobile)
    {
        if (string.IsNullOrWhiteSpace(mobile)) return "";
        string m = mobile.Trim();
        if (m.Length < 7) return new string('*', m.Length);
        return m.Substring(0, 4) + "***" + m.Substring(m.Length - 4);
    }

    /// <summary>یکسان‌سازی موبایل ایران مطابق PhoneUtil در برنامه ویندوزی.</summary>
    public static string NormalizeMobile(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        StringBuilder digits = new();
        foreach (char c in raw)
            if (c >= '0' && c <= '9') digits.Append(c);

        string n = digits.ToString();
        if (n.StartsWith("0098", StringComparison.Ordinal)) n = n.Substring(4);
        else if (n.StartsWith("98", StringComparison.Ordinal) && n.Length == 12) n = n.Substring(2);
        if (n.Length == 10 && n[0] == '9') n = "0" + n;
        return IsMobile(n) ? n : "";
    }

    public static bool IsMobile(string n)
    {
        if (n.Length != 11 || !n.StartsWith("09", StringComparison.Ordinal)) return false;
        foreach (char c in n)
            if (c < '0' || c > '9') return false;
        return true;
    }
}
