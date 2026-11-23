using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TimeLedger.Api.Auth;
using TimeLedger.Api.Contracts;
using TimeLedger.Api.Data;
using TimeLedger.Api.Infrastructure;
using TimeLedger.Domain;
using TimeLedger.Domain.Entities;
using TimeLedger.Domain.Integration;
using TimeLedger.Domain.Rules;

namespace TimeLedger.Api.Endpoints;

public static class LeaveEndpoints
{
    private const int MaxRangeDays = 366;
    private const int MaxTextLength = 500;

    public static IEndpointRouteBuilder MapLeaveEndpoints(this IEndpointRouteBuilder app)
    {
        var leave = app.MapGroup("/api/leave-requests").WithTags("Leave");

        leave.MapGet("/", async (LeaveStatus? status, CurrentUser me, TimeLedgerDbContext db, CancellationToken ct) =>
            {
                var query =
                    from l in db.LeaveRequests.AsNoTracking()
                    join e in me.VisibleEmployees(db).AsNoTracking() on l.EmployeeId equals e.Id
                    where status == null || l.Status == status
                    orderby l.CreatedAt descending
                    select new LeaveRequestDto(l.Id, l.EmployeeId, e.EmployeeCode, e.FullName, l.Type, l.StartDate,
                        l.EndDate, l.Days, l.Status, l.Reason, l.DecidedBy, l.DecisionNote, l.DecidedAt, l.CreatedAt);
                return await query.Take(200).ToListAsync(ct);
            })
            .RequireAuthorization(Policies.SignedIn)
            .WithSummary("List the leave requests you may see, optionally filtered by status.");

        leave.MapPost("/", RequestLeaveAsync)
            .RequireAuthorization(Policies.SignedIn)
            .Produces<LeaveRequestDto>(201)
            .WithSummary("Request leave. Days are counted as working days, excluding rest days and public holidays.");

        leave.MapPost("/{id:guid}/approve", ApproveAsync)
            .RequireAuthorization(Policies.LeaveApprover)
            .Produces<LeaveRequestDto>()
            .ProducesProblem(403)
            .ProducesProblem(409)
            .WithSummary("Approve a pending request, deduct annual balance, and publish leave.approved.");

        leave.MapPost("/{id:guid}/reject", RejectAsync)
            .RequireAuthorization(Policies.LeaveApprover)
            .Produces<LeaveRequestDto>()
            .ProducesProblem(403)
            .ProducesProblem(409)
            .WithSummary("Reject a pending request.");

        app.MapGet("/api/employees/{id:guid}/leave-balance", async (Guid id, int? year, CurrentUser me, TimeLedgerDbContext db, TimeProvider time, CancellationToken ct) =>
            {
                var y = year ?? time.LocalToday().Year;
                if (!Checks.Year(y))
                {
                    return Checks.BadDate();
                }

                if (!await me.VisibleEmployees(db).AnyAsync(e => e.Id == id, ct))
                {
                    return Results.NotFound();
                }

                var balance = await db.LeaveBalances.AsNoTracking().FirstOrDefaultAsync(b => b.EmployeeId == id && b.Year == y, ct)
                              ?? new LeaveBalance { EmployeeId = id, Year = y };
                return Results.Ok(new LeaveBalanceDto(id, y, balance.EntitledDays, balance.UsedDays, balance.RemainingDays));
            })
            .RequireAuthorization(Policies.SignedIn)
            .Produces<LeaveBalanceDto>()
            .WithTags("Leave")
            .WithSummary("Annual leave balance for a year (default: current year).");

        var holidays = app.MapGroup("/api/holidays").WithTags("Holidays");

        holidays.MapGet("/", async (int? year, TimeLedgerDbContext db, TimeProvider time, CancellationToken ct) =>
            {
                var y = year ?? time.LocalToday().Year;
                if (!Checks.Year(y))
                {
                    return Checks.BadDate();
                }

                return Results.Ok(await db.PublicHolidays.AsNoTracking()
                    .Where(h => h.Date >= new DateOnly(y, 1, 1) && h.Date <= new DateOnly(y, 12, 31))
                    .OrderBy(h => h.Date)
                    .Select(h => new HolidayDto(h.Date, h.Name))
                    .ToListAsync(ct));
            })
            .RequireAuthorization(Policies.SignedIn)
            .Produces<IEnumerable<HolidayDto>>();

        holidays.MapPut("/{date}", async (DateOnly date, HolidayDto body, TimeLedgerDbContext db, CancellationToken ct) =>
            {
                if (!Checks.Date(date))
                {
                    return Checks.BadDate();
                }

                if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 200)
                {
                    return Checks.Invalid("name", "Required, at most 200 characters.");
                }

                var existing = await db.PublicHolidays.FindAsync([date], ct);
                if (existing is null)
                {
                    db.PublicHolidays.Add(new PublicHoliday { Date = date, Name = body.Name });
                }
                else
                {
                    existing.Name = body.Name;
                }

                await db.SaveChangesAsync(ct);
                return Results.Ok(new HolidayDto(date, body.Name));
            })
            .RequireAuthorization(Policies.PayrollAdmin)
            .WithSummary("Add or rename a public holiday (cuti bersama can be added here too).");

