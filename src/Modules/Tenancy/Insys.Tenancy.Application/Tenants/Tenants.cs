using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Tenancy.Domain.Tenants;

namespace Insys.Tenancy.Application.Tenants;

public interface ITenantRepository
{
    Task<Tenant?> Get(Guid tenantId, CancellationToken cancellationToken);

    Task<bool> CodeExists(string tenantCode, CancellationToken cancellationToken);

    void Add(Tenant tenant);
}

public sealed record TenantSummary(Guid Id, string TenantCode, string LegalName, string IsolationProfile, string Status, long RowVersion);

public sealed record StatusChangeView(string FromStatus, string ToStatus, string Reason, Guid ChangedBy, DateTimeOffset ChangedAt);

public sealed record TenantDetail(
    Guid Id, string TenantCode, string LegalName, string IsolationProfile, string Status, string? OnboardingStep, string? OnboardingStatus,
    IReadOnlyList<string> OnboardingSteps, IReadOnlyList<StatusChangeView> StatusHistory, long RowVersion);

public interface ITenantQueries
{
    Task<IReadOnlyList<TenantSummary>> List(string? status, Guid? afterId, int limit, CancellationToken cancellationToken);

    Task<TenantDetail?> Get(Guid tenantId, CancellationToken cancellationToken);
}

// Platform operator commands. Each runs as the target tenant (ITargetTenantRequest), so the onboarding case, status
// history, outbox and audit rows belong to that tenant (TEN-007).

public sealed record RegisterTenant(Guid TenantId, string TenantCode, string LegalName, IsolationProfile IsolationProfile) : ICommand<Guid>, ITargetTenantRequest
{
    public Guid TargetTenantId => TenantId;
}

public sealed record CompleteOnboardingStep(Guid TenantId, string Step, long ExpectedVersion) : ICommand<long>, ITargetTenantRequest, IVersionedCommand
{
    public Guid TargetTenantId => TenantId;
}

public enum TenantStatusAction
{
    Activate,
    Suspend,
    Reactivate,
    Terminate,
}

/// <summary>Commands ActivateTenant, SuspendTenant, ReactivateTenant and TerminateTenant.</summary>
public sealed record ChangeTenantStatus(Guid TenantId, TenantStatusAction Action, string Reason, long ExpectedVersion)
    : ICommand<long>, ITargetTenantRequest, IVersionedCommand
{
    public Guid TargetTenantId => TenantId;
}

public sealed record ChangeIsolationProfile(Guid TenantId, IsolationProfile IsolationProfile, string ApprovedMigrationRef, long ExpectedVersion)
    : ICommand<long>, ITargetTenantRequest, IVersionedCommand
{
    public Guid TargetTenantId => TenantId;
}

public sealed record ListTenants(string? Status, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<TenantSummary>>;

public sealed record GetTenant(Guid TenantId) : IQuery<TenantDetail>, ITargetTenantRequest
{
    public Guid TargetTenantId => TenantId;
}

public sealed class RegisterTenantValidator : AbstractValidator<RegisterTenant>
{
    public RegisterTenantValidator()
    {
        RuleFor(c => c.TenantId).NotEmpty();
        RuleFor(c => c.TenantCode).NotEmpty().MaximumLength(10).Matches("^[A-Z0-9_]+$").WithMessage("tenantCode is upper-case letters, digits and underscores.");
        RuleFor(c => c.LegalName).NotEmpty().MaximumLength(150);
        RuleFor(c => c.IsolationProfile).IsInEnum();
    }
}

public sealed class ChangeTenantStatusValidator : AbstractValidator<ChangeTenantStatus>
{
    public ChangeTenantStatusValidator()
    {
        RuleFor(c => c.Reason).NotEmpty().MaximumLength(2000);
        RuleFor(c => c.Action).IsInEnum();
    }
}

public sealed class CompleteOnboardingStepValidator : AbstractValidator<CompleteOnboardingStep>
{
    public CompleteOnboardingStepValidator() => RuleFor(c => c.Step).NotEmpty().Must(s => OnboardingSteps.All.Contains(s))
        .WithMessage($"step must be one of: {string.Join(", ", OnboardingSteps.All)}.");
}

internal static class Operator
{
    /// <summary>The acting operator: an identified user of the platform operator tenant (TEN-007).</summary>
    public static Result<Guid> Require(IExecutionContext execution) =>
        execution.ActorType == ActorType.User && Guid.TryParse(execution.ActorId, out var id)
            ? id
            : Error.Forbidden(ErrorCodes.Forbidden, "Tenant administration is done by identified platform operators.");

    public static async Task<Result<Tenant>> Load(ITenantRepository tenants, Guid tenantId, long expectedVersion, CancellationToken cancellationToken)
    {
        var tenant = await tenants.Get(tenantId, cancellationToken);
        if (tenant is null)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The tenant was not found.");
        }

        return tenant.RowVersion == expectedVersion
            ? tenant
            : Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The tenant has changed since it was read.");
    }
}

