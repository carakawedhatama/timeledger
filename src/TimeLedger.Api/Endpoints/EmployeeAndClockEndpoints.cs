using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using TimeLedger.Api.Auth;
using TimeLedger.Api.Contracts;
using TimeLedger.Api.Data;
using TimeLedger.Api.Infrastructure;
using TimeLedger.Domain;
using TimeLedger.Domain.Entities;
using TimeLedger.Domain.Rules;

namespace TimeLedger.Api.Endpoints;

public static class EmployeeAndClockEndpoints
{
    private const decimal MaxMonthlySalary = 100_000_000_000m;

    public static IEndpointRouteBuilder MapEmployeeAndClockEndpoints(this IEndpointRouteBuilder app)
    {
        var employees = app.MapGroup("/api/employees").WithTags("Employees");

        employees.MapGet("/", async (CurrentUser me, TimeLedgerDbContext db, CancellationToken ct) =>
                (await me.VisibleEmployees(db).AsNoTracking().OrderBy(e => e.EmployeeCode).ToListAsync(ct))
                .Select(e => EmployeeDto.From(e, me.Actor)))
            .RequireAuthorization(Policies.SignedIn)
            .WithSummary("List the employees you may see. Salary is included only for yourself, or for payroll admins.");

        employees.MapGet("/{id:guid}", async (Guid id, CurrentUser me, TimeLedgerDbContext db, CancellationToken ct) =>
                await me.VisibleEmployees(db).AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct) is { } e
                    ? Results.Ok(EmployeeDto.From(e, me.Actor))
                    : Results.NotFound())
            .RequireAuthorization(Policies.SignedIn)
            .Produces<EmployeeDto>()
            .WithSummary("Get one employee.");

        employees.MapPost("/", CreateEmployeeAsync)
            .RequireAuthorization(Policies.PayrollAdmin)
            .Produces<EmployeeDto>(201)
            .WithSummary("Create an employee.");

        employees.MapPut("/{id:guid}/supervisor", SetSupervisorAsync)
            .RequireAuthorization(Policies.PayrollAdmin)
            .Produces<EmployeeDto>()
            .WithSummary("Set or clear who supervises this employee.");

        employees.MapGet("/{id:guid}/clock-events", GetPunchesAsync)
            .RequireAuthorization(Policies.SignedIn)
            .WithTags("Clock")
            .Produces<IEnumerable<ClockEventDto>>()
            .WithSummary("An employee's punches between two local dates (default: last 7 days).");

        app.MapPost("/api/clock-events", RecordPunchAsync)
            .RequireAuthorization(Policies.SignedIn)
            .WithTags("Clock")
            .Produces<ClockEventDto>(201)
            .Produces<ClockEventDto>(200)
            .ProducesProblem(409)
            .ProducesProblem(422)
            .WithSummary("Clock in or out. Requires an Idempotency-Key header; a retried request returns the original result.");

        app.MapGet("/api/presence", GetPresenceAsync)
            .RequireAuthorization(Policies.SignedIn)
            .WithTags("Clock")
            .WithSummary("Who is clocked in right now (served from Redis, rebuilt from PostgreSQL on a cache miss).");