        return app;
    }

    private static async Task<IResult> RequestLeaveAsync(
        CreateLeaveRequest req, CurrentUser me, TimeLedgerDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (!Enum.IsDefined(req.Type))
        {
            return Checks.Invalid("type", "A valid leave type is required.");
        }

        if (req.Reason?.Length > MaxTextLength)
        {
            return Checks.Invalid("reason", $"At most {MaxTextLength} characters.");
        }

        if (!Checks.Date(req.StartDate) || !Checks.Date(req.EndDate))
        {
            return Checks.BadDate();
        }

        if (req.EndDate < req.StartDate)
        {
            return Results.Problem(statusCode: 422, title: "End date must be on or after the start date.");
        }

        if (req.EndDate.DayNumber - req.StartDate.DayNumber >= MaxRangeDays)
        {
            return Results.Problem(statusCode: 422, title: $"A request can span at most {MaxRangeDays} days.");
        }

        if (!AccessRules.CanActFor(me.Actor, req.EmployeeId))
        {
            return Checks.Forbidden("You can only request leave for yourself.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockEmployeeAsync(req.EmployeeId, ct);

        var employee = await db.Employees.FindAsync([req.EmployeeId], ct);
        if (employee is null || !employee.IsActive)
        {
            return Results.Problem(statusCode: 404, title: "Active employee not found.");
        }

        if (await TouchesClosedPeriodAsync(db, req.StartDate, req.EndDate, ct))
        {
            return Results.Problem(statusCode: 422, title: "These dates are in a closed pay period.");
        }

        var overlapping = await db.LeaveRequests.AnyAsync(l =>
            l.EmployeeId == req.EmployeeId &&
            (l.Status == LeaveStatus.Pending || l.Status == LeaveStatus.Approved) &&
            l.StartDate <= req.EndDate && l.EndDate >= req.StartDate, ct);
        if (overlapping)
        {
            return Results.Problem(statusCode: 409, title: "This overlaps an existing pending or approved request.");
        }

        var holidays = await LoadHolidaysAsync(db, req.StartDate, req.EndDate, ct);
        var byYear = LeaveCalendar.CountDaysByYear(req.StartDate, req.EndDate, employee.Schedule, holidays);
        if (byYear.Count == 0)
        {
            return Results.Problem(statusCode: 422, title: "The range contains no working days.");
        }

        if (req.Type == LeaveType.Annual)
        {
            foreach (var (year, days) in byYear)
            {
                var balance = await db.LeaveBalances.FindAsync([employee.Id, year], ct);
                var remaining = balance?.RemainingDays ?? LeaveBalance.StatutoryAnnualDays;
                if (days > remaining)
                {
                    return Results.Problem(statusCode: 422, title: $"Insufficient annual leave for {year}: {remaining} day(s) remaining, {days} requested.");
                }
            }
        }

        var request = new LeaveRequest
        {
            EmployeeId = employee.Id,
            Type = req.Type,
            StartDate = req.StartDate,
            EndDate = req.EndDate,
            Days = byYear.Values.Sum(),
            Reason = req.Reason?.Trim(),
            CreatedAt = time.GetUtcNow(),
        };
        db.LeaveRequests.Add(request);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Results.Created($"/api/leave-requests/{request.Id}", ToDto(request, employee));
    }

    private static async Task<IResult> ApproveAsync(
        Guid id, DecideLeaveRequest? req, CurrentUser me, TimeLedgerDbContext db, TimeProvider time, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (error, request, employee) = await LoadForDecisionAsync(id, req, me, db, ct);
        if (error is not null)
        {
            return error;
        }

        if (request!.Status == LeaveStatus.Pending && await TouchesClosedPeriodAsync(db, request.StartDate, request.EndDate, ct))
        {
            return Results.Problem(statusCode: 422, title: "These dates are in a closed pay period. The request can only be rejected.");
        }

        var now = time.GetUtcNow();

        // First, so a request that was already decided says so, rather than "insufficient balance".
        request.Approve(me.Email, now);

        // Recounted: a holiday may have been added since the request was made.
        var holidays = await LoadHolidaysAsync(db, request.StartDate, request.EndDate, ct);
        var byYear = LeaveCalendar.CountDaysByYear(request.StartDate, request.EndDate, employee!.Schedule, holidays);
        if (byYear.Count == 0)
        {
            return Results.Problem(statusCode: 422, title: "The range no longer contains any working days.");
        }

        request.Days = byYear.Values.Sum();

        if (request.Type == LeaveType.Annual)
        {
            foreach (var (year, days) in byYear)
            {
                var balance = await db.LeaveBalances.FindAsync([employee.Id, year], ct);
                if (balance is null)
                {
                    balance = new LeaveBalance { EmployeeId = employee.Id, Year = year };
                    db.LeaveBalances.Add(balance);
                }

                // Throws DomainException (→ 422) if the balance has run out since the request was made.
                balance.Consume(days);
            }
        }

        var evt = new LeaveApprovedEvent(request.Id, employee.Id, employee.EmployeeCode, request.Type,
            request.StartDate, request.EndDate, request.Days);
        db.OutboxMessages.Add(OutboxMessage.Create(EventTypes.LeaveApproved, request.Id,
            JsonSerializer.Serialize(evt, ApiJson.Webhook), now));

        // One transaction: balance, request and outbox event commit together.
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(ToDto(request, employee));
    }

    private static async Task<IResult> RejectAsync(
        Guid id, DecideLeaveRequest? req, CurrentUser me, TimeLedgerDbContext db, TimeProvider time, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (error, request, employee) = await LoadForDecisionAsync(id, req, me, db, ct);
        if (error is not null)
        {
            return error;
        }

        request!.Reject(me.Email, req?.Note, time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(ToDto(request, employee!));
    }

    /// <summary>Locks, loads and authorizes a decision. Out-of-scope requests are 404, so ids can't be probed.</summary>
    private static async Task<(IResult? Error, LeaveRequest? Request, Employee? Employee)> LoadForDecisionAsync(
        Guid id, DecideLeaveRequest? req, CurrentUser me, TimeLedgerDbContext db, CancellationToken ct)
    {
        if (req?.Note?.Length > MaxTextLength)
        {
            return (Checks.Invalid("note", $"At most {MaxTextLength} characters."), null, null);
        }

        var before = await db.LeaveRequests.AsNoTracking()
            .Where(l => l.Id == id)
            .Select(l => new { l.EmployeeId, l.Status })
            .FirstOrDefaultAsync(ct);
        if (before is null)
        {
            return (Results.NotFound(), null, null);
        }

        await db.LockEmployeeAsync(before.EmployeeId, ct);

        var employee = await me.VisibleEmployees(db).FirstOrDefaultAsync(e => e.Id == before.EmployeeId, ct);
        if (employee is null)
        {
            return (Results.NotFound(), null, null);
        }

        if (!AccessRules.CanDecideLeave(me.Actor, employee.Id, employee.SupervisorId))
        {
            return (Checks.Forbidden("You cannot decide your own leave."), null, null);
        }

        var request = (await db.LeaveRequests.FindAsync([id], ct))!;

        // Pending when we arrived and decided by the time we had the lock: we lost a race, which is a 409.
        return before.Status == LeaveStatus.Pending && request.Status != LeaveStatus.Pending
            ? (Results.Problem(statusCode: 409, title: "The record was changed by another request. Reload and try again."), null, null)
            : (null, request, employee);
    }

    private static Task<bool> TouchesClosedPeriodAsync(TimeLedgerDbContext db, DateOnly start, DateOnly end, CancellationToken ct) =>
        db.PayPeriods.AnyAsync(p => p.Status == PayPeriodStatus.Closed && p.StartDate <= end && p.EndDate >= start, ct);

    internal static async Task<IReadOnlySet<DateOnly>> LoadHolidaysAsync(TimeLedgerDbContext db, DateOnly start, DateOnly end, CancellationToken ct) =>
        (await db.PublicHolidays.AsNoTracking()
            .Where(h => h.Date >= start && h.Date <= end)
            .Select(h => h.Date)
            .ToListAsync(ct))
        .ToHashSet();

    private static LeaveRequestDto ToDto(LeaveRequest l, Employee e) => new(
        l.Id, l.EmployeeId, e.EmployeeCode, e.FullName, l.Type, l.StartDate, l.EndDate, l.Days, l.Status,
        l.Reason, l.DecidedBy, l.DecisionNote, l.DecidedAt, l.CreatedAt);
}
