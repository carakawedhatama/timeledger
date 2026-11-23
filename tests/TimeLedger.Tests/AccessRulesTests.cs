using TimeLedger.Domain;
using TimeLedger.Domain.Rules;

namespace TimeLedger.Tests;

public class AccessRulesTests
{
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();
    private static readonly Guid SomeoneElse = Guid.NewGuid();

    public enum Target
    {
        Self,
        Report,
        Stranger,
        Unsupervised,
    }

    [Theory]
    // role, target → view, pay, act, set time, decide leave
    [InlineData(UserRole.Employee, Target.Self, true, true, true, false, false)]
    [InlineData(UserRole.Employee, Target.Report, false, false, false, false, false)]
    [InlineData(UserRole.Employee, Target.Stranger, false, false, false, false, false)]
    [InlineData(UserRole.Employee, Target.Unsupervised, false, false, false, false, false)]
    [InlineData(UserRole.Supervisor, Target.Self, true, true, true, false, false)]
    [InlineData(UserRole.Supervisor, Target.Report, true, false, false, false, true)]
    [InlineData(UserRole.Supervisor, Target.Stranger, false, false, false, false, false)]
    [InlineData(UserRole.Supervisor, Target.Unsupervised, false, false, false, false, false)]
    [InlineData(UserRole.PayrollAdmin, Target.Self, true, true, true, false, false)]
    [InlineData(UserRole.PayrollAdmin, Target.Report, true, true, true, true, true)]
    [InlineData(UserRole.PayrollAdmin, Target.Stranger, true, true, true, true, true)]
    [InlineData(UserRole.PayrollAdmin, Target.Unsupervised, true, true, true, true, true)]
    public void Permission_matrix(UserRole role, Target target, bool view, bool pay, bool act, bool setTime, bool decide)
    {
        var actor = new Actor(role, Me);
        var (employee, supervisor) = target switch
        {
            Target.Self => (Me, (Guid?)SomeoneElse),
            Target.Report => (Other, Me),
            Target.Stranger => (Other, SomeoneElse),
            _ => (Other, null),
        };

        Assert.Equal(view, AccessRules.CanView(actor, employee, supervisor));
        Assert.Equal(pay, AccessRules.CanViewPay(actor, employee));
        Assert.Equal(act, AccessRules.CanActFor(actor, employee));
        Assert.Equal(setTime, AccessRules.CanSetPunchTime(actor, employee));
        Assert.Equal(decide, AccessRules.CanDecideLeave(actor, employee, supervisor));
    }

    [Fact]
    public void Unlinked_supervisor_does_not_match_employees_without_a_supervisor()
    {
        var actor = new Actor(UserRole.Supervisor, null);

        Assert.False(AccessRules.CanView(actor, Other, null));
        Assert.False(AccessRules.CanDecideLeave(actor, Other, null));
    }

    [Fact]
    public void Self_supervision_does_not_allow_self_approval()
    {
        Assert.False(AccessRules.CanDecideLeave(new Actor(UserRole.Supervisor, Me), Me, Me));
    }

    [Fact]
    public void Unlinked_admin_can_do_everything_for_everyone()
    {
        var actor = new Actor(UserRole.PayrollAdmin, null);

        Assert.True(AccessRules.CanView(actor, Other, null));
        Assert.True(AccessRules.CanSetPunchTime(actor, Other));
        Assert.True(AccessRules.CanDecideLeave(actor, Other, null));
    }

    [Fact]
    public void Default_actor_has_no_access()
    {
        Assert.False(AccessRules.CanView(default, Other, null));
        Assert.False(AccessRules.CanViewPay(default, Other));
        Assert.False(AccessRules.CanActFor(default, Other));
        Assert.False(AccessRules.CanDecideLeave(default, Other, null));
    }
}
