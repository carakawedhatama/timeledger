using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using TimeLedger.Api.Data;
using TimeLedger.Domain;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace TimeLedger.Tests.Integration;

/// <summary>Real time, which a test can shift or pin. Timers are unaffected, so the workers keep polling.</summary>
public sealed class TestClock : TimeProvider
{
    private TimeSpan _offset;

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + _offset;

    public void Set(DateTimeOffset instant) => _offset = instant - base.GetUtcNow();

    public void Advance(TimeSpan by) => _offset += by;
}

/// <summary>One PostgreSQL and one Redis for the run; each app gets its own database in both.</summary>
public sealed class Containers : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithCommand("-c", "max_connections=1000")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine")
        .WithCommand("redis-server", "--databases", "1000")
        .Build();

    private int _next;

    /// <summary>An app shared by tests that only touch records they create themselves.</summary>
    public TestApp Shared { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());
        Shared = await StartAppAsync();
    }

    public async Task DisposeAsync()
    {
        await Shared.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
    }

    public (string Postgres, string Redis) NewDatabases()
    {
        var n = Interlocked.Increment(ref _next);
        var postgres = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
        {
            Database = $"timeledger_test_{n}",
            MaxPoolSize = 50,
        };
        return (postgres.ConnectionString, $"{_redis.GetConnectionString()},defaultDatabase={n},abortConnect=false");
    }

    public Task<TestApp> StartAppAsync(string environment = "Testing", Dictionary<string, string?>? settings = null) =>
        TestApp.StartAsync(NewDatabases(), environment, settings);
}

[CollectionDefinition(Name)]
public sealed class IntegrationCollection : ICollectionFixture<Containers>
{
    public const string Name = "integration";
}

public sealed class TestApp : WebApplicationFactory<Program>
{
    public const string Password = "integration-test-password";
    // At least 64 bytes, so it is a valid HS512 key too and the algorithm-pinning test tests the pin.
    public const string SigningKey = "integration-test-signing-key-0123456789-abcdefghijklmnopqrstuvwxyz-ABCDEF";

    private readonly (string Postgres, string Redis) _databases;
    private readonly string _environment;
    private readonly Dictionary<string, string?> _settings;

    private TestApp((string Postgres, string Redis) databases, string environment, Dictionary<string, string?>? settings)
    {
        _databases = databases;
        _environment = environment;
        _settings = new Dictionary<string, string?>
        {
            ["Auth:SigningKey"] = SigningKey,
            ["Auth:BootstrapAdmin:Email"] = AdminEmail,
            ["Auth:BootstrapAdmin:Password"] = Password,
            ["Seed:Enabled"] = "false",
            ["MockProvider:Enabled"] = "true",
            ["MockProvider:FailureRate"] = "0",
        };
        foreach (var (key, value) in settings ?? [])
        {
            _settings[key] = value;
        }
    }

    public string AdminEmail { get; } = "root@test.local";
    public TestClock Clock { get; } = new();
    public Api Admin { get; private set; } = null!;
    public Api Anonymous => new(CreateClient());

    /// <summary>Boots the app without signing in.</summary>
    public static TestApp Create((string Postgres, string Redis) databases, string environment = "Testing", Dictionary<string, string?>? settings = null) =>
        new(databases, environment, settings);