        return app;
    }

    private static async Task<IResult> CreateEmployeeAsync(
        CreateEmployeeRequest req, CurrentUser me, TimeLedgerDbContext db, PresenceCache presence, TimeProvider time, CancellationToken ct)
    {
        var errors = Validate(req);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var code = req.EmployeeCode!.Trim();
        var email = req.Email!.Trim().ToLowerInvariant();
        if (await db.Employees.AnyAsync(e => e.EmployeeCode == code || e.Email == email, ct))
        {
            return Results.Problem(statusCode: 409, title: "An employee with this code or email already exists.");
        }

        if (req.SupervisorId is { } supervisorId && !await db.Employees.AnyAsync(e => e.Id == supervisorId && e.IsActive, ct))
        {
            return Checks.Invalid("supervisorId", "Active employee not found.");
        }

        var employee = new Employee
        {
            EmployeeCode = code,
            FullName = req.FullName!.Trim(),
            Email = email,
            MonthlyBaseSalary = req.MonthlyBaseSalary,
            Schedule = req.Schedule,
            TimeZoneId = req.TimeZoneId!,
            SupervisorId = req.SupervisorId,
            CreatedAt = time.GetUtcNow(),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync(ct);
        await presence.InvalidateAsync();
        return Results.Created($"/api/employees/{employee.Id}", EmployeeDto.From(employee, me.Actor));
    }

    private static async Task<IResult> SetSupervisorAsync(
        Guid id, SetSupervisorRequest req, CurrentUser me, TimeLedgerDbContext db, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockLedgerAsync(ct);

        var employee = await db.Employees.FindAsync([id], ct);
        if (employee is null)
        {
            return Results.NotFound();
        }

        if (req.SupervisorId is { } supervisorId)
        {
            var supervisors = await db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, ct);
            if (!supervisors.TryGetValue(supervisorId, out var candidate) || !candidate.IsActive)
            {
                return Checks.Invalid("supervisorId", "Active employee not found.");
            }

            // A loop (A supervises B supervises A) would let two people approve each other's leave.
            var seen = new HashSet<Guid>();
            for (Guid? next = supervisorId; next is { } current && seen.Add(current); next = supervisors[current].SupervisorId)
            {
                if (current == id)
                {
                    return Results.Problem(statusCode: 422, title: "That would make the employee their own supervisor, directly or through the chain.");
                }
            }
        }

        employee.SupervisorId = req.SupervisorId;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(EmployeeDto.From(employee, me.Actor));
    }

    private static async Task<IResult> GetPunchesAsync(
        Guid id, DateOnly? from, DateOnly? to, CurrentUser me, TimeLedgerDbContext db, TimeProvider time, CancellationToken ct)
    {
        var end = to ?? time.LocalToday();
        if (!Checks.Date(end) || (from is { } f && !Checks.Date(f)))
        {
            return Checks.BadDate();
        }

        if (!await me.VisibleEmployees(db).AnyAsync(e => e.Id == id, ct))
        {
            return Results.NotFound();
        }

        var start = from ?? end.AddDays(-7);
        var fromUtc = Clock.ToUtc(start, TimeOnly.MinValue, Clock.HomeTimeZone);
        var toUtc = Clock.ToUtc(end.AddDays(1), TimeOnly.MinValue, Clock.HomeTimeZone);

        var events = await db.ClockEvents.AsNoTracking()
            .Where(c => c.EmployeeId == id && c.OccurredAt >= fromUtc && c.OccurredAt < toUtc)
            .OrderBy(c => c.OccurredAt).ThenBy(c => c.CreatedAt)
            .ToListAsync(ct);
        return Results.Ok(events.Select(ClockEventDto.From));
    }

    private static async Task<IResult> RecordPunchAsync(
        ClockEventRequest req, HttpContext http, CurrentUser me, TimeLedgerDbContext db, IdempotencyStore idempotency,
        PresenceCache presence, TimeProvider time, ILoggerFactory logs, CancellationToken ct)
    {
        var key = http.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 100)
        {
            return Results.Problem(statusCode: 400, title: "An Idempotency-Key header (1–100 characters) is required.");
        }

        if (!Enum.IsDefined(req.Type) || !Enum.IsDefined(req.Source))
        {
            return Checks.Invalid("type", "A valid type (and source, if given) is required.");
        }

        if (req.OccurredAt is { } at && !Checks.Year(at.Year))
        {
            return Checks.BadDate();
        }

        // Before the idempotency lookup, so a replay can never show someone another employee's punch.
        if (!AccessRules.CanActFor(me.Actor, req.EmployeeId))
        {
            return Checks.Forbidden("You can only clock yourself in or out.");
        }

        if (req.OccurredAt is not null && !AccessRules.CanSetPunchTime(me.Actor, req.EmployeeId))
        {
            return Checks.Forbidden("Only a payroll admin can set a punch's time, and not for their own attendance.");
        }

        // Keys belong to an employee: two people choosing the same key are two different punches.
        var claim = await idempotency.TryBeginAsync($"clock:{req.EmployeeId:N}", key);
        switch (claim.State)
        {
            case IdempotencyState.InFlight:
                return Results.Problem(statusCode: 409, title: "A request with this Idempotency-Key is still being processed.");
            case IdempotencyState.Completed:
                return Replay(http, req, JsonSerializer.Deserialize<ClockEventDto>(claim.StoredResponse!, ApiJson.Web)!);
        }

        ClockEventDto saved;
        bool replayed;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.LockEmployeeAsync(req.EmployeeId, ct);

            // Looked up before the sequence rules, which would call a retried clock-in "already clocked in".
            var previous = await db.ClockEvents.AsNoTracking()
                .FirstOrDefaultAsync(c => c.EmployeeId == req.EmployeeId && c.IdempotencyKey == key, ct);
            replayed = previous is not null;

            if (previous is null)
            {
                var (error, punch) = await AddPunchAsync(req, key, db, time, ct);
                if (error is not null)
                {
                    await idempotency.AbandonAsync(claim.RedisKey);
                    return error;
                }

                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                previous = punch;
            }

            saved = ClockEventDto.From(previous!);
        }
        catch
        {
            await idempotency.AbandonAsync(claim.RedisKey);
            throw;
        }

        var json = JsonSerializer.Serialize(saved, ApiJson.Web);
        try
        {
            if (!replayed)
            {
                await presence.InvalidateAsync();
            }

            await idempotency.CompleteAsync(claim.RedisKey, json);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // The punch is committed, so this is a success. PostgreSQL answers any retry.
            logs.CreateLogger(nameof(EmployeeAndClockEndpoints)).LogWarning(ex, "Redis update failed after punch {PunchId}", saved.Id);
        }

        return replayed ? Replay(http, req, saved) : Results.Content(json, "application/json", statusCode: 201);
    }

    /// <summary>Validates and adds a punch. The caller holds the employee lock.</summary>
    private static async Task<(IResult? Error, ClockEvent? Punch)> AddPunchAsync(
        ClockEventRequest req, string key, TimeLedgerDbContext db, TimeProvider time, CancellationToken ct)
    {
        var employee = await db.Employees.FindAsync([req.EmployeeId], ct);
        if (employee is null || !employee.IsActive)
        {
            return (Results.Problem(statusCode: 404, title: "Active employee not found."), null);
        }

        var now = time.GetUtcNow();
        var occurredAt = ToStoredPrecision(req.OccurredAt ?? now);
        if (occurredAt > now.AddMinutes(5))
        {
            return (Results.Problem(statusCode: 422, title: "A punch cannot be in the future."), null);
        }

        var last = await db.ClockEvents.AsNoTracking()
            .Where(c => c.EmployeeId == employee.Id)
            .OrderByDescending(c => c.OccurredAt).ThenByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (ClockRules.Validate(last?.Type, last?.OccurredAt, req.Type, occurredAt) is { } error)
        {
            return (Results.Problem(statusCode: 422, title: error), null);
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(employee.TimeZoneId);
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(occurredAt, zone).DateTime);
        if (await db.PayPeriods.AnyAsync(p => p.Status == PayPeriodStatus.Closed && p.StartDate <= day && p.EndDate >= day, ct))
        {
            return (Results.Problem(statusCode: 422, title: "That day is in a closed pay period."), null);
        }

        var punch = new ClockEvent
        {
            EmployeeId = employee.Id,
            Type = req.Type,
            OccurredAt = occurredAt,
            Source = req.Source,
            IdempotencyKey = key,
            CreatedAt = now,
        };
        db.ClockEvents.Add(punch);
        return (null, punch);
    }

    private static IResult Replay(HttpContext http, ClockEventRequest req, ClockEventDto stored)
    {
        if (stored.Type != req.Type || (req.OccurredAt is { } at && ToStoredPrecision(at) != ToStoredPrecision(stored.OccurredAt)))
        {
            return Results.Problem(statusCode: 422, title: "This Idempotency-Key was already used for a different punch.");
        }

        http.Response.Headers["Idempotent-Replayed"] = "true";
        return Results.Content(JsonSerializer.Serialize(stored, ApiJson.Web), "application/json", statusCode: 200);
    }

    /// <summary>timestamptz holds microseconds; truncating first makes a replay identical to the original response.</summary>
    private static DateTimeOffset ToStoredPrecision(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMicrosecond), TimeSpan.Zero);

    private static async Task<IReadOnlyList<PresenceEntry>> GetPresenceAsync(
        CurrentUser me, TimeLedgerDbContext db, PresenceCache presence, CancellationToken ct)
    {
        var generation = await presence.GetGenerationAsync();
        var everyone = await presence.GetAsync(generation);
        if (everyone is null)
        {
            everyone = await LoadPresenceAsync(db, ct);
            await presence.SetAsync(generation, everyone);
        }

        // One shared snapshot, filtered per caller.
        var visible = (await me.VisibleEmployees(db).Select(e => e.Id).ToListAsync(ct)).ToHashSet();
        return everyone.Where(e => visible.Contains(e.EmployeeId)).ToList();
    }

    private static async Task<IReadOnlyList<PresenceEntry>> LoadPresenceAsync(TimeLedgerDbContext db, CancellationToken ct)
    {
        var rows = await db.Employees.AsNoTracking()
            .Where(e => e.IsActive)
            .Select(e => new
            {
                e.Id,
                e.EmployeeCode,
                e.FullName,
                Last = db.ClockEvents
                    .Where(c => c.EmployeeId == e.Id)
                    .OrderByDescending(c => c.OccurredAt).ThenByDescending(c => c.CreatedAt)
                    .Select(c => new { c.Type, c.OccurredAt })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        return rows
            .Select(r => new PresenceEntry(r.Id, r.EmployeeCode, r.FullName,
                r.Last is not null && r.Last.Type == ClockEventType.ClockIn ? "in" : "out",
                r.Last?.OccurredAt))
            .OrderBy(e => e.EmployeeCode)
            .ToList();
    }

    private static Dictionary<string, string[]> Validate(CreateEmployeeRequest req)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(req.EmployeeCode) || req.EmployeeCode.Length > 32)
        {
            errors["employeeCode"] = ["Required, at most 32 characters."];
        }

        if (string.IsNullOrWhiteSpace(req.FullName) || req.FullName.Length > 200)
        {
            errors["fullName"] = ["Required, at most 200 characters."];
        }

        if (!Checks.Email(req.Email))
        {
            errors["email"] = ["A valid email address is required."];
        }

        if (req.MonthlyBaseSalary is <= 0 or > MaxMonthlySalary)
        {
            errors["monthlyBaseSalary"] = [$"Must be greater than zero and at most {MaxMonthlySalary:0}."];
        }

        if (!Enum.IsDefined(req.Schedule))
        {
            errors["schedule"] = ["Unknown work schedule."];
        }

        if (req.TimeZoneId is not { Length: > 0 and <= 64 } || !TimeZoneInfo.TryFindSystemTimeZoneById(req.TimeZoneId, out _))
        {
            errors["timeZoneId"] = ["Unknown time zone."];
        }

        return errors;
    }
}
