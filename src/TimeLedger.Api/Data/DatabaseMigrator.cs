using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace TimeLedger.Api.Data;

public static class DatabaseMigrator
{
    private const long StartupLock = 7_301_002;

    /// <summary>
    /// Runs <paramref name="seed"/> after migrating, all under one lock, so replicas starting together don't collide.
    /// Databases created by the pre-migration releases (EnsureCreated) are adopted first.
    /// </summary>
    public static async Task InitializeAsync(TimeLedgerDbContext db, ILogger log, Func<Task> seed, CancellationToken ct = default)
    {
        var creator = db.GetService<IRelationalDatabaseCreator>();
        if (!await creator.ExistsAsync(ct))
        {
            await creator.CreateAsync(ct);
        }

        // Session-level lock, so keep this one connection until done.
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_lock({StartupLock})", ct);
            try
            {
                await AdoptEnsureCreatedSchemaAsync(db, log, ct);
                await db.Database.MigrateAsync(ct);
                await seed();
            }
            finally
            {
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock({StartupLock})", CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task AdoptEnsureCreatedSchemaAsync(TimeLedgerDbContext db, ILogger log, CancellationToken ct)
    {
        var legacy = await db.Database.SqlQuery<bool>($"""
            SELECT to_regclass('employees') IS NOT NULL
               AND to_regclass('"__EFMigrationsHistory"') IS NULL AS "Value"
            """).SingleAsync(ct);
        if (!legacy)
        {
            return;
        }

        var initial = db.Database.GetMigrations().First();
        var efVersion = ProductInfo.GetVersion();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"""
            CREATE TABLE "__EFMigrationsHistory" (
                "MigrationId" character varying(150) NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                "ProductVersion" character varying(32) NOT NULL)
            """, ct);
        await db.Database.ExecuteSqlAsync($"""INSERT INTO "__EFMigrationsHistory" VALUES ({initial}, {efVersion})""", ct);
        await tx.CommitAsync(ct);

        log.LogWarning("Adopted an EnsureCreated database as {Migration}.", initial);
    }
}
