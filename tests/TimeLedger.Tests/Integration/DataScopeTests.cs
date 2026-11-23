using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TimeLedger.Api.Auth;
using TimeLedger.Domain;
using TimeLedger.Domain.Entities;
using TimeLedger.Domain.Rules;

namespace TimeLedger.Tests.Integration;

/// <summary>Past the role check: whose data each person can read and change.</summary>
[Collection(IntegrationCollection.Name)]
public class DataScopeTests(Containers containers)
{
    private TestApp App => containers.Shared;

    [Fact]
    public async Task Employee_lists_only_themselves()
    {
        var (_, report, _) = await App.TeamAsync();

        Assert.Equal([report.EmployeeId], (await report.Api.Get("/api/employees").Expect(HttpStatusCode.OK)).Ids());
    }

    [Fact]
    public async Task Supervisor_lists_self_and_direct_reports_only()
    {
        var (supervisor, report, _) = await App.TeamAsync();
        await App.EmployeeAsync(report.EmployeeId); // an indirect report

        var ids = (await supervisor.Api.Get("/api/employees").Expect(HttpStatusCode.OK)).Ids();

        Assert.True(ids.SetEquals([supervisor.EmployeeId, report.EmployeeId]));
    }

    [Fact]
    public async Task Admin_lists_everyone()
    {
        var (supervisor, report, outsider) = await App.TeamAsync();

        var ids = (await App.Admin.Get("/api/employees").Expect(HttpStatusCode.OK)).Ids();

        Assert.Superset(new HashSet<Guid> { supervisor.EmployeeId, report.EmployeeId, outsider.EmployeeId }, ids);
    }

