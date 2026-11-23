namespace TimeLedger.Domain.Entities;

public class Employee
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string EmployeeCode { get; set; }
    public required string FullName { get; set; }
    public required string Email { get; set; }

    /// <summary>Monthly base salary in IDR. The overtime hourly rate is 1/173 of this.</summary>
    public decimal MonthlyBaseSalary { get; set; }

    public WorkSchedule Schedule { get; set; } = WorkSchedule.FiveDay;

    /// <summary>IANA time zone used to decide which calendar day a shift belongs to.</summary>
    public string TimeZoneId { get; set; } = "Asia/Jakarta";

    /// <summary>The employee who reviews this one's attendance and leave.</summary>
    public Guid? SupervisorId { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}
