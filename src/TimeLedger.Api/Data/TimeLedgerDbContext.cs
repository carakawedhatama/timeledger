using System.Text;
using Microsoft.EntityFrameworkCore;
using TimeLedger.Domain.Entities;

namespace TimeLedger.Api.Data;

public sealed class TimeLedgerDbContext(DbContextOptions<TimeLedgerDbContext> options) : DbContext(options)
{
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<ClockEvent> ClockEvents => Set<ClockEvent>();
    public DbSet<LeaveRequest> LeaveRequests => Set<LeaveRequest>();
    public DbSet<LeaveBalance> LeaveBalances => Set<LeaveBalance>();
    public DbSet<PublicHoliday> PublicHolidays => Set<PublicHoliday>();
    public DbSet<PayPeriod> PayPeriods => Set<PayPeriod>();
    public DbSet<TimesheetEntry> TimesheetEntries => Set<TimesheetEntry>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<IntegrationConnection> IntegrationConnections => Set<IntegrationConnection>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();

    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Employee>(e =>
        {
            e.ToTable("employees");
            e.HasKey(x => x.Id);
            e.Property(x => x.EmployeeCode).HasMaxLength(32);
            e.Property(x => x.FullName).HasMaxLength(200);
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.MonthlyBaseSalary).HasPrecision(18, 2);
            e.Property(x => x.Schedule).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.TimeZoneId).HasMaxLength(64);
            e.HasIndex(x => x.EmployeeCode).IsUnique();
            e.HasIndex(x => x.Email).IsUnique();
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.SupervisorId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<UserAccount>(e =>
        {
            e.ToTable("user_accounts", t => t.HasCheckConstraint(
                "ck_user_accounts_employee_link", "role = 'PayrollAdmin' OR employee_id IS NOT NULL"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.PasswordHash).HasMaxLength(256);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
            MapXmin(e.Property(x => x.Version));
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.Email).IsUnique();
            e.HasIndex(x => x.EmployeeId).IsUnique();
        });

        b.Entity<AuthSession>(e =>
        {
            e.ToTable("auth_sessions");
            e.HasKey(x => x.Id);
            e.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ClockEvent>(e =>
        {
            e.ToTable("clock_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.IdempotencyKey).HasMaxLength(100);
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.EmployeeId, x.OccurredAt });
            e.HasIndex(x => x.OccurredAt);
            e.HasIndex(x => new { x.EmployeeId, x.IdempotencyKey }).IsUnique().HasFilter("idempotency_key IS NOT NULL");
        });

        b.Entity<LeaveRequest>(e =>
        {
            e.ToTable("leave_requests");
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Days).HasPrecision(5, 1);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.DecidedBy).HasMaxLength(200);
            e.Property(x => x.DecisionNote).HasMaxLength(500);
            MapXmin(e.Property(x => x.Version));
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.EmployeeId, x.StartDate });
            e.HasIndex(x => x.Status);
        });

        b.Entity<LeaveBalance>(e =>
        {
            e.ToTable("leave_balances");
            e.HasKey(x => new { x.EmployeeId, x.Year });
            e.Property(x => x.EntitledDays).HasPrecision(5, 1);
            e.Property(x => x.UsedDays).HasPrecision(5, 1);
            e.Ignore(x => x.RemainingDays);
            MapXmin(e.Property(x => x.Version));
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PublicHoliday>(e =>
        {
            e.ToTable("public_holidays");
            e.HasKey(x => x.Date);
            e.Property(x => x.Name).HasMaxLength(200);
        });

        b.Entity<PayPeriod>(e =>
        {
            e.ToTable("pay_periods");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            MapXmin(e.Property(x => x.Version));
            e.HasIndex(x => new { x.StartDate, x.EndDate });
        });

        b.Entity<TimesheetEntry>(e =>
        {
            e.ToTable("timesheet_entries");
            e.HasKey(x => x.Id);
            e.Property(x => x.DayKind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.LeaveType).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.OvertimePay).HasPrecision(18, 2);
            e.HasOne<PayPeriod>().WithMany().HasForeignKey(x => x.PayPeriodId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.PayPeriodId, x.EmployeeId, x.WorkDate }).IsUnique();
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasMaxLength(100);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.HasIndex(x => x.CreatedAt).HasFilter("processed_at IS NULL");
        });

        b.Entity<IntegrationConnection>(e =>
        {
            e.ToTable("integration_connections");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Provider).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.EndpointUrl).HasMaxLength(2000);
            e.Property(x => x.Secret).HasMaxLength(200);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<WebhookDelivery>(e =>
        {
            e.ToTable("webhook_deliveries");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.LastError).HasMaxLength(WebhookDelivery.MaxErrorLength);
            e.HasOne<OutboxMessage>().WithMany().HasForeignKey(x => x.OutboxMessageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<IntegrationConnection>().WithMany().HasForeignKey(x => x.ConnectionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.OutboxMessageId, x.ConnectionId }).IsUnique();
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
        });

        UseSnakeCaseColumns(b);
    }

    /// <summary>
    /// Npgsql maps a <c>uint</c> row version to PostgreSQL's xmin system column: optimistic concurrency
    /// with no extra column to maintain.
    /// </summary>
    private static void MapXmin(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<uint> property) =>
        property.IsRowVersion();

    private static void UseSnakeCaseColumns(ModelBuilder b)
    {
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                // Leave the xmin row-version mapping to Npgsql's convention.
                if (!(property.IsConcurrencyToken && property.ClrType == typeof(uint)))
                {
                    property.SetColumnName(ToSnakeCase(property.Name));
                }
            }
        }
    }

    internal static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
            {
                sb.Append('_');
            }

            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }
}
