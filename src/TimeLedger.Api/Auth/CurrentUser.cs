using TimeLedger.Api.Data;
using TimeLedger.Domain;
using TimeLedger.Domain.Entities;
using TimeLedger.Domain.Rules;

namespace TimeLedger.Api.Auth;

/// <summary>The caller, as loaded from the database for this request. Until signed in it has no role and sees nothing.</summary>
public sealed class CurrentUser
{
    public Guid Id { get; private set; }
    public Guid SessionId { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public Actor Actor { get; private set; }

    public void SignIn(UserAccount user, Guid sessionId)
    {
        Id = user.Id;
        SessionId = sessionId;
        Email = user.Email;
        Actor = new Actor(user.Role, user.EmployeeId);
    }

    /// <summary><see cref="AccessRules.CanView"/> as a query. The two are tested against each other.</summary>
    public IQueryable<Employee> VisibleEmployees(TimeLedgerDbContext db)
    {
        if (Actor.Role == UserRole.PayrollAdmin)
        {
            return db.Employees;
        }

        // Comparing a nullable column to a null parameter becomes IS NULL in SQL, so bail out before that can happen.
        if (Actor.EmployeeId is not { } self)
        {
            return db.Employees.Where(_ => false);
        }

        return Actor.Role == UserRole.Supervisor
            ? db.Employees.Where(e => e.Id == self || e.SupervisorId == self)
            : db.Employees.Where(e => e.Id == self);
    }
}
