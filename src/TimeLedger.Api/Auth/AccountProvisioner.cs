using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TimeLedger.Api.Data;
using TimeLedger.Domain;
using TimeLedger.Domain.Entities;

namespace TimeLedger.Api.Auth;

/// <summary>Accounts that must exist before anyone can sign in: demo users in Development, or the first admin.</summary>
public sealed class AccountProvisioner(
    TimeLedgerDbContext db, Passwords passwords, TimeProvider time, IOptions<AuthOptions> options, ILogger<AccountProvisioner> log)
{
    public const string DemoPassword = "TimeLedger-Demo-2026";
    public const string DemoAdminEmail = "admin@timeledger.local";

    // Employee code → the code of their supervisor.
    private static readonly Dictionary<string, string?> DemoTeams = new()
    {
        ["EMP-001"] = null,
        ["EMP-002"] = "EMP-001",
        ["EMP-003"] = "EMP-001",
        ["EMP-004"] = "EMP-001",
        ["EMP-005"] = null,
        ["EMP-006"] = "EMP-005",
        ["EMP-007"] = "EMP-005",
        ["EMP-008"] = "EMP-005",
    };

    public async Task SeedDemoAccountsAsync(CancellationToken ct = default)
    {
        if (await db.UserAccounts.AnyAsync(ct))
        {
            return;
        }

        // Only the seeded people: someone else's employee who happens to be EMP-001 must not get a public password.
        var codes = DemoTeams.Keys.ToList();
        var staff = await db.Employees
            .Where(e => codes.Contains(e.EmployeeCode) && e.Email.EndsWith("@timeledger.local"))
            .ToDictionaryAsync(e => e.EmployeeCode, ct);
        if (staff.Count == 0)
        {
            return;
        }

        Add(DemoAdminEmail, DemoPassword, UserRole.PayrollAdmin);
        foreach (var (code, employee) in staff)
        {
            if (DemoTeams[code] is { } supervisorCode && staff.TryGetValue(supervisorCode, out var supervisor))
            {
                employee.SupervisorId = supervisor.Id;
            }

            var role = DemoTeams.ContainsValue(code) ? UserRole.Supervisor : UserRole.Employee;
            Add(employee.Email, DemoPassword, role, employee.Id);
        }

        await db.SaveChangesAsync(ct);
        log.LogWarning("Created demo accounts with a published password. Never expose this environment.");
    }

    public async Task EnsureAdminAsync(CancellationToken ct = default)
    {
        if (await db.UserAccounts.AnyAsync(u => u.Role == UserRole.PayrollAdmin && u.IsActive, ct))
        {
            return;
        }

        var bootstrap = options.Value.BootstrapAdmin;
        if (string.IsNullOrWhiteSpace(bootstrap.Email) || string.IsNullOrWhiteSpace(bootstrap.Password))
        {
            log.LogWarning("There is no active payroll admin. Set Auth:BootstrapAdmin:Email and :Password to create one.");
            return;
        }

        var email = UserAccount.NormalizeEmail(bootstrap.Email);
        if (Passwords.Validate(bootstrap.Password, email) is { } error)
        {
            throw new InvalidOperationException($"Auth:BootstrapAdmin:Password: {error}");
        }

        if (await db.UserAccounts.AnyAsync(u => u.Email == email, ct))
        {
            throw new InvalidOperationException("Auth:BootstrapAdmin:Email belongs to an existing account; use a different address.");
        }

        Add(email, bootstrap.Password, UserRole.PayrollAdmin);
        await db.SaveChangesAsync(ct);
        log.LogInformation("Created the bootstrap payroll admin.");
    }

    private void Add(string email, string password, UserRole role, Guid? employeeId = null)
    {
        var user = new UserAccount { Email = email, Role = role, EmployeeId = employeeId, CreatedAt = time.GetUtcNow() };
        user.PasswordHash = passwords.Hash(user, password);
        db.UserAccounts.Add(user);
    }
}
