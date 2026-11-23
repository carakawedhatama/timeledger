using Microsoft.EntityFrameworkCore;
using TimeLedger.Api.Auth;
using TimeLedger.Api.Contracts;
using TimeLedger.Api.Data;
using TimeLedger.Api.Infrastructure;
using TimeLedger.Domain;
using TimeLedger.Domain.Entities;

namespace TimeLedger.Api.Endpoints;

public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var users = app.MapGroup("/api/users").WithTags("Users").RequireAuthorization(Policies.PayrollAdmin);

        users.MapGet("/", async (TimeLedgerDbContext db, CancellationToken ct) =>
                (await db.UserAccounts.AsNoTracking().OrderBy(u => u.Email).ToListAsync(ct)).Select(UserDto.From))
            .WithSummary("List accounts.");

        users.MapPost("/", CreateAsync)
            .Produces<UserDto>(201)
            .WithSummary("Create an account. Employee and Supervisor accounts must be linked to an employee.");

        users.MapPatch("/{id:guid}", UpdateAsync)
            .Produces<UserDto>()
            .WithSummary("Change role or status, or reset a password. Not for your own account.");

        return app;
    }

    private static async Task<IResult> CreateAsync(
        CreateUserRequest req, TimeLedgerDbContext db, Passwords passwords, TimeProvider time, CancellationToken ct)
    {
        if (!Checks.Email(req.Email))
        {
            return Checks.Invalid("email", "A valid email address is required.");
        }

        var email = UserAccount.NormalizeEmail(req.Email!);
        if (!Enum.IsDefined(req.Role))
        {
            return Checks.Invalid("role", $"Choose one of: {string.Join(", ", Enum.GetNames<UserRole>())}.");
        }

        if (Passwords.Validate(req.Password, email) is { } error)
        {
            return Checks.Invalid("password", error);
        }

        if (req.EmployeeId is null && req.Role != UserRole.PayrollAdmin)
        {
            return Checks.Invalid("employeeId", $"A {req.Role} account must be linked to an employee.");
        }

        if (req.EmployeeId is { } employeeId && !await db.Employees.AnyAsync(e => e.Id == employeeId && e.IsActive, ct))
        {
            return Checks.Invalid("employeeId", "Active employee not found.");
        }

        if (await db.UserAccounts.AnyAsync(u => u.Email == email || (req.EmployeeId != null && u.EmployeeId == req.EmployeeId), ct))
        {
            return Results.Problem(statusCode: 409, title: "An account already exists for this email or employee.");
        }

        var user = new UserAccount { Email = email, Role = req.Role, EmployeeId = req.EmployeeId, CreatedAt = time.GetUtcNow() };
        user.PasswordHash = passwords.Hash(user, req.Password!);
        db.UserAccounts.Add(user);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/users/{user.Id}", UserDto.From(user));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id, UpdateUserRequest req, CurrentUser me, TimeLedgerDbContext db, Passwords passwords,
        LoginThrottle throttle, CancellationToken ct)
    {
        // Also what stops the last admin removing themselves, or a stolen token resetting its owner's password.
        if (id == me.Id)
        {
            return Checks.Forbidden("You cannot administer your own account. Use change-password, or ask another payroll admin.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockLedgerAsync(ct);

        // Re-checked under the lock: two admins demoting each other at once must not leave zero admins.
        if (!await db.UserAccounts.AnyAsync(u => u.Id == me.Id && u.IsActive && u.Role == UserRole.PayrollAdmin, ct))
        {
            return Checks.Forbidden("Your account is no longer a payroll admin.");
        }

        var user = await db.UserAccounts.FindAsync([id], ct);
        if (user is null)
        {
            return Results.NotFound();
        }

        if (req.Role is { } role)
        {
            if (!Enum.IsDefined(role))
            {
                return Checks.Invalid("role", $"Choose one of: {string.Join(", ", Enum.GetNames<UserRole>())}.");
            }

            if (role != UserRole.PayrollAdmin && user.EmployeeId is null)
            {
                return Checks.Invalid("role", $"A {role} account must be linked to an employee.");
            }
        }

        if (req.NewPassword is not null)
        {
            if (Passwords.Validate(req.NewPassword, user.Email) is { } error)
            {
                return Checks.Invalid("newPassword", error);
            }

            user.PasswordHash = passwords.Hash(user, req.NewPassword);
        }

        user.Role = req.Role ?? user.Role;
        user.IsActive = req.IsActive ?? user.IsActive;
        if (db.Entry(user).State == EntityState.Modified)
        {
            user.RevokeSessions();
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        if (req.NewPassword is not null)
        {
            await throttle.ResetAsync(user.Email);
        }

        return Results.Ok(UserDto.From(user));
    }
}
