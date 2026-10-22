using Insys.BuildingBlocks.Domain;
using Insys.Identity.Domain.Users;
using Insys.Testing;

namespace Insys.Identity.Tests;

public sealed class UserAccountTests
{
    private static readonly DateOnly Today = new(2026, 10, 21);
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.CreateVersion7();

    private static UserAccount User(bool mfa = true) => UserAccount.ProvisionUser(Guid.CreateVersion7(), "jane", "Jane", "OIDC", "sub-jane", mfa);

    private static RoleFacts Role(string code, bool mfa = false, params string[] permissions) => new(code, true, permissions.ToHashSet(), mfa);

    [Fact]
    public void UserAccount_Lifecycle_MatchesDomainModel()
    {
        var implemented = UserAccount.Lifecycle.Transitions.Select(t => $"{t.From}-{t.Trigger}->{t.To}").OrderBy(t => t, StringComparer.Ordinal).ToList();
        var specified = DomainModelSpec.Transitions("iam", "UserAccount").Select(t => t.Replace("First sign-in", "FirstSignIn", StringComparison.Ordinal)).ToList();

        implemented.ShouldBe(specified);
    }

    [Fact]
    public void ProvisionedUser_IsInvited_AndFirstSignInActivates()
    {
        var user = User();
        user.Status.ShouldBe(UserStatus.Invited);

        user.RecordSignIn(Now).IsSuccess.ShouldBeTrue();
        user.Status.ShouldBe(UserStatus.Active);
    }

    [Fact]
    public void AssignRole_PrivilegedRoleWithoutMfa_IsRefused_IAM003() =>
        User(mfa: false).AssignRole(Role("ADMIN", mfa: true), [], [], Today, null, null, null, Admin, Today, Now).Error!.Code.ShouldBe("MFA_REQUIRED");

    [Fact]
    public void AssignRole_CombiningBlockedPermissions_IsRefused_SRS002()
    {
        var user = User();
        var maker = Role("MAKER", permissions: "payment.create");
        user.AssignRole(maker, [], [], Today, null, null, null, Admin, Today, Now).IsSuccess.ShouldBeTrue();

        var result = user.AssignRole(Role("CHECKER", permissions: "payment.approve"), [maker],
            [new DutyConflictRule("payment.create", "payment.approve", "BLOCK")], Today, null, null, null, Admin, Today, Now);

        result.Error!.Code.ShouldBe("SEGREGATION_OF_DUTIES");
    }

    [Fact]
    public void AssignRole_WarnRule_DoesNotBlock_SRS002()
    {
        var user = User();
        var maker = Role("MAKER", permissions: "payment.create");
        user.AssignRole(maker, [], [], Today, null, null, null, Admin, Today, Now);

        user.AssignRole(Role("CHECKER", permissions: "payment.approve"), [maker], [new DutyConflictRule("payment.create", "payment.approve", "WARN")],
            Today, null, null, null, Admin, Today, Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void RevokeRole_EndsTheAssignmentToday_SoItIsNoLongerEffective_IAM005()
    {
        var user = User();
        user.AssignRole(Role("CLERK"), [], [], Today.AddDays(-10), null, null, null, Admin, Today, Now);

        user.RevokeRole("CLERK", Today, Now).IsSuccess.ShouldBeTrue();

        user.EffectiveAssignments(Today).ShouldBeEmpty();
        user.DomainEvents.OfType<RoleRevoked>().ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData(UserStatus.Invited, "Lock")]
    [InlineData(UserStatus.Invited, "Disable")]
    public void InvitedUser_CannotBeLockedOrDisabled_IsInvalidStateTransition(UserStatus _, string action)
    {
        var user = User();
        var result = action == "Lock" ? user.LockUser() : user.DisableUser(Now);

        result.Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
    }

    [Fact]
    public void DisableUser_FromLocked_IsAllowed_AndRaisesUserDisabled_IAM005()
    {
        var user = User();
        user.RecordSignIn(Now);
        user.LockUser().IsSuccess.ShouldBeTrue();

        user.DisableUser(Now).IsSuccess.ShouldBeTrue();
        user.Status.ShouldBe(UserStatus.Disabled);
        user.DomainEvents.OfType<UserDisabled>().ShouldHaveSingleItem();
        user.UnlockUser().Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
    }

    [Fact]
    public void PrivilegedAssignment_IsPending_UntilASecondPersonApproves_SRS002()
    {
        var user = User();
        var privileged = new RoleFacts("TENANT_ADMIN", true, new HashSet<string> { "user.create" }, RequiresMfa: true, Privileged: true);
        user.AssignRole(privileged, [], [], Today, null, null, null, Admin, Today, Now).IsSuccess.ShouldBeTrue();

        var assignment = user.RoleAssignments.Single();
        assignment.Status.ShouldBe(AssignmentStatus.PendingApproval);
        user.EffectiveAssignments(Today).ShouldBeEmpty();
        user.DomainEvents.OfType<RoleAssigned>().ShouldBeEmpty();
        user.DomainEvents.OfType<PrivilegedRoleRequested>().Single().AssignedBy.ShouldBe(Admin);
        user.AssignRole(privileged, [], [], Today, null, null, null, Admin, Today, Now).Error!.Code.ShouldBe("ROLE_ALREADY_ASSIGNED");

        user.DecideAssignment(assignment.Id, approved: true, Admin, Now).Error!.Code.ShouldBe("MAKER_CHECKER_SAME_PERSON");
        user.EffectiveAssignments(Today).ShouldBeEmpty();

        var checker = Guid.CreateVersion7();
        user.DecideAssignment(assignment.Id, approved: true, checker, Now).IsSuccess.ShouldBeTrue();
        assignment.Status.ShouldBe(AssignmentStatus.Active);
        assignment.ApprovedBy.ShouldBe(checker);
        user.EffectiveAssignments(Today).Single().RoleCode.ShouldBe("TENANT_ADMIN");
        user.DomainEvents.OfType<RoleAssigned>().ShouldHaveSingleItem();
    }

    [Fact]
    public void RejectedPrivilegedAssignment_NeverApplies()
    {
        var user = User();
        user.AssignRole(new RoleFacts("FINANCE_OFFICER", true, new HashSet<string>(), false, Privileged: true), [], [], Today, null, null, null, Admin, Today, Now);
        var assignment = user.RoleAssignments.Single();
        user.DecideAssignment(assignment.Id, approved: false, Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        assignment.Status.ShouldBe(AssignmentStatus.Rejected);
        user.EffectiveAssignments(Today).ShouldBeEmpty();
        user.DecideAssignment(assignment.Id, approved: true, Guid.CreateVersion7(), Now).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void OrdinaryAssignment_IsActiveAtOnce() =>
        User().AssignRole(Role("CLERK"), [], [], Today, null, null, null, Admin, Today, Now).IsSuccess.ShouldBeTrue();
}
