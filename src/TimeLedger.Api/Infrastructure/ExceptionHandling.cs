using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TimeLedger.Domain;

namespace TimeLedger.Api.Infrastructure;

/// <summary>Maps known exceptions to RFC 7807 problem responses.</summary>
public sealed class ProblemExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        var (status, title) = exception switch
        {
            DomainException => (StatusCodes.Status422UnprocessableEntity, exception.Message),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "The record was changed by another request. Reload and try again."),
            BadHttpRequestException bad => (bad.StatusCode, "The request is malformed."),
            PostgresException pg => FromPostgres(pg),
            DbUpdateException { InnerException: PostgresException pg } => FromPostgres(pg),
            _ => (0, string.Empty),
        };

        if (status == 0)
        {
            return false;
        }

        http.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = new ProblemDetails { Status = status, Title = title },
            Exception = exception,
        });
    }

    /// <summary>The database is the backstop for races and for text the validators don't look at.</summary>
    private static (int Status, string Title) FromPostgres(PostgresException pg) => pg.SqlState switch
    {
        PostgresErrorCodes.UniqueViolation => (StatusCodes.Status409Conflict, "This conflicts with an existing record."),
        PostgresErrorCodes.StringDataRightTruncation => (StatusCodes.Status400BadRequest, "A value is too long."),
        PostgresErrorCodes.CharacterNotInRepertoire => (StatusCodes.Status400BadRequest, "Text contains an invalid character."),
        _ => (0, string.Empty),
    };
}
