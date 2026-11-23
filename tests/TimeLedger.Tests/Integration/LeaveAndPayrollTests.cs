using System.Net;
using Microsoft.EntityFrameworkCore;
using TimeLedger.Domain;

namespace TimeLedger.Tests.Integration;

[Collection(IntegrationCollection.Name)]
public class LeaveAndPayrollTests(Containers containers)
{
    private TestApp App => containers.Shared;

    [Fact]
    public async Task Concurrent_approval_is_one_success_and_conflicts()
    {
        var employee = await App.EmployeeAsync();
        var id = (await App.Admin.Leave(employee, "2061-03-07", "2061-03-08").Expect(HttpStatusCode.Created)).Id();

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => App.Admin.Post($"/api/leave-requests/{id}/approve")));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(results, r => Assert.Contains(r.StatusCode,
            new[] { HttpStatusCode.OK, HttpStatusCode.Conflict, HttpStatusCode.UnprocessableEntity }));
        Assert.Contains(results, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(2m, await App.DbAsync(db => db.LeaveBalances.Where(b => b.EmployeeId == employee).SumAsync(b => b.UsedDays)));
    }

    [Fact]
    public async Task Parallel_first_approvals_of_different_requests_all_succeed()
    {
        var employee = await App.EmployeeAsync();
        var ids = new List<Guid>();
        foreach (var day in new[] { "07", "08", "09", "10", "11" })
        {
            ids.Add((await App.Admin.Leave(employee, $"2061-03-{day}").Expect(HttpStatusCode.Created)).Id());
        }

        var results = await Task.WhenAll(ids.Select(id => App.Admin.Post($"/api/leave-requests/{id}/approve")));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(5m, await App.DbAsync(db => db.LeaveBalances.Where(b => b.EmployeeId == employee).SumAsync(b => b.UsedDays)));
    }

    [Fact]
    public async Task Approving_twice_in_sequence_is_a_422()
    {
        var id = (await App.Admin.Leave(await App.EmployeeAsync(), "2061-04-04").Expect(HttpStatusCode.Created)).Id();
        await App.Admin.Post($"/api/leave-requests/{id}/approve").Expect(HttpStatusCode.OK);

        var error = await App.Admin.Post($"/api/leave-requests/{id}/approve").Expect(HttpStatusCode.UnprocessableEntity);

        Assert.Contains("already Approved", error.Str("title"));
    }

    [Fact]
    public async Task Concurrent_overlapping_requests_create_one()
    {
        var employee = await App.EmployeeAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => App.Admin.Leave(employee, "2061-05-02", "2061-05-03")));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
    }

    [Fact]
    public async Task Leave_across_new_year_is_charged_to_both_years()
    {
        var employee = await App.EmployeeAsync();
        var id = (await App.Admin.Leave(employee, "2063-12-27", "2064-01-04").Expect(HttpStatusCode.Created)).Id();

        await App.Admin.Post($"/api/leave-requests/{id}/approve").Expect(HttpStatusCode.OK);

        var used = await App.DbAsync(db => db.LeaveBalances.Where(b => b.EmployeeId == employee).ToDictionaryAsync(b => b.Year, b => b.UsedDays));
        Assert.Equal(3m, used[2063]); // Thu 27, Fri 28, a weekend, Mon 31
        Assert.Equal(4m, used[2064]); // Tue 1 to Fri 4: this database has no holiday on 1 Jan
    }

    [Fact]
    public async Task Annual_balance_is_enforced_at_approval()
    {
        var employee = await App.EmployeeAsync();
        var first = (await App.Admin.Leave(employee, "2061-06-06", "2061-06-15").Expect(HttpStatusCode.Created)).Id();
        var second = (await App.Admin.Leave(employee, "2061-07-04", "2061-07-13").Expect(HttpStatusCode.Created)).Id();
        await App.Admin.Post($"/api/leave-requests/{first}/approve").Expect(HttpStatusCode.OK);

        await App.Admin.Post($"/api/leave-requests/{second}/approve").Expect(HttpStatusCode.UnprocessableEntity);

        Assert.Equal(LeaveStatus.Pending, await App.DbAsync(db => db.LeaveRequests.Where(l => l.Id == second).Select(l => l.Status).SingleAsync()));
    }

    [Theory]
    [InlineData("9999-12-30", "9999-12-31", HttpStatusCode.BadRequest)]
    [InlineData("0001-01-03", "2100-01-01", HttpStatusCode.BadRequest)]
    [InlineData("2061-01-01", "2062-06-01", HttpStatusCode.UnprocessableEntity)]
    [InlineData("2061-03-10", "2061-03-01", HttpStatusCode.UnprocessableEntity)]
    [InlineData("2061-03-05", "2061-03-06", HttpStatusCode.UnprocessableEntity)] // a weekend
    public async Task Bad_leave_range_is_refused(string start, string end, HttpStatusCode expected)
    {
        await App.Admin.Leave(await App.EmployeeAsync(), start, end, "Sick").Expect(expected);
    }

    [Fact]
    public async Task Long_reason_is_a_400()
    {
        await App.Admin.Post("/api/leave-requests", new
        {
            employeeId = await App.EmployeeAsync(),
            type = "Sick",
            startDate = "2061-03-01",
            endDate = "2061-03-01",
            reason = new string('x', 501),
        }).Expect(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Leave_type_is_required()
    {
        await App.Admin.Post("/api/leave-requests", new { employeeId = await App.EmployeeAsync(), startDate = "2061-03-01", endDate = "2061-03-01" })
            .Expect(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Concurrent_overlapping_periods_create_one()
    {
        await using var app = await containers.StartAppAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => app.Admin.Period("2040-03-01", "2040-03-31")));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
    }

    [Fact]
    public async Task Closed_period_accepts_no_punches_or_leave()
    {
        await using var app = await containers.StartAppAsync();
        var employee = await app.EmployeeAsync();
        var pending = (await app.Admin.Leave(employee, "2026-03-10").Expect(HttpStatusCode.Created)).Id();
        var period = (await app.Admin.Period("2026-03-01", "2026-03-31").Expect(HttpStatusCode.Created)).Id();
        await app.Admin.Post($"/api/pay-periods/{period}/close").Expect(HttpStatusCode.OK);

        await app.Admin.Punch(employee, "ClockIn", "2026-03-12T08:00:00+07:00").Expect(HttpStatusCode.UnprocessableEntity);
        await app.Admin.Leave(employee, "2026-03-17").Expect(HttpStatusCode.UnprocessableEntity);
        await app.Admin.Post($"/api/leave-requests/{pending}/approve").Expect(HttpStatusCode.UnprocessableEntity);
        await app.Admin.Post($"/api/leave-requests/{pending}/reject").Expect(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Approval_cannot_land_while_a_close_is_computing()
    {
        await using var app = await containers.StartAppAsync();
        var employee = await app.EmployeeAsync();
        var leave = (await app.Admin.Leave(employee, "2026-03-10", type: "Sick").Expect(HttpStatusCode.Created)).Id();
        var period = (await app.Admin.Period("2026-03-01", "2026-03-31").Expect(HttpStatusCode.Created)).Id();

        Task<HttpResponseMessage> close = null!;
        Task<HttpResponseMessage> approve = null!;
        await app.DbAsync(async db =>
        {
            // Hold the close at its insert, after it has read the leave it will freeze.
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("LOCK TABLE timesheet_entries IN SHARE MODE");
            close = app.Admin.Post($"/api/pay-periods/{period}/close");
            await HttpAssertions.Eventually(() => app.DbAsync(other => other.Database
                .SqlQuery<bool>($"""SELECT EXISTS(SELECT 1 FROM pg_locks l JOIN pg_class c ON c.oid = l.relation WHERE c.relname = 'timesheet_entries' AND NOT l.granted) AS "Value" """)
                .SingleAsync()));

            approve = app.Admin.Post($"/api/leave-requests/{leave}/approve");
            await Task.Delay(500);
            Assert.False(approve.IsCompleted);
            await tx.CommitAsync();
            return true;
        });

        await close.Expect(HttpStatusCode.OK);
        await approve.Expect(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Close_waits_for_a_night_shift_and_then_pays_it()
    {
        await using var app = await containers.StartAppAsync();
        var employee = await app.EmployeeAsync();
        app.Clock.Set(DateTimeOffset.Parse("2026-04-01T00:30:00+07:00"));
        var admin = await app.LoginAsync(app.AdminEmail);
        await admin.Punch(employee, "ClockIn", "2026-03-31T22:00:00+07:00").Expect(HttpStatusCode.Created);
        var period = (await admin.Period("2026-03-01", "2026-03-31").Expect(HttpStatusCode.Created)).Id();

        await admin.Post($"/api/pay-periods/{period}/close").Expect(HttpStatusCode.UnprocessableEntity);

        app.Clock.Set(DateTimeOffset.Parse("2026-04-01T06:30:00+07:00"));
        await admin.Punch(employee, "ClockOut", "2026-04-01T06:00:00+07:00").Expect(HttpStatusCode.Created);
        var closed = await admin.Post($"/api/pay-periods/{period}/close").Expect(HttpStatusCode.OK);

        Assert.Equal(480, closed.GetProperty("employees")[0].GetProperty("summary").GetProperty("workedMinutes").GetInt32());
    }

    [Fact]
    public async Task A_stale_open_shift_does_not_block_close_or_trap_the_employee()
    {
        await using var app = await containers.StartAppAsync();
        var employee = await app.EmployeeAsync();
        await app.Admin.Punch(employee, "ClockIn", "2026-03-20T08:00:00+07:00").Expect(HttpStatusCode.Created);
        var period = (await app.Admin.Period("2026-03-01", "2026-03-31").Expect(HttpStatusCode.Created)).Id();

        await app.Admin.Post($"/api/pay-periods/{period}/close").Expect(HttpStatusCode.OK);

        await app.Admin.Punch(employee, "ClockOut").Expect(HttpStatusCode.Created);
        await app.Admin.Punch(employee, "ClockIn").Expect(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Close_waits_until_the_day_is_over_in_every_employees_zone()
    {
        await using var app = await containers.StartAppAsync();
        await app.EmployeeAsync(timeZoneId: "America/New_York");
        app.Clock.Set(DateTimeOffset.Parse("2026-04-01T02:00:00+07:00"));
        var admin = await app.LoginAsync(app.AdminEmail);
        var period = (await admin.Period("2026-03-01", "2026-03-31").Expect(HttpStatusCode.Created)).Id();

        await admin.Post($"/api/pay-periods/{period}/close").Expect(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Long_shift_across_the_period_edge_is_not_an_orphan_clock_out()
    {
        await using var app = await containers.StartAppAsync();
        var employee = await app.EmployeeAsync();
        await app.Admin.Punch(employee, "ClockIn", "2026-06-30T06:00:00+07:00").Expect(HttpStatusCode.Created);
        await app.Admin.Punch(employee, "ClockOut", "2026-07-01T01:00:00+07:00").Expect(HttpStatusCode.Created);
        var period = (await app.Admin.Period("2026-07-01", "2026-07-31").Expect(HttpStatusCode.Created)).Id();

        var sheets = await app.Admin.Get($"/api/pay-periods/{period}/timesheets").Expect(HttpStatusCode.OK);

        Assert.DoesNotContain("without a matching", sheets.GetRawText());
    }

    [Fact]
    public async Task Closing_twice_at_once_publishes_one_event()
    {
        await using var app = await containers.StartAppAsync();
        await app.EmployeeAsync();
        var period = (await app.Admin.Period("2026-03-01", "2026-03-31").Expect(HttpStatusCode.Created)).Id();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => app.Admin.Post($"/api/pay-periods/{period}/close")));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(results, r => Assert.True((int)r.StatusCode < 500));
        Assert.Equal(1, await app.DbAsync(db => db.OutboxMessages.CountAsync()));
    }
}
