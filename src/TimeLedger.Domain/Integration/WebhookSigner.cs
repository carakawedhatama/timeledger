using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TimeLedger.Domain.Integration;

/// <summary>
/// Signs outgoing webhooks as <c>t=&lt;unix seconds&gt;,v1=&lt;hex HMAC-SHA256 of "t.body"&gt;</c>.
/// Including the timestamp in the signed content lets receivers reject replayed requests.
/// </summary>
public static class WebhookSigner
{
    public const string SignatureHeader = "X-TimeLedger-Signature";
    public const int DefaultToleranceSeconds = 300;

    public static string Sign(string secret, long unixSeconds, string body) =>
        $"t={unixSeconds.ToString(CultureInfo.InvariantCulture)},v1={ComputeHex(secret, unixSeconds, body)}";

    public static bool Verify(string secret, string? header, string body, long nowUnixSeconds, int toleranceSeconds = DefaultToleranceSeconds)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        long? timestamp = null;
        string? signature = null;
        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2)
            {
                continue;
            }

            if (kv[0] == "t" && long.TryParse(kv[1], NumberStyles.None, CultureInfo.InvariantCulture, out var t))
            {
                timestamp = t;
            }
            else if (kv[0] == "v1")
            {
                signature = kv[1];
            }
        }

        if (timestamp is null || signature is null || Math.Abs(nowUnixSeconds - timestamp.Value) > toleranceSeconds)
        {
            return false;
        }

        var expected = ComputeHex(secret, timestamp.Value, body);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature));
    }

    private static string ComputeHex(string secret, long unixSeconds, string body)
    {
        var payload = Encoding.UTF8.GetBytes($"{unixSeconds.ToString(CultureInfo.InvariantCulture)}.{body}");
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
