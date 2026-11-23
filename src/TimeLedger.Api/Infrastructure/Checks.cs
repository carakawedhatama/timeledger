namespace TimeLedger.Api.Infrastructure;

/// <summary>Input limits shared by the endpoints.</summary>
public static class Checks
{
    public const int MinYear = 1900;
    public const int MaxYear = 2199;

    public static bool Year(int year) => year is >= MinYear and <= MaxYear;

    public static bool Date(DateOnly date) => Year(date.Year);

    public static bool Email(string? email) =>
        !string.IsNullOrWhiteSpace(email) && email.Contains('@') && email.Length <= 320;

    public static IResult BadDate() =>
        Results.Problem(statusCode: 400, title: $"Dates must fall between the years {MinYear} and {MaxYear}.");

    public static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    public static IResult Forbidden(string title) => Results.Problem(statusCode: 403, title: title);
}