internal sealed class RegisterTenantHandler(ITenantRepository tenants, IExecutionContext execution, IClock clock, IAuditTrail audit)
    : ICommandHandler<RegisterTenant, Guid>
{
    public async Task<Result<Guid>> Handle(RegisterTenant command, CancellationToken cancellationToken)
    {
        var operatorId = Operator.Require(execution);
        if (operatorId.IsFailure)
        {
            return operatorId.Error;
        }

        if (await tenants.CodeExists(command.TenantCode, cancellationToken))
        {
            return Error.Conflict(TenancyErrorCodes.TenantCodeTaken, "Another tenant already uses this tenant code.");
        }

        var tenant = Tenant.RegisterTenant(command.TenantId, command.TenantCode, command.LegalName, command.IsolationProfile, clock.UtcNow);
        if (tenant.IsFailure)
        {
            return tenant.Error;
        }

        tenants.Add(tenant.Value);
        audit.Record(new AuditEntry("TENANT", "RegisterTenant", nameof(Tenant), tenant.Value.Id.ToString(),
            new Dictionary<string, string?> { ["tenantCode"] = command.TenantCode, ["isolationProfile"] = command.IsolationProfile.ToString() }));
        return tenant.Value.Id;
    }
}

internal sealed class CompleteOnboardingStepHandler(ITenantRepository tenants, IExecutionContext execution, IClock clock, IAuditTrail audit)
    : ICommandHandler<CompleteOnboardingStep, long>
{
    public async Task<Result<long>> Handle(CompleteOnboardingStep command, CancellationToken cancellationToken)
    {
        var operatorId = Operator.Require(execution);
        if (operatorId.IsFailure)
        {
            return operatorId.Error;
        }

        var tenant = await Operator.Load(tenants, command.TenantId, command.ExpectedVersion, cancellationToken);
        if (tenant.IsFailure)
        {
            return tenant.Error;
        }

        audit.Record(new AuditEntry("TENANT", "CompleteOnboardingStep", nameof(Tenant), command.TenantId.ToString(),
            new Dictionary<string, string?> { ["step"] = command.Step }));
        var result = tenant.Value.CompleteOnboardingStep(command.Step, clock.UtcNow);
        return result.IsFailure ? result.Error : tenant.Value.RowVersion + 1;
    }
}

internal sealed class ChangeTenantStatusHandler(ITenantRepository tenants, IExecutionContext execution, IClock clock, IAuditTrail audit)
    : ICommandHandler<ChangeTenantStatus, long>
{
    public async Task<Result<long>> Handle(ChangeTenantStatus command, CancellationToken cancellationToken)
    {
        var operatorId = Operator.Require(execution);
        if (operatorId.IsFailure)
        {
            return operatorId.Error;
        }

        var tenant = await Operator.Load(tenants, command.TenantId, command.ExpectedVersion, cancellationToken);
        if (tenant.IsFailure)
        {
            return tenant.Error;
        }

        var now = clock.UtcNow;
        var operation = $"{command.Action}Tenant";
        audit.Record(new AuditEntry("TENANT", operation, nameof(Tenant), command.TenantId.ToString(),
            new Dictionary<string, string?> { ["from"] = tenant.Value.Status.ToString() }));
        var result = command.Action switch
        {
            TenantStatusAction.Activate => tenant.Value.ActivateTenant(operatorId.Value, command.Reason, now),
            TenantStatusAction.Suspend => tenant.Value.SuspendTenant(operatorId.Value, command.Reason, now),
            TenantStatusAction.Reactivate => tenant.Value.ReactivateTenant(operatorId.Value, command.Reason, now),
            _ => tenant.Value.TerminateTenant(operatorId.Value, command.Reason, now),
        };
        return result.IsFailure ? result.Error : tenant.Value.RowVersion + 1;
    }
}

internal sealed class ChangeIsolationProfileHandler(ITenantRepository tenants, IExecutionContext execution, IClock clock, IAuditTrail audit)
    : ICommandHandler<ChangeIsolationProfile, long>
{
    public async Task<Result<long>> Handle(ChangeIsolationProfile command, CancellationToken cancellationToken)
    {
        var operatorId = Operator.Require(execution);
        if (operatorId.IsFailure)
        {
            return operatorId.Error;
        }

        var tenant = await Operator.Load(tenants, command.TenantId, command.ExpectedVersion, cancellationToken);
        if (tenant.IsFailure)
        {
            return tenant.Error;
        }

        audit.Record(new AuditEntry("TENANT", "ChangeIsolationProfile", nameof(Tenant), command.TenantId.ToString(),
            new Dictionary<string, string?> { ["to"] = command.IsolationProfile.ToString(), ["migration"] = command.ApprovedMigrationRef }));
        var result = tenant.Value.ChangeIsolationProfile(command.IsolationProfile, command.ApprovedMigrationRef, operatorId.Value, clock.UtcNow);
        return result.IsFailure ? result.Error : tenant.Value.RowVersion + 1;
    }
}

internal sealed class ListTenantsHandler(ITenantQueries queries) : IQueryHandler<ListTenants, IReadOnlyList<TenantSummary>>
{
    public async Task<Result<IReadOnlyList<TenantSummary>>> Handle(ListTenants query, CancellationToken cancellationToken) =>
        Result.Success(await queries.List(query.Status, query.AfterId, query.Limit, cancellationToken));
}

internal sealed class GetTenantHandler(ITenantQueries queries) : IQueryHandler<GetTenant, TenantDetail>
{
    public async Task<Result<TenantDetail>> Handle(GetTenant query, CancellationToken cancellationToken) =>
        await queries.Get(query.TenantId, cancellationToken) is { } tenant ? tenant : Error.NotFound(ErrorCodes.NotFound, "The tenant was not found.");
}
