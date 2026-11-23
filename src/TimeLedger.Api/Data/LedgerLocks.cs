using Microsoft.EntityFrameworkCore;

namespace TimeLedger.Api.Data;

/// <summary>
/// Transaction-scoped PostgreSQL advisory locks for rules that span rows ("no overlap", "alternate in and out",
/// "nothing lands in a period while it closes"), which xmin cannot protect. Always ledger first, then employee.
/// </summary>
public static class LedgerLocks
{
    private const long Ledger = 7_301_001;

    /// <summary>For a write about one employee. Such writes run in parallel with each other, never with a close.</summary>
    public static async Task LockEmployeeAsync(this TimeLedgerDbContext db, Guid employeeId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock_shared({Ledger})", ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({employeeId.ToString()}, 0))", ct);
    }

    /// <summary>For a write that needs a still view of every employee: closing or opening a period, changing teams.</summary>
    public static Task LockLedgerAsync(this TimeLedgerDbContext db, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({Ledger})", ct);
}
