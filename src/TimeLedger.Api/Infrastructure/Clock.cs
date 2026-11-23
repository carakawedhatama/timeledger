namespace TimeLedger.Api.Infrastructure;

public static class Clock
{
    /// <summary>The company's home time zone, used for "today" in pay-period and absence logic.</summary>
    public const string HomeTimeZoneId = "Asia/Jakarta";

    public static TimeZoneInfo HomeTimeZone { get; } = TimeZoneInfo.FindSystemTimeZoneById(HomeTimeZoneId);

    public static DateOnly LocalToday(this TimeProvider time) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), HomeTimeZone).DateTime);

    public static DateTimeOffset ToUtc(DateOnly date, TimeOnly time, TimeZoneInfo zone) =>
        new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time), zone), TimeSpan.Zero);
}
