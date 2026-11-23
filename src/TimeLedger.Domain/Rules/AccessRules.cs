namespace TimeLedger.Domain.Rules;

/// <summary>The signed-in person, reduced to what authorization decisions need.</summary>
public readonly record struct Actor(UserRole Role, Guid? EmployeeId);

/// <summary>Which employee's records an actor may touch. Pure, so the whole matrix is unit-testable.</summary>
public static class AccessRules
{
    public static bool CanView(Actor actor, Guid employeeId, Guid? supervisorId) =>
        actor.Role == UserRole.PayrollAdmin || IsSelf(actor, employeeId) || Supervises(actor, supervisorId);

    /// <summary>Salary, and overtime pay (from which salary can be derived): the employee and payroll only.</summary>
    public static bool CanViewPay(Actor actor, Guid employeeId) =>
        actor.Role == UserRole.PayrollAdmin || IsSelf(actor, employeeId);

    /// <summary>Clocking and requesting leave.</summary>
    public static bool CanActFor(Actor actor, Guid employeeId) =>
        actor.Role == UserRole.PayrollAdmin || IsSelf(actor, employeeId);

    /// <summary>Choosing a punch's time is a correction, and nobody corrects their own attendance.</summary>
    public static bool CanSetPunchTime(Actor actor, Guid employeeId) =>
        actor.Role == UserRole.PayrollAdmin && !IsSelf(actor, employeeId);

    /// <summary>Nobody decides their own leave, whatever their role.</summary>
    public static bool CanDecideLeave(Actor actor, Guid employeeId, Guid? supervisorId) =>
        !IsSelf(actor, employeeId) && (actor.Role == UserRole.PayrollAdmin || Supervises(actor, supervisorId));

    private static bool IsSelf(Actor actor, Guid employeeId) => actor.EmployeeId == employeeId;

    // The "is not null" matters: an unlinked supervisor must not match every employee who has no supervisor.
    private static bool Supervises(Actor actor, Guid? supervisorId) =>
        actor.Role == UserRole.Supervisor && actor.EmployeeId is not null && actor.EmployeeId == supervisorId;
}
