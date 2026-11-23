using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace TimeLedger.Api.Auth;

/// <summary>
/// Limits password guesses per account. Each attempt is counted before the password is checked, so a burst
/// of parallel guesses can't all slip in under the limit. Success clears the counter.
/// </summary>
public sealed class LoginThrottle(IConnectionMultiplexer redis, IOptions<AuthOptions> options)
{
    private const string CountScript = """
        local n = redis.call('INCR', KEYS[1])
        if n == 1 then redis.call('EXPIRE', KEYS[1], ARGV[1]) end
        return {n, redis.call('TTL', KEYS[1])}
        """;

    /// <returns>Seconds to wait, or null if the attempt may proceed.</returns>
    public async Task<long?> RegisterAttemptAsync(string email)
    {
        var settings = options.Value;
        var result = (long[])(await redis.GetDatabase().ScriptEvaluateAsync(
            CountScript, new RedisKey[] { Key(email) }, new RedisValue[] { settings.LockoutMinutes * 60 }))!;

        return result[0] > settings.MaxFailedLogins ? Math.Max(result[1], 1) : null;
    }

    public Task ResetAsync(string email) => redis.GetDatabase().KeyDeleteAsync(Key(email));

    private static string Key(string email) => "login:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email)));
}
