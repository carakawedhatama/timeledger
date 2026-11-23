namespace TimeLedger.Domain.Rules;

public sealed record OvertimeResult(int RegularMinutes, int OvertimeMinutes, decimal OvertimePay, IReadOnlyList<string> Warnings);

/// <summary>
/// Overtime per Indonesian Government Regulation (PP) 35/2021.
/// <list type="bullet">
/// <item>Hourly rate = 1/173 × monthly wage.</item>
/// <item>Workday: 1st overtime hour × 1.5, each later hour × 2.</item>
/// <item>Rest day / public holiday, 5-day week: hours 1–8 × 2, 9th × 3, 10th–12th × 4.</item>
/// <item>Rest day / public holiday, 6-day week: hours 1–7 × 2, 8th × 3, 9th–11th × 4.</item>
/// <item>Overtime is capped at 4 hours/day and 18 hours/week; breaches are flagged, not blocked,
/// because the hours were worked and must still be paid.</item>
/// </list>
/// Partial hours are paid pro rata per minute; the final amount is rounded to whole rupiah.
/// </summary>
public static class OvertimeCalculator
{
    public const decimal HourlyDivisor = 173m;
    public const int MaxDailyOvertimeMinutes = 4 * 60;
    public const int MaxWeeklyOvertimeMinutes = 18 * 60;

    public static decimal HourlyRate(decimal monthlySalary) => monthlySalary / HourlyDivisor;

    public static OvertimeResult Calculate(int workedMinutes, DayKind kind, WorkSchedule schedule, decimal monthlySalary)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(workedMinutes);
        ArgumentOutOfRangeException.ThrowIfNegative(monthlySalary);

        var normal = SchedulePolicy.NormalDailyMinutes(schedule);
        var warnings = new List<string>();
        int regular;
        int overtime;
        (int Width, decimal Multiplier)[] bands;

        if (kind == DayKind.Workday)
        {
            regular = Math.Min(workedMinutes, normal);
            overtime = workedMinutes - regular;
            bands = [(60, 1.5m), (int.MaxValue, 2m)];

            if (overtime > MaxDailyOvertimeMinutes)
            {
                warnings.Add($"Overtime of {FormatHours(overtime)} exceeds the 4h daily limit.");
            }
        }
        else
        {
            // Every minute on a rest day or public holiday is overtime.
            regular = 0;
            overtime = workedMinutes;
            bands = [(normal, 2m), (60, 3m), (int.MaxValue, 4m)];

            if (overtime > normal + MaxDailyOvertimeMinutes)
            {
                warnings.Add($"{FormatHours(overtime)} worked on a {Describe(kind)} exceeds the statutory maximum.");
            }
        }

        var pay = Math.Round(Allocate(overtime, bands) * HourlyRate(monthlySalary), 0, MidpointRounding.AwayFromZero);
        return new OvertimeResult(regular, overtime, pay, warnings);
    }

    /// <summary>Sum of (hours in band × multiplier), i.e. pay expressed in "hourly-rate units".</summary>
    private static decimal Allocate(int overtimeMinutes, (int Width, decimal Multiplier)[] bands)
    {
        var remaining = overtimeMinutes;
        var units = 0m;

        foreach (var (width, multiplier) in bands)
        {
            if (remaining <= 0)
            {
                break;
            }

            var take = Math.Min(remaining, width);
            units += take / 60m * multiplier;
            remaining -= take;
        }

        return units;
    }

    private static string Describe(DayKind kind) => kind == DayKind.PublicHoliday ? "public holiday" : "rest day";

    private static string FormatHours(int minutes) => $"{minutes / 60}h{minutes % 60:00}m";
}
