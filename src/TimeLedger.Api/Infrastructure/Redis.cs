using System.Text.Json;
using StackExchange.Redis;

namespace TimeLedger.Api.Infrastructure;

public enum IdempotencyState
{
    Started,
    InFlight,
    Completed,
}

public readonly record struct IdempotencyResult(IdempotencyState State, string RedisKey, string? StoredResponse);

/// <summary>
/// Makes POSTs safe to retry. The first request with a key claims it with SET NX; a duplicate either
/// gets the stored response back or a 409 if the original is still running. Results live for 24 hours; a claim
/// only for a minute, so a crashed request doesn't block its own retry for a day.
/// </summary>
public sealed class IdempotencyStore(IConnectionMultiplexer redis)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private static readonly TimeSpan PendingTtl = TimeSpan.FromMinutes(1);
    private const string PendingMarker = "__pending__";

    public async Task<IdempotencyResult> TryBeginAsync(string scope, string key)
    {
        var db = redis.GetDatabase();
        var redisKey = $"idem:{scope}:{key}";

        if (await db.StringSetAsync(redisKey, PendingMarker, PendingTtl, When.NotExists))
        {
            return new IdempotencyResult(IdempotencyState.Started, redisKey, null);
        }

        var existing = await db.StringGetAsync(redisKey);
        if (existing.IsNull)
        {
            // Expired between the two calls: claim it again.
            return await TryBeginAsync(scope, key);
        }

        return existing == PendingMarker
            ? new IdempotencyResult(IdempotencyState.InFlight, redisKey, null)
            : new IdempotencyResult(IdempotencyState.Completed, redisKey, existing.ToString());
    }

    public Task CompleteAsync(string redisKey, string responseJson) =>
        redis.GetDatabase().StringSetAsync(redisKey, responseJson, Ttl);

    /// <summary>Release the key after a failure so the client can retry.</summary>
    public Task AbandonAsync(string redisKey) => redis.GetDatabase().KeyDeleteAsync(redisKey);
}

public sealed record PresenceEntry(Guid EmployeeId, string EmployeeCode, string FullName, string Status, DateTimeOffset? Since);

/// <summary>
/// "Who's in right now". Each write bumps a generation and each generation has its own snapshot, so a reader
/// that loaded the database before a punch committed can only fill a snapshot nobody will read again.
/// </summary>
public sealed class PresenceCache(IConnectionMultiplexer redis)
{
    private const string GenerationKey = "presence:generation";
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromMinutes(1);

    /// <summary>Call before querying the database, and pass the result to <see cref="SetAsync"/>.</summary>
    public async Task<long> GetGenerationAsync() =>
        (long?)await redis.GetDatabase().StringGetAsync(GenerationKey) ?? 0;

    public Task InvalidateAsync() => redis.GetDatabase().StringIncrementAsync(GenerationKey);

    public async Task<IReadOnlyList<PresenceEntry>?> GetAsync(long generation)
    {
        var json = await redis.GetDatabase().StringGetAsync(SnapshotKey(generation));
        return json.IsNull ? null : JsonSerializer.Deserialize<List<PresenceEntry>>(json.ToString(), ApiJson.Web);
    }

    public Task SetAsync(long generation, IReadOnlyList<PresenceEntry> entries) =>
        redis.GetDatabase().StringSetAsync(SnapshotKey(generation), JsonSerializer.Serialize(entries, ApiJson.Web), SnapshotTtl);

    private static string SnapshotKey(long generation) => $"presence:v2:{generation}";
}

/// <summary>Single-holder lock: SET NX PX to acquire, compare-and-delete Lua script to release.</summary>
public sealed class RedisLock(IConnectionMultiplexer redis)
{
    private const string ReleaseScript =
        "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";

    public async Task<IAsyncDisposable?> TryAcquireAsync(string name, TimeSpan ttl)
    {
        var db = redis.GetDatabase();
        var key = $"lock:{name}";
        var token = Guid.NewGuid().ToString("N");

        return await db.StringSetAsync(key, token, ttl, When.NotExists)
            ? new Handle(db, key, token)
            : null;
    }

    private sealed class Handle(IDatabase db, string key, string token) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() =>
            await db.ScriptEvaluateAsync(ReleaseScript, new RedisKey[] { key }, new RedisValue[] { token });
    }
}
