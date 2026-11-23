using System.Security.Cryptography;
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

public static class PayrollEndpoints
{
    public static IEndpointRouteBuilder MapPayrollEndpoints(this IEndpointRouteBuilder app)
    {
        var periods = app.MapGroup("/api/pay-periods").WithTags("Pay periods");

        periods.MapGet("/", async (TimeLedgerDbContext db, CancellationToken ct) =>
                (await db.PayPeriods.AsNoTracking().OrderByDescending(p => p.StartDate).ToListAsync(ct)).Select(PayPeriodDto.From))
            .RequireAuthorization(Policies.SignedIn)
            .WithSummary("List pay periods, newest first.");

        periods.MapPost("/", async (CreatePayPeriodRequest req, TimeLedgerDbContext db, TimeProvider time, CancellationToken ct) =>
            {
                if (!Checks.Date(req.StartDate) || !Checks.Date(req.EndDate))
                {
                    return Checks.BadDate();
                }

                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await db.LockLedgerAsync(ct);

                var period = PayPeriod.Create(req.StartDate, req.EndDate, time.GetUtcNow());
                if (await db.PayPeriods.AnyAsync(p => p.StartDate <= req.EndDate && p.EndDate >= req.StartDate, ct))
                {
                    return Results.Problem(statusCode: 409, title: "This overlaps an existing pay period.");
                }

                db.PayPeriods.Add(period);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return Results.Created($"/api/pay-periods/{period.Id}", PayPeriodDto.From(period));
            })
            .RequireAuthorization(Policies.PayrollAdmin)
            .Produces<PayPeriodDto>(201)
            .WithSummary("Open a pay period (max 31 days, no overlaps).");

        periods.MapGet("/{id:guid}/timesheets", async (Guid id, CurrentUser me, TimeLedgerDbContext db, TimesheetService timesheets, TimeProvider time, CancellationToken ct) =>
            {
                var period = await db.PayPeriods.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
                if (period is null)
                {
                    return Results.NotFound();
                }

                var isFinal = period.Status == PayPeriodStatus.Closed;
                var scope = me.VisibleEmployees(db);
                var sheets = isFinal
                    ? await timesheets.LoadFrozenAsync(period, scope, ct)
                    : await timesheets.BuildAsync(period, scope, time.LocalToday(), ct);

                var visible = sheets.Select(s => TimesheetService.ToDto(s, AccessRules.CanViewPay(me.Actor, s.Employee.Id))).ToList();
                return Results.Ok(new PeriodTimesheetsDto(PayPeriodDto.From(period), isFinal, visible));
            })
            .RequireAuthorization(Policies.SignedIn)
            .Produces<PeriodTimesheetsDto>()
            .WithSummary("Timesheets for a period: a live preview while open, frozen lines once closed.");

        periods.MapPost("/{id:guid}/close", async (Guid id, TimeLedgerDbContext db, TimesheetService timesheets, RedisLock locks, TimeProvider time, CancellationToken ct) =>
            {
                // The Redis lock keeps two operators from computing a close at the same time;
                // the ledger lock below, and xmin, are the backstops if it ever expires mid-close.
                await using var handle = await locks.TryAcquireAsync($"pay-period:{id}", TimeSpan.FromMinutes(2));
                if (handle is null)
                {
                    return Results.Problem(statusCode: 409, title: "This pay period is already being closed.");
                }

                await using var tx = await db.Database.BeginTransactionAsync(ct);

                // Waits for punches and leave decisions in flight, and holds off new ones until the freeze commits.
                await db.LockLedgerAsync(ct);

                var period = await db.PayPeriods.FindAsync([id], ct);
                if (period is null)
                {
                    return Results.NotFound();
                }

                if (period.Status == PayPeriodStatus.Closed)
                {
                    return Results.Problem(statusCode: 422, title: "Pay period is already closed.");
                }

                var now = time.GetUtcNow();
                if (await FindCloseBlockerAsync(db, period, now, ct) is { } blocker)
                {
                    return Results.Problem(statusCode: 422, title: blocker);
                }

                // Everything up to and including the last day is in the past, so absences are final.
                var sheets = await timesheets.BuildAsync(period, db.Employees, period.EndDate.AddDays(1), ct);

                foreach (var sheet in sheets)
                {
                    db.TimesheetEntries.AddRange(sheet.Days.Select(d => new TimesheetEntry
                    {
                        PayPeriodId = period.Id,
                        EmployeeId = sheet.Employee.Id,
                        WorkDate = d.Date,
                        DayKind = d.Kind,
                        WorkedMinutes = d.WorkedMinutes,
                        RegularMinutes = d.RegularMinutes,
                        OvertimeMinutes = d.OvertimeMinutes,
                        OvertimePay = d.OvertimePay,
                        LeaveType = d.Leave,
                        Anomalies = d.Anomalies.ToList(),
                    }));
                }

                period.Close(now);

                var evt = new PayrollPeriodClosedEvent(period.Id, period.StartDate, period.EndDate, now, "IDR",
                    sheets.Select(s => new EmployeePayrollLine(
                        s.Employee.Id, s.Employee.EmployeeCode, s.Employee.FullName,
                        RegularHours: Math.Round(s.Summary.RegularMinutes / 60m, 2),
                        OvertimeHours: Math.Round(s.Summary.OvertimeMinutes / 60m, 2),
                        s.Summary.OvertimePay,
                        s.Summary.AnnualLeaveDays, s.Summary.SickLeaveDays, s.Summary.UnpaidLeaveDays,
                        s.Summary.AnomalyCount,
                        NormalDailyHours: SchedulePolicy.NormalDailyMinutes(s.Employee.Schedule) / 60m)).ToList());

                db.OutboxMessages.Add(OutboxMessage.Create(EventTypes.PayrollPeriodClosed, period.Id,
                    JsonSerializer.Serialize(evt, ApiJson.Webhook), now));

                // Frozen lines, the status change and the outbox event commit in one transaction.
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                var closed = sheets.Select(s => TimesheetService.ToDto(s, showPay: true)).ToList();
                return Results.Ok(new PeriodTimesheetsDto(PayPeriodDto.From(period), true, closed));
            })
            .RequireAuthorization(Policies.PayrollAdmin)
            .Produces<PeriodTimesheetsDto>()
            .ProducesProblem(409)
            .ProducesProblem(422)
            .WithSummary("Freeze timesheets and publish payroll.period_closed to subscribed payroll integrations.");

        MapIntegrations(app);
        return app;
    }