    public static async Task<TestApp> StartAsync((string Postgres, string Redis) databases, string environment, Dictionary<string, string?>? settings)
    {
        var app = Create(databases, environment, settings);
        app.Admin = await app.LoginAsync(app.AdminEmail);
        return app;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.UseSetting("ConnectionStrings:Postgres", _databases.Postgres);
        builder.UseSetting("ConnectionStrings:Redis", _databases.Redis);
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock)));
    }

    public async Task<T> DbAsync<T>(Func<TimeLedgerDbContext, Task<T>> query)
    {
        await using var scope = Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<TimeLedgerDbContext>());
    }

    public async Task<Api> LoginAsync(string email, string password = Password)
    {
        var body = await Anonymous.Post("/api/auth/login", new { email, password }).Expect(HttpStatusCode.OK);
        return WithToken(body.Str("accessToken"));
    }

    public Api WithToken(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new Api(client, token);
    }

    public async Task<Guid> EmployeeAsync(Guid? supervisorId = null, string schedule = "FiveDay", string timeZoneId = "Asia/Jakarta")
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var body = await Admin.Post("/api/employees", new
        {
            employeeCode = id,
            fullName = $"Person {id}",
            email = $"{id}@staff.test",
            monthlyBaseSalary = 5_190_000,
            schedule,
            timeZoneId,
            supervisorId,
        }).Expect(HttpStatusCode.Created);
        return body.Id();
    }

    public async Task<Person> PersonAsync(UserRole role, Guid? supervisorId = null)
    {
        var employeeId = await EmployeeAsync(supervisorId);
        var email = $"{employeeId:N}@login.test";
        var user = await Admin.Post("/api/users", new { email, password = Password, role = role.ToString(), employeeId })
            .Expect(HttpStatusCode.Created);
        return new Person(user.Id(), employeeId, email, await LoginAsync(email));
    }

    /// <summary>Supervisor → report, plus someone outside that team.</summary>
    public async Task<(Person Supervisor, Person Report, Person Outsider)> TeamAsync()
    {
        var supervisor = await PersonAsync(UserRole.Supervisor);
        var report = await PersonAsync(UserRole.Employee, supervisor.EmployeeId);
        return (supervisor, report, await PersonAsync(UserRole.Employee));
    }
}

public sealed record Person(Guid UserId, Guid EmployeeId, string Email, Api Api);

public sealed class Api(HttpClient client, string? token = null)
{
    public HttpClient Client => client;
    public string? Token => token;

    public Task<HttpResponseMessage> Get(string path) => client.GetAsync(path);

    public Task<HttpResponseMessage> Post(string path, object? body = null) =>
        body is null ? client.PostAsync(path, null) : client.PostAsJsonAsync(path, body);

    public Task<HttpResponseMessage> Put(string path, object body) => client.PutAsJsonAsync(path, body);

    public Task<HttpResponseMessage> Patch(string path, object body) => client.PatchAsJsonAsync(path, body);

    public Task<HttpResponseMessage> Punch(Guid employeeId, object type, string? at = null, string? key = null)
    {
        var body = new Dictionary<string, object> { ["employeeId"] = employeeId, ["type"] = type };
        if (at is not null)
        {
            body["occurredAt"] = at;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/clock-events") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    public Task<HttpResponseMessage> Leave(Guid employeeId, string start, string? end = null, string type = "Annual") =>
        Post("/api/leave-requests", new { employeeId, type, startDate = start, endDate = end ?? start });

    public Task<HttpResponseMessage> Period(string startDate, string endDate) =>
        Post("/api/pay-periods", new { startDate, endDate });
}

public static class HttpAssertions
{
    /// <summary>Asserts the status, showing the body on failure, and returns the JSON (if there is any).</summary>
    public static async Task<JsonElement> Expect(this Task<HttpResponseMessage> pending, HttpStatusCode expected)
    {
        using var response = await pending;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(expected == response.StatusCode, $"Expected {(int)expected}, got {(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonSerializer.Deserialize<JsonElement>(text);
    }

    public static string Str(this JsonElement json, string name) => json.GetProperty(name).GetString()!;

    public static Guid Id(this JsonElement json) => json.GetProperty("id").GetGuid();

    public static HashSet<Guid> Ids(this JsonElement array, string name = "id") =>
        array.EnumerateArray().Select(e => e.GetProperty(name).GetGuid()).ToHashSet();

    public static async Task Eventually(Func<Task<bool>> condition, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Condition not met within {seconds}s.");
            await Task.Delay(100);
        }
    }
}
