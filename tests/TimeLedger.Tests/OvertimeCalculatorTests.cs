using TimeLedger.Domain;
using TimeLedger.Domain.Rules;

namespace TimeLedger.Tests;

public class OvertimeCalculatorTests
{
    // 5,190,000 / 173 = 30,000 per hour: keeps the expected values easy to check by hand.
    private const decimal Salary = 5_190_000m;

    [Fact]
    public void Hourly_rate_is_one_173rd_of_monthly_salary()
    {
        Assert.Equal(30_000m, OvertimeCalculator.HourlyRate(Salary));
    }

    [Theory]
    [InlineData(480, 480, 0, 0)]             // exactly 8h: no overtime
    [InlineData(300, 300, 0, 0)]             // short day
    [InlineData(540, 480, 60, 45_000)]       // 1h OT at 1.5x
    [InlineData(600, 480, 120, 105_000)]     // 1.5x + 2x
    [InlineData(510, 480, 30, 22_500)]       // half hour, pro rata at 1.5x
    [InlineData(720, 480, 240, 225_000)]     // 4h OT: 45k + 3 × 60k
    public void Workday_on_five_day_week(int worked, int expectedRegular, int expectedOvertime, int expectedPay)
    {
        var r = OvertimeCalculator.Calculate(worked, DayKind.Workday, WorkSchedule.FiveDay, Salary);

        Assert.Equal(expectedRegular, r.RegularMinutes);
        Assert.Equal(expectedOvertime, r.OvertimeMinutes);
        Assert.Equal((decimal)expectedPay, r.OvertimePay);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Six_day_week_has_seven_hour_normal_day()
    {
        var r = OvertimeCalculator.Calculate(8 * 60, DayKind.Workday, WorkSchedule.SixDay, Salary);

        Assert.Equal(420, r.RegularMinutes);
        Assert.Equal(60, r.OvertimeMinutes);
        Assert.Equal(45_000m, r.OvertimePay);
    }

    [Theory]
    [InlineData(480, 480_000)]    // 8h × 2x
    [InlineData(540, 570_000)]    // + 9th hour × 3x
    [InlineData(600, 690_000)]    // + 10th hour × 4x
    [InlineData(720, 930_000)]    // + 11th and 12th × 4x
    public void Rest_day_on_five_day_week_uses_2x_3x_4x_bands(int worked, int expectedPay)
    {
        var r = OvertimeCalculator.Calculate(worked, DayKind.RestDay, WorkSchedule.FiveDay, Salary);

        Assert.Equal(0, r.RegularMinutes);
        Assert.Equal(worked, r.OvertimeMinutes);
        Assert.Equal((decimal)expectedPay, r.OvertimePay);
    }

    [Fact]
    public void Public_holiday_on_six_day_week_uses_seven_hour_first_band()
    {
        // 7h × 2 + 1h × 3 + 1h × 4 = 21 units × 30,000
        var r = OvertimeCalculator.Calculate(9 * 60, DayKind.PublicHoliday, WorkSchedule.SixDay, Salary);

        Assert.Equal(630_000m, r.OvertimePay);
    }

    [Fact]
    public void Overtime_beyond_daily_limit_is_paid_and_flagged()
    {
        var r = OvertimeCalculator.Calculate(13 * 60, DayKind.Workday, WorkSchedule.FiveDay, Salary);

        Assert.Equal(300, r.OvertimeMinutes);
        Assert.Equal(285_000m, r.OvertimePay); // 45k + 4 × 60k: still paid in full
        Assert.Contains("4h daily limit", Assert.Single(r.Warnings));
    }

    [Fact]
    public void Pay_is_rounded_to_whole_rupiah()
    {
        // 7,000,000 / 173 = 40,462.43...; 1h at 1.5x = 60,693.64 → 60,694
        var r = OvertimeCalculator.Calculate(540, DayKind.Workday, WorkSchedule.FiveDay, 7_000_000m);

        Assert.Equal(60_694m, r.OvertimePay);
    }

    [Fact]
    public void Negative_minutes_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OvertimeCalculator.Calculate(-1, DayKind.Workday, WorkSchedule.FiveDay, Salary));
    }
}