    /// <summary>Why this period can't be frozen yet, or null.</summary>
    private static async Task<string?> FindCloseBlockerAsync(TimeLedgerDbContext db, PayPeriod period, DateTimeOffset now, CancellationToken ct)
    {
        const string NotOver = "A pay period can only be closed after its last day.";
        if (DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Clock.HomeTimeZone).DateTime) <= period.EndDate)
        {
            return NotOver;
        }

        var staff = await db.Employees.AsNoTracking()
            .Where(e => e.IsActive)
            .Select(e => new
            {
                e.EmployeeCode,
                e.TimeZoneId,
                Last = db.ClockEvents
                    .Where(c => c.EmployeeId == e.Id)
                    .OrderByDescending(c => c.OccurredAt).ThenByDescending(c => c.CreatedAt)
                    .Select(c => new { c.Type, c.OccurredAt })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        var working = new List<string>();
        foreach (var person in staff)
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(person.TimeZoneId);
            DateOnly LocalDate(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);

            if (LocalDate(now) <= period.EndDate)
            {
                return $"{NotOver} It is still that day for {person.EmployeeCode} ({person.TimeZoneId}).";
            }

            // A night shift still running would be frozen at zero minutes and never paid. Past the suspicious-shift
            // length it is a forgotten clock-out, which is an anomaly and shouldn't hold payroll up.
            if (person.Last is { Type: ClockEventType.ClockIn } last
                && (now - last.OccurredAt).TotalMinutes <= TimesheetCalculator.SuspiciousShiftMinutes
                && LocalDate(last.OccurredAt) >= period.StartDate && LocalDate(last.OccurredAt) <= period.EndDate)
            {
                working.Add(person.EmployeeCode);
            }
        }

        return working.Count == 0
            ? null
            : $"A shift that started in this period is still running for {string.Join(", ", working)}. Close after they clock out.";
    }

    private static void MapIntegrations(IEndpointRouteBuilder app)
    {
        var integrations = app.MapGroup("/api/integrations").WithTags("Integrations").RequireAuthorization(Policies.PayrollAdmin);

        integrations.MapGet("/", async (TimeLedgerDbContext db, CancellationToken ct) =>
                (await db.IntegrationConnections.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct)).Select(IntegrationDto.From))
            .WithSummary("List integration connections (secrets are masked).");

        integrations.MapPost("/", async (CreateIntegrationRequest req, TimeLedgerDbContext db, TimeProvider time, CancellationToken ct) =>
            {
                var errors = new Dictionary<string, string[]>();
                var name = req.Name?.Trim();
                if (string.IsNullOrEmpty(name) || name.Length > 200)
                {
                    errors["name"] = ["Required, at most 200 characters."];
                }

                if (!Enum.IsDefined(req.Provider))
                {
                    errors["provider"] = ["A valid provider is required."];
                }

                if (req.EndpointUrl is not { Length: <= 2000 } || !Uri.TryCreate(req.EndpointUrl, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                {
                    errors["endpointUrl"] = ["Must be an absolute http(s) URL of at most 2000 characters."];
                }

                var eventTypes = req.EventTypes ?? [];
                if (eventTypes.Count == 0 || eventTypes.Except(EventTypes.All).Any())
                {
                    errors["eventTypes"] = [$"Choose one or more of: {string.Join(", ", EventTypes.All)}."];
                }

                if (!string.IsNullOrWhiteSpace(req.Secret) && req.Secret.Length is < IntegrationDto.MinSecretLength or > 200)
                {
                    errors["secret"] = [$"Must be {IntegrationDto.MinSecretLength}–200 characters, or omitted to generate one."];
                }

                if (errors.Count > 0)
                {
                    return Results.ValidationProblem(errors);
                }

                if (await db.IntegrationConnections.AnyAsync(c => c.Name == name, ct))
                {
                    return Results.Problem(statusCode: 409, title: "An integration with this name already exists.");
                }

                var secret = string.IsNullOrWhiteSpace(req.Secret)
                    ? "whsec_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()
                    : req.Secret;

                var connection = new IntegrationConnection
                {
                    Name = name!,
                    Provider = req.Provider,
                    EndpointUrl = req.EndpointUrl!,
                    Secret = secret,
                    EventTypes = eventTypes.Distinct().ToList(),
                    CreatedAt = time.GetUtcNow(),
                };
                db.IntegrationConnections.Add(connection);
                await db.SaveChangesAsync(ct);

                return Results.Created($"/api/integrations/{connection.Id}", new CreatedIntegrationDto(IntegrationDto.From(connection), secret));
            })
            .Produces<CreatedIntegrationDto>(201)
            .WithSummary("Connect a payroll provider or webhook. The signing secret is returned only once.");

        integrations.MapPost("/{id:guid}/enabled", async (Guid id, bool enabled, TimeLedgerDbContext db, CancellationToken ct) =>
            {
                var connection = await db.IntegrationConnections.FindAsync([id], ct);
                if (connection is null)
                {
                    return Results.NotFound();
                }

                connection.IsEnabled = enabled;
                await db.SaveChangesAsync(ct);
                return Results.Ok(IntegrationDto.From(connection));
            })
            .WithSummary("Pause or resume a connection (?enabled=true|false).");

        app.MapGet("/api/deliveries", async (DeliveryStatus? status, Guid? connectionId, TimeLedgerDbContext db, CancellationToken ct) =>
            {
                var query =
                    from d in db.WebhookDeliveries.AsNoTracking()
                    join c in db.IntegrationConnections.AsNoTracking() on d.ConnectionId equals c.Id
                    join m in db.OutboxMessages.AsNoTracking() on d.OutboxMessageId equals m.Id
                    where (status == null || d.Status == status) && (connectionId == null || d.ConnectionId == connectionId)
                    orderby d.CreatedAt descending
                    select new DeliveryDto(d.Id, d.ConnectionId, c.Name, m.Type, d.Status, d.Attempts, d.NextAttemptAt,
                        d.LastStatusCode, d.LastError, d.CreatedAt, d.CompletedAt);
                return await query.Take(100).ToListAsync(ct);
            })
            .RequireAuthorization(Policies.PayrollAdmin)
            .WithTags("Integrations")
            .WithSummary("Webhook delivery log, newest first.");

        app.MapPost("/api/deliveries/{id:guid}/retry", async (Guid id, TimeLedgerDbContext db, TimeProvider time, CancellationToken ct) =>
            {
                var delivery = await db.WebhookDeliveries.FindAsync([id], ct);
                if (delivery is null)
                {
                    return Results.NotFound();
                }

                delivery.Requeue(time.GetUtcNow());
                await db.SaveChangesAsync(ct);
                return Results.Accepted();
            })
            .RequireAuthorization(Policies.PayrollAdmin)
            .WithTags("Integrations")
            .WithSummary("Requeue a dead-lettered delivery with a fresh retry budget.");
    }
}
