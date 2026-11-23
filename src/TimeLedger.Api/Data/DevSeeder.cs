using Microsoft.EntityFrameworkCore;
using TimeLedger.Api.Infrastructure;
using TimeLedger.Domain;
using TimeLedger.Domain.Entities;
using TimeLedger.Domain.Integration;
using TimeLedger.Domain.Rules;

namespace TimeLedger.Api.Data;

/// <summary>
/// Demo data: eight employees, fixed-date Indonesian holidays, last month (closable) and this month
/// (open), realistic punches with a few deliberate anomalies, leave requests, and two integrations
/// pointed at the built-in mock payroll provider.
/// </summary>
public static class DevSeeder
{
    public static async Task SeedAsync(TimeLedgerDbContext db, TimeProvider time, IConfiguration config, CancellationToken ct = default)
    {
        if (await db.Employees.AnyAsync(ct))
        {
            return;
        }

        var now = time.GetUtcNow();
        var today = time.LocalToday();
        var zone = Clock.HomeTimeZone;
        var rng = new Random(42);

        var employees = new[]
        {
            Make("EMP-001", "Ayu Lestari", 12_500_000m, WorkSchedule.FiveDay),
            Make("EMP-002", "Bima Pratama", 9_000_000m, WorkSchedule.FiveDay),
            Make("EMP-003", "Citra Wulandari", 15_000_000m, WorkSchedule.FiveDay),
            Make("EMP-004", "Dimas Saputra", 6_500_000m, WorkSchedule.SixDay),
            Make("EMP-005", "Eka Rahmawati", 8_200_000m, WorkSchedule.FiveDay),
            Make("EMP-006", "Fajar Nugroho", 7_400_000m, WorkSchedule.SixDay),
            Make("EMP-007", "Gita Permata", 11_000_000m, WorkSchedule.FiveDay),
            Make("EMP-008", "Hendra Wijaya", 5_190_000m, WorkSchedule.FiveDay),
        };
        db.Employees.AddRange(employees);

        // Fixed-date national holidays only; moveable ones (Idul Fitri, Nyepi, Waisak…) and
        // cuti bersama are set each year by joint ministerial decree, so add them via PUT /api/holidays.
        // Next year too: in December the seeded leave request reaches into January.
        foreach (var year in new[] { today.Year - 1, today.Year, today.Year + 1 })
        {
            db.PublicHolidays.AddRange(
                new PublicHoliday { Date = new DateOnly(year, 1, 1), Name = "Tahun Baru Masehi" },
                new PublicHoliday { Date = new DateOnly(year, 5, 1), Name = "Hari Buruh Internasional" },
                new PublicHoliday { Date = new DateOnly(year, 6, 1), Name = "Hari Lahir Pancasila" },
                new PublicHoliday { Date = new DateOnly(year, 8, 17), Name = "Hari Kemerdekaan RI" },
                new PublicHoliday { Date = new DateOnly(year, 12, 25), Name = "Hari Raya Natal" });
        }

        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var lastMonth = thisMonth.AddMonths(-1);
        db.PayPeriods.AddRange(
            new PayPeriod { StartDate = lastMonth, EndDate = thisMonth.AddDays(-1), CreatedAt = now },
            new PayPeriod { StartDate = thisMonth, EndDate = thisMonth.AddMonths(1).AddDays(-1), CreatedAt = now });

        await db.SaveChangesAsync(ct);
        var holidays = (await db.PublicHolidays.Select(h => h.Date).ToListAsync(ct)).ToHashSet();

        // Approved sick leave for Citra (Mon–Tue last month) and pending annual leave for Bima (Mon–Wed next week).
        var sickStart = NextMonday(lastMonth.AddDays(7));
        var sick = new LeaveRequest
        {
            EmployeeId = employees[2].Id, Type = LeaveType.Sick, StartDate = sickStart, EndDate = sickStart.AddDays(1),
            Days = LeaveCalendar.CountDays(sickStart, sickStart.AddDays(1), WorkSchedule.FiveDay, holidays),
            Status = LeaveStatus.Approved, Reason = "Demam", DecidedBy = "hr@timeledger.local", DecidedAt = now, CreatedAt = now,
        };
        var annualStart = NextMonday(today.AddDays(7));
        var annual = new LeaveRequest
        {
            EmployeeId = employees[1].Id, Type = LeaveType.Annual, StartDate = annualStart, EndDate = annualStart.AddDays(2),
            Days = LeaveCalendar.CountDays(annualStart, annualStart.AddDays(2), WorkSchedule.FiveDay, holidays),
            Reason = "Pulang kampung", CreatedAt = now,
        };
        db.LeaveRequests.AddRange(sick, annual);

        var leaveDays = LeaveCalendar.ExpandApproved([sick], WorkSchedule.FiveDay, holidays)
            .Keys.Select(d => (sick.EmployeeId, d)).ToHashSet();

        for (var day = lastMonth; day < today; day = day.AddDays(1))
        {
            foreach (var (employee, index) in employees.Select((e, i) => (e, i)))
            {
                var isRest = holidays.Contains(day) || (employee.Schedule == WorkSchedule.FiveDay
                    ? day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
                    : day.DayOfWeek is DayOfWeek.Sunday);

                // Occasional Saturday overtime for Fajar; otherwise no work on rest days.
                var restDayShift = isRest && index == 5 && day.DayOfWeek == DayOfWeek.Saturday && rng.NextDouble() < 0.5;
                if ((isRest && !restDayShift) || leaveDays.Contains((employee.Id, day)))
                {
                    continue;
                }

                // ~2% unexplained absences.
                if (!isRest && rng.NextDouble() < 0.02)
                {
                    continue;
                }

                var inLocal = new TimeOnly(restDayShift ? 9 : 7, 30).AddMinutes(rng.Next(0, 60));
                var normalHours = employee.Schedule == WorkSchedule.FiveDay ? 8 : 7;
                var lengthMinutes = restDayShift ? 240 + rng.Next(0, 120)
                    : (normalHours * 60) + (rng.NextDouble() < 0.3 ? rng.Next(30, 180) : rng.Next(0, 20));

                var clockIn = Clock.ToUtc(day, inLocal, zone);
                db.ClockEvents.Add(new ClockEvent
                {
                    EmployeeId = employee.Id, Type = ClockEventType.ClockIn, OccurredAt = clockIn,
                    Source = index % 3 == 0 ? ClockSource.Mobile : ClockSource.Kiosk, CreatedAt = now,
                });

                // ~1.5% forgotten clock-outs, surfaced as anomalies.
                if (rng.NextDouble() < 0.015)
                {
                    continue;
                }

                db.ClockEvents.Add(new ClockEvent
                {
                    EmployeeId = employee.Id, Type = ClockEventType.ClockOut, OccurredAt = clockIn.AddMinutes(lengthMinutes),
                    Source = index % 3 == 0 ? ClockSource.Mobile : ClockSource.Kiosk, CreatedAt = now,
                });
            }
        }

        // A few people already clocked in this morning, so the presence board has something to show.
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        if (localNow.Hour >= 9 && SchedulePolicy.Classify(WorkSchedule.FiveDay, today, holidays) == DayKind.Workday)
        {
            foreach (var employee in employees.Take(5))
            {
                db.ClockEvents.Add(new ClockEvent
                {
                    EmployeeId = employee.Id, Type = ClockEventType.ClockIn,
                    OccurredAt = Clock.ToUtc(today, new TimeOnly(7, 30).AddMinutes(rng.Next(0, 60)), zone),
                    Source = ClockSource.Kiosk, CreatedAt = now,
                });
            }
        }

        var mockUrl = (config["MockProvider:BaseUrl"] ?? "http://localhost:5080").TrimEnd('/') + "/mock/payroll-provider/webhooks";
        var mockSecret = config["MockProvider:Secret"] ?? "whsec_local_demo_only";
        db.IntegrationConnections.AddRange(
            new IntegrationConnection
            {
                Name = "Payroll provider (Gusto-style)", Provider = IntegrationProvider.GustoStyle, EndpointUrl = mockUrl,
                Secret = mockSecret, EventTypes = [EventTypes.PayrollPeriodClosed], CreatedAt = now,
            },
            new IntegrationConnection
            {
                Name = "HR data warehouse webhook", Provider = IntegrationProvider.GenericWebhook, EndpointUrl = mockUrl,
                Secret = mockSecret, EventTypes = [EventTypes.PayrollPeriodClosed, EventTypes.LeaveApproved], CreatedAt = now,
            });

        await db.SaveChangesAsync(ct);

        Employee Make(string code, string name, decimal salary, WorkSchedule schedule) => new()
        {
            EmployeeCode = code,
            FullName = name,
            Email = $"{name.Split(' ')[0].ToLowerInvariant()}@timeledger.local",
            MonthlyBaseSalary = salary,
            Schedule = schedule,
            CreatedAt = now,
        };
    }

    private static DateOnly NextMonday(DateOnly from)
    {
        var d = from;
        while (d.DayOfWeek != DayOfWeek.Monday)
        {
            d = d.AddDays(1);
        }

        return d;
    }
}
