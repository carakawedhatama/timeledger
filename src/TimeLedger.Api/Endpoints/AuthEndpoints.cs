using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TimeLedger.Api.Auth;
using TimeLedger.Api.Contracts;
using TimeLedger.Api.Data;
using TimeLedger.Api.Infrastructure;
using TimeLedger.Domain.Entities;

namespace TimeLedger.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Auth");

        auth.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .Produces<LoginResponse>()
            .ProducesProblem(401)
            .ProducesProblem(429)
            .WithSummary("Exchange email and password for a bearer token.");

        auth.MapGet("/me", async (CurrentUser me, TimeLedgerDbContext db, CancellationToken ct) =>
                await ToMeAsync(db, await db.UserAccounts.AsNoTracking().SingleAsync(u => u.Id == me.Id, ct), ct))
            .RequireAuthorization(Policies.SignedIn)
            .WithSummary("The signed-in account.");

        auth.MapPost("/logout", async (CurrentUser me, TimeLedgerDbContext db, CancellationToken ct) =>
            {
                await db.AuthSessions.Where(s => s.Id == me.SessionId).ExecuteDeleteAsync(ct);
                return Results.NoContent();
            })
            .RequireAuthorization(Policies.SignedIn)
            .WithSummary("End this session. Other devices stay signed in.");

        auth.MapPost("/change-password", ChangePasswordAsync)
            .RequireAuthorization(Policies.SignedIn)
            .Produces(204)
            .WithSummary("Change your password. Signs out every session, including this one.");

        return app;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest req, HttpContext http, TimeLedgerDbContext db, Passwords passwords, TokenService tokens,
        LoginThrottle throttle, IOptions<AuthOptions> options, TimeProvider time, CancellationToken ct)
    {
        if (!Checks.Email(req.Email) || string.IsNullOrEmpty(req.Password) || req.Password.Length > Passwords.MaxLength)
        {
            return Results.Problem(statusCode: 400, title: "Email and password are required.");
        }

        var email = UserAccount.NormalizeEmail(req.Email!);
        if (await throttle.RegisterAttemptAsync(email) is { } wait)
        {
            return TooManyAttempts(http, wait);
        }

        var user = await db.UserAccounts.AsNoTracking().FirstOrDefaultAsync(u => u.Email == email, ct);

        // Verify before looking at IsActive, and answer identically for unknown, wrong and disabled.
        if (!passwords.Verify(user, req.Password) || !user!.IsActive ||
            (user.EmployeeId is { } employeeId && !await db.Employees.AnyAsync(e => e.Id == employeeId && e.IsActive, ct)))
        {
            return Results.Problem(statusCode: 401, title: "Incorrect email or password.");
        }

        await throttle.ResetAsync(email);

        var now = time.GetUtcNow();
        await db.AuthSessions.Where(s => s.UserId == user.Id && s.ExpiresAt <= now).ExecuteDeleteAsync(ct);

        // The stamp is the one read alongside the hash, so a login racing a password reset produces a dead session.
        var session = new AuthSession
        {
            UserId = user.Id,
            SecurityStamp = user.SecurityStamp,
            ExpiresAt = now.AddMinutes(options.Value.SessionMinutes),
        };
        db.AuthSessions.Add(session);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new LoginResponse(tokens.Create(user, session, now), "Bearer", session.ExpiresAt, await ToMeAsync(db, user, ct)));
    }

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest req, HttpContext http, CurrentUser me, TimeLedgerDbContext db, Passwords passwords,
        LoginThrottle throttle, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(req.CurrentPassword) || req.CurrentPassword.Length > Passwords.MaxLength)
        {
            return Checks.Invalid("currentPassword", "Required.");
        }

        if (Passwords.Validate(req.NewPassword, me.Email) is { } error)
        {
            return Checks.Invalid("newPassword", error);
        }

        if (req.NewPassword == req.CurrentPassword)
        {
            return Checks.Invalid("newPassword", "Must differ from the current password.");
        }

        // A stolen token must not allow unlimited guesses at the password.
        if (await throttle.RegisterAttemptAsync(me.Email) is { } wait)
        {
            return TooManyAttempts(http, wait);
        }

        var user = await db.UserAccounts.SingleAsync(u => u.Id == me.Id, ct);

        // 400 rather than 401: the token is fine, and clients treat 401 as "signed out".
        if (!passwords.Verify(user, req.CurrentPassword))
        {
            return Checks.Invalid("currentPassword", "Incorrect password.");
        }

        user.PasswordHash = passwords.Hash(user, req.NewPassword!);
        user.RevokeSessions();
        await db.SaveChangesAsync(ct);
        await throttle.ResetAsync(me.Email);
        return Results.NoContent();
    }

    private static IResult TooManyAttempts(HttpContext http, long seconds)
    {
        http.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.Problem(statusCode: 429, title: "Too many attempts. Try again later.");
    }

    private static async Task<MeDto> ToMeAsync(TimeLedgerDbContext db, UserAccount user, CancellationToken ct)
    {
        var name = await db.Employees.Where(e => e.Id == user.EmployeeId).Select(e => e.FullName).FirstOrDefaultAsync(ct);
        return new MeDto(user.Id, user.Email, user.Role, user.EmployeeId, name);
    }
}
