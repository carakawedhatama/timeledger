using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace TimeLedger.Tests.Integration;

[Collection(IntegrationCollection.Name)]
public class ClockAndValidationTests(Containers containers)
{
    private TestApp App => containers.Shared;

    [Fact]
    public async Task Concurrent_clock_ins_record_one_punch()
    {
        var employee = await App.EmployeeAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => App.Admin.Punch(employee, "ClockIn")));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, await App.DbAsync(db => db.ClockEvents.CountAsync(c => c.EmployeeId == employee)));
    }

    [Fact]
    public async Task Retry_replays_the_identical_response()
    {
        var employee = await App.EmployeeAsync();
        using var first = await App.Admin.Punch(employee, "ClockIn", key: "retry");
        using var second = await App.Admin.Punch(employee, "ClockIn", key: "retry");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("true", second.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Retry_replays_from_postgres_when_redis_lost_the_key()
    {
        var employee = await App.EmployeeAsync();
        var first = await App.Admin.Punch(employee, "ClockIn", key: "lost").Expect(HttpStatusCode.Created);
        var deleted = await App.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase()
            .KeyDeleteAsync($"idem:clock:{employee:N}:lost");

        var second = await App.Admin.Punch(employee, "ClockIn", key: "lost").Expect(HttpStatusCode.OK);

        Assert.True(deleted);
        Assert.Equal(first.GetRawText(), second.GetRawText());
    }

    [Fact]
    public async Task Concurrent_retries_of_one_key_record_one_punch()
    {
        var employee = await App.EmployeeAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 15).Select(_ => App.Admin.Punch(employee, "ClockIn", key: "burst")));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(results, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK, HttpStatusCode.Conflict }));
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_punch_is_refused()
    {
        var employee = await App.EmployeeAsync();
        await App.Admin.Punch(employee, "ClockIn", key: "reused").Expect(HttpStatusCode.Created);

        await App.Admin.Punch(employee, "ClockOut", key: "reused").Expect(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task The_same_key_for_two_employees_is_two_punches()
    {
        var (a, b) = (await App.EmployeeAsync(), await App.EmployeeAsync());

        await App.Admin.Punch(a, "ClockIn", key: "shared").Expect(HttpStatusCode.Created);
        var second = await App.Admin.Punch(b, "ClockIn", key: "shared").Expect(HttpStatusCode.Created);

        Assert.Equal(b, second.GetProperty("employeeId").GetGuid());
    }

    [Fact]
    public async Task Presence_is_complete_after_a_punch_and_after_a_new_hire()
    {
        await using var app = await containers.StartAppAsync();
        var (a, b) = (await app.EmployeeAsync(), await app.EmployeeAsync());

        await app.Admin.Punch(a, "ClockIn").Expect(HttpStatusCode.Created);
        var afterPunch = await app.Admin.Get("/api/presence").Expect(HttpStatusCode.OK);
        Assert.True(afterPunch.Ids("employeeId").SetEquals([a, b]));

        var hire = await app.EmployeeAsync();
        Assert.Contains(hire, (await app.Admin.Get("/api/presence").Expect(HttpStatusCode.OK)).Ids("employeeId"));

        await app.Admin.Punch(a, "ClockOut").Expect(HttpStatusCode.Created);
        var afterOut = await app.Admin.Get("/api/presence").Expect(HttpStatusCode.OK);
        Assert.Equal("out", afterOut.EnumerateArray().Single(e => e.GetProperty("employeeId").GetGuid() == a).Str("status"));
    }

    [Theory]
    [InlineData(99)]
    [InlineData(1)]
    [InlineData("99")]
    [InlineData("Lunch")]
    [InlineData("ClockIn, ClockOut")]
    public async Task Undefined_punch_type_is_refused(object type)
    {
        await App.Admin.Punch(await App.EmployeeAsync(), type).Expect(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("schedule", 99)]
    [InlineData("schedule", "SevenDay")]
    [InlineData("monthlyBaseSalary", 0)]
    [InlineData("monthlyBaseSalary", 1e17)]
    [InlineData("timeZoneId", null)]
    [InlineData("timeZoneId", "Mars/Olympus")]
    [InlineData("employeeCode", "")]
    [InlineData("fullName", null)]
    [InlineData("fullName", "a\0b")]
    [InlineData("email", "nope")]
    [InlineData("supervisorId", "00000000-0000-0000-0000-000000000001")]
    public async Task Invalid_employee_is_a_400(string field, object? value)
    {
        var id = Guid.NewGuid().ToString("N")[..10];
        var body = new Dictionary<string, object?>
        {
            ["employeeCode"] = id,
            ["fullName"] = "Valid Name",
            ["email"] = $"{id}@staff.test",
            ["monthlyBaseSalary"] = 5_000_000,
        };
        body[field] = value;

        await App.Admin.Post("/api/employees", body).Expect(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Duplicate_email_in_another_case_is_a_409()
    {
        var id = Guid.NewGuid().ToString("N")[..10];
        object Body(string code, string email) => new { employeeCode = code, fullName = "Name", email, monthlyBaseSalary = 1_000_000 };

        await App.Admin.Post("/api/employees", Body(id, $"{id}@staff.test")).Expect(HttpStatusCode.Created);
        await App.Admin.Post("/api/employees", Body(id + "x", $" {id.ToUpperInvariant()}@STAFF.TEST ")).Expect(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("/api/holidays?year=99999")]
    [InlineData("/api/holidays?year=0")]
    [InlineData("/api/leave-requests?status=nope")]
    [InlineData("/api/employees/{employee}/clock-events?to=9999-12-31")]
    [InlineData("/api/employees/{employee}/clock-events?from=not-a-date")]
    [InlineData("/api/employees/{employee}/leave-balance?year=99999")]
    public async Task Bad_query_is_a_400(string path)
    {
        var employee = await App.EmployeeAsync();

        await App.Admin.Get(path.Replace("{employee}", employee.ToString())).Expect(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Balance_of_a_nonexistent_employee_is_a_404()
    {
        await App.Admin.Get($"/api/employees/{Guid.NewGuid()}/leave-balance").Expect(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("Testing")]
    [InlineData("Development")] // where minimal APIs throw on a bad request
    public async Task Malformed_json_is_a_400(string environment)
    {
        await using var app = await containers.StartAppAsync(environment);

        await app.Admin.Client.PostAsync("/api/employees", new StringContent("{bad", Encoding.UTF8, "application/json"))
            .Expect(HttpStatusCode.BadRequest);
        await app.Admin.Get("/api/leave-requests?status=nope").Expect(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task Holiday_needs_a_name(string? name)
    {
        await App.Admin.Put("/api/holidays/2070-03-03", new { date = "2070-03-03", name }).Expect(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Holiday_name_is_limited()
    {
        await App.Admin.Put("/api/holidays/2070-03-03", new { date = "2070-03-03", name = new string('x', 201) }).Expect(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("name", "")]
    [InlineData("eventTypes", null)]
    [InlineData("secret", "abc")]
    [InlineData("provider", 99)]
    [InlineData("provider", null)]
    [InlineData("endpointUrl", "ftp://example.com")]
    [InlineData("endpointUrl", null)]
    public async Task Invalid_integration_is_a_400(string field, object? value)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = Guid.NewGuid().ToString(),
            ["provider"] = "GenericWebhook",
            ["endpointUrl"] = "https://example.com/hook",
            ["eventTypes"] = new[] { "leave.approved" },
        };
        body[field] = value;

        await App.Admin.Post("/api/integrations", body).Expect(HttpStatusCode.BadRequest);
    }
}