    [Fact]
    public async Task Salary_is_visible_only_to_the_employee_and_payroll()
    {
        var (supervisor, report, _) = await App.TeamAsync();
        var path = $"/api/employees/{report.EmployeeId}";

        async Task<JsonValueKind> SalaryAs(Api api) =>
            (await api.Get(path).Expect(HttpStatusCode.OK)).GetProperty("monthlyBaseSalary").ValueKind;

        Assert.Equal(JsonValueKind.Number, await SalaryAs(report.Api));
        Assert.Equal(JsonValueKind.Number, await SalaryAs(App.Admin));
        Assert.Equal(JsonValueKind.Null, await SalaryAs(supervisor.Api));

        var listed = await supervisor.Api.Get("/api/employees").Expect(HttpStatusCode.OK);
        var row = listed.EnumerateArray().Single(e => e.Id() == report.EmployeeId);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("monthlyBaseSalary").ValueKind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/clock-events")]
    [InlineData("/leave-balance")]
    public async Task Records_outside_your_scope_are_404(string suffix)
    {
        var (supervisor, report, outsider) = await App.TeamAsync();

        await supervisor.Api.Get($"/api/employees/{outsider.EmployeeId}{suffix}").Expect(HttpStatusCode.NotFound);
        await report.Api.Get($"/api/employees/{supervisor.EmployeeId}{suffix}").Expect(HttpStatusCode.NotFound);
        await report.Api.Get($"/api/employees/{report.EmployeeId}{suffix}").Expect(HttpStatusCode.OK);
        await supervisor.Api.Get($"/api/employees/{report.EmployeeId}{suffix}").Expect(HttpStatusCode.OK);
    }

    [Fact]
    public async Task People_clock_only_themselves()
    {
        var (supervisor, report, outsider) = await App.TeamAsync();

        await report.Api.Punch(report.EmployeeId, "ClockIn").Expect(HttpStatusCode.Created);
        await report.Api.Punch(outsider.EmployeeId, "ClockIn").Expect(HttpStatusCode.Forbidden);
        await supervisor.Api.Punch(report.EmployeeId, "ClockOut").Expect(HttpStatusCode.Forbidden);
        await App.Admin.Punch(report.EmployeeId, "ClockOut").Expect(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Forbidden_punch_does_not_reveal_whether_the_employee_exists()
    {
        var (_, report, outsider) = await App.TeamAsync();

        var real = await report.Api.Punch(outsider.EmployeeId, "ClockIn").Expect(HttpStatusCode.Forbidden);
        var fake = await report.Api.Punch(Guid.NewGuid(), "ClockIn").Expect(HttpStatusCode.Forbidden);

        Assert.Equal(real.Str("title"), fake.Str("title"));
    }

    [Theory]
    [InlineData(UserRole.Employee)]
    [InlineData(UserRole.Supervisor)]
    [InlineData(UserRole.PayrollAdmin)]
    public async Task Nobody_chooses_the_time_of_their_own_punch(UserRole role)
    {
        var person = await App.PersonAsync(role);

        await person.Api.Punch(person.EmployeeId, "ClockIn", "2026-01-05T08:00:00+07:00").Expect(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Admin_can_correct_someone_elses_punch_time()
    {
        var employee = await App.EmployeeAsync();

        var punch = await App.Admin.Punch(employee, "ClockIn", "2026-01-05T08:00:00+07:00").Expect(HttpStatusCode.Created);

        Assert.Equal(new DateTimeOffset(2026, 1, 5, 1, 0, 0, TimeSpan.Zero), punch.GetProperty("occurredAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Replay_cannot_reveal_someone_elses_punch()
    {
        var (_, report, outsider) = await App.TeamAsync();
        await report.Api.Punch(report.EmployeeId, "ClockIn", key: "known-key").Expect(HttpStatusCode.Created);

        await outsider.Api.Punch(report.EmployeeId, "ClockIn", key: "known-key").Expect(HttpStatusCode.Forbidden);
        var own = await outsider.Api.Punch(outsider.EmployeeId, "ClockIn", key: "known-key").Expect(HttpStatusCode.Created);

        Assert.Equal(outsider.EmployeeId, own.GetProperty("employeeId").GetGuid());
    }

    [Fact]
    public async Task Presence_is_filtered_per_caller_from_one_shared_snapshot()
    {
        var (supervisor, report, outsider) = await App.TeamAsync();

        // Admin first, so the cache holds everyone.
        var all = (await App.Admin.Get("/api/presence").Expect(HttpStatusCode.OK)).Ids("employeeId");
        var team = (await supervisor.Api.Get("/api/presence").Expect(HttpStatusCode.OK)).Ids("employeeId");
        var self = (await report.Api.Get("/api/presence").Expect(HttpStatusCode.OK)).Ids("employeeId");

        Assert.Contains(outsider.EmployeeId, all);
        Assert.True(team.SetEquals([supervisor.EmployeeId, report.EmployeeId]));
        Assert.Equal([report.EmployeeId], self);
    }

    [Fact]
    public async Task Leave_lists_are_scoped()
    {
        var (supervisor, report, outsider) = await App.TeamAsync();
        var mine = (await report.Api.Leave(report.EmployeeId, "2060-03-01").Expect(HttpStatusCode.Created)).Id();
        var theirs = (await outsider.Api.Leave(outsider.EmployeeId, "2060-03-01").Expect(HttpStatusCode.Created)).Id();

        Assert.Equal([mine], (await report.Api.Get("/api/leave-requests").Expect(HttpStatusCode.OK)).Ids());
        Assert.Equal([mine], (await supervisor.Api.Get("/api/leave-requests?status=Pending").Expect(HttpStatusCode.OK)).Ids());
        Assert.Superset(new HashSet<Guid> { mine, theirs }, (await App.Admin.Get("/api/leave-requests").Expect(HttpStatusCode.OK)).Ids());
    }

    [Fact]
    public async Task People_request_leave_only_for_themselves()
    {
        var (supervisor, report, outsider) = await App.TeamAsync();

        await report.Api.Leave(outsider.EmployeeId, "2060-03-02").Expect(HttpStatusCode.Forbidden);
        await supervisor.Api.Leave(report.EmployeeId, "2060-03-02").Expect(HttpStatusCode.Forbidden);
        await App.Admin.Leave(report.EmployeeId, "2060-03-02").Expect(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task Supervisor_decides_for_direct_reports(string action)
    {
        var (supervisor, report, _) = await App.TeamAsync();
        var id = (await report.Api.Leave(report.EmployeeId, "2060-03-03").Expect(HttpStatusCode.Created)).Id();

        var decided = await supervisor.Api.Post($"/api/leave-requests/{id}/{action}", new { }).Expect(HttpStatusCode.OK);

        Assert.Equal(supervisor.Email, decided.Str("decidedBy"));
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task Supervisor_cannot_see_or_decide_leave_outside_the_team(string action)
    {
        var (supervisor, _, outsider) = await App.TeamAsync();
        var id = (await outsider.Api.Leave(outsider.EmployeeId, "2060-03-04").Expect(HttpStatusCode.Created)).Id();

        await supervisor.Api.Post($"/api/leave-requests/{id}/{action}", new { }).Expect(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(UserRole.Supervisor, "approve")]
    [InlineData(UserRole.Supervisor, "reject")]
    [InlineData(UserRole.PayrollAdmin, "approve")]
    [InlineData(UserRole.PayrollAdmin, "reject")]
    public async Task Nobody_decides_their_own_leave(UserRole role, string action)
    {
        var person = await App.PersonAsync(role);
        var id = (await person.Api.Leave(person.EmployeeId, "2060-03-05").Expect(HttpStatusCode.Created)).Id();

        await person.Api.Post($"/api/leave-requests/{id}/{action}", new { }).Expect(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Approver_in_the_body_is_ignored()
    {
        var (supervisor, report, _) = await App.TeamAsync();
        var id = (await report.Api.Leave(report.EmployeeId, "2060-03-08").Expect(HttpStatusCode.Created)).Id();

        var decided = await supervisor.Api.Post($"/api/leave-requests/{id}/approve", new { decidedBy = "ceo@company.test" }).Expect(HttpStatusCode.OK);

        Assert.Equal(supervisor.Email, decided.Str("decidedBy"));
    }

    [Fact]
    public async Task Decision_works_without_a_body()
    {
        var (supervisor, report, _) = await App.TeamAsync();
        var id = (await report.Api.Leave(report.EmployeeId, "2060-03-09").Expect(HttpStatusCode.Created)).Id();

        await supervisor.Api.Post($"/api/leave-requests/{id}/approve").Expect(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Moving_teams_moves_access_immediately()
    {
        var (before, report, _) = await App.TeamAsync();
        var after = await App.PersonAsync(UserRole.Supervisor);

        await App.Admin.Put($"/api/employees/{report.EmployeeId}/supervisor", new { supervisorId = after.EmployeeId }).Expect(HttpStatusCode.OK);

        await before.Api.Get($"/api/employees/{report.EmployeeId}").Expect(HttpStatusCode.NotFound);
        await after.Api.Get($"/api/employees/{report.EmployeeId}").Expect(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Clearing_the_supervisor_removes_access()
    {
        var (supervisor, report, _) = await App.TeamAsync();

        await App.Admin.Put($"/api/employees/{report.EmployeeId}/supervisor", new { supervisorId = (Guid?)null }).Expect(HttpStatusCode.OK);

        await supervisor.Api.Get($"/api/employees/{report.EmployeeId}").Expect(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Having_reports_without_the_role_grants_nothing()
    {
        var lead = await App.PersonAsync(UserRole.Employee);
        var report = await App.EmployeeAsync(lead.EmployeeId);

        await lead.Api.Get($"/api/employees/{report}").Expect(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Supervisor_chain_cannot_loop()
    {
        var a = await App.EmployeeAsync();
        var b = await App.EmployeeAsync(a);
        var c = await App.EmployeeAsync(b);

        await App.Admin.Put($"/api/employees/{a}/supervisor", new { supervisorId = a }).Expect(HttpStatusCode.UnprocessableEntity);
        await App.Admin.Put($"/api/employees/{a}/supervisor", new { supervisorId = b }).Expect(HttpStatusCode.UnprocessableEntity);
        await App.Admin.Put($"/api/employees/{a}/supervisor", new { supervisorId = c }).Expect(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Query_filter_agrees_with_the_access_rules_for_every_actor()
    {
        var (supervisor, report, outsider) = await App.TeamAsync();
        Guid?[] links = [null, supervisor.EmployeeId, report.EmployeeId, outsider.EmployeeId];
        var everyone = await App.DbAsync(db => db.Employees.AsNoTracking().ToListAsync());

        foreach (var role in Enum.GetValues<UserRole>().Append(default))
        {
            foreach (var link in links)
            {
                var caller = new CurrentUser();
                caller.SignIn(new UserAccount { Email = "x@y.z", Role = role, EmployeeId = link }, Guid.NewGuid());

                var actual = await App.DbAsync(db => caller.VisibleEmployees(db).Select(e => e.Id).ToListAsync());
                var expected = everyone.Where(e => AccessRules.CanView(caller.Actor, e.Id, e.SupervisorId)).Select(e => e.Id);

                Assert.Equal(expected.Order(), actual.Order());
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timesheets_are_scoped_and_hide_team_pay(bool closed)
    {
        await using var app = await containers.StartAppAsync();
        var (supervisor, report, _) = await app.TeamAsync();
        await app.Admin.Punch(report.EmployeeId, "ClockIn", "2026-03-02T07:00:00+07:00").Expect(HttpStatusCode.Created);
        await app.Admin.Punch(report.EmployeeId, "ClockOut", "2026-03-02T18:00:00+07:00").Expect(HttpStatusCode.Created);
        var period = (await app.Admin.Period("2026-03-01", "2026-03-31").Expect(HttpStatusCode.Created)).Id();
        if (closed)
        {
            await app.Admin.Post($"/api/pay-periods/{period}/close").Expect(HttpStatusCode.OK);
        }

        async Task<JsonElement> SheetsAs(Api api) =>
            (await api.Get($"/api/pay-periods/{period}/timesheets").Expect(HttpStatusCode.OK)).GetProperty("employees");

        var team = await SheetsAs(supervisor.Api);
        Assert.True(team.Ids("employeeId").SetEquals([supervisor.EmployeeId, report.EmployeeId]));

        var hidden = team.EnumerateArray().Single(e => e.GetProperty("employeeId").GetGuid() == report.EmployeeId);
        Assert.Equal(JsonValueKind.Null, hidden.GetProperty("summary").GetProperty("overtimePay").ValueKind);
        Assert.Equal(180, hidden.GetProperty("summary").GetProperty("overtimeMinutes").GetInt32());
        Assert.All(hidden.GetProperty("days").EnumerateArray(), d => Assert.Equal(JsonValueKind.Null, d.GetProperty("overtimePay").ValueKind));

        var own = Assert.Single((await SheetsAs(report.Api)).EnumerateArray());
        Assert.Equal(165_000m, own.GetProperty("summary").GetProperty("overtimePay").GetDecimal());

        Assert.Equal(3, (await SheetsAs(app.Admin)).GetArrayLength());
    }
}
