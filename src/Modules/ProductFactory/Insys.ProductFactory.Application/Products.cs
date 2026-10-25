/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : ProductFactory
 * CONFIDENTIALITY AND INTELLECTUAL PROPERTY NOTICE
 * This source code and its associated documentation contain confidential and
 * proprietary information belonging to OpenFactor Technology Group or its
 * applicable intellectual property rights holder.
 * This material is provided solely for authorised development, maintenance,
 * operation or evaluation purposes, as applicable. No person may reproduce,
 * modify, distribute, disclose, publish or otherwise use this material except
 * as expressly permitted by an applicable written agreement or licence.
 * No ownership rights or licence to any intellectual property are granted
 * by the inclusion of this notice, except as expressly provided in an
 * applicable written agreement or licence.
 * Third-party software, open-source components and customer-owned materials
 * remain subject to their respective ownership rights and applicable licences
 * or agreements.
 * UNAUTHORISED ACCESS, USE, COPYING OR DISCLOSURE IS PROHIBITED.
 * ============================================================================
 */
using System.Globalization;
using System.Text.Json;
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Contracts.StaticData;
using Insys.BuildingBlocks.Domain;
using Insys.ProductFactory.Contracts;
using Insys.ProductFactory.Domain.Products;
using Insys.Rating.Contracts;
using Insys.Workflow.Contracts;

namespace Insys.ProductFactory.Application;

/// <summary>Product versions are rows of product.cfg_product_definitions_and_versions; the repository keeps the rows in step with the aggregates it hands out.</summary>
public interface IProductVersionRepository
{
    Task<ProductVersion?> Get(Guid id, CancellationToken cancellationToken);

    Task<ProductVersion?> Get(string productCode, int version, CancellationToken cancellationToken);

    /// <summary>Every version of the product, newest first.</summary>
    Task<IReadOnlyList<ProductVersion>> Versions(string productCode, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> InStatus(ProductVersionStatus status, DateOnly? effectiveOnOrBefore, int limit, CancellationToken cancellationToken);

    void Add(ProductVersion version);
}

/// <summary>The Product module's static data that product versions are checked against.</summary>
public interface IProductCatalogue
{
    /// <summary>Whether the taxonomy in force has the family (and class) under the domain's line.</summary>
    Task<bool> FamilyExists(ProductDomain domain, string familyCode, string? classCode, DateOnly day, CancellationToken cancellationToken);

    Task<ProductValidationContext> ValidationContext(ProductVersion version, DateOnly day, CancellationToken cancellationToken);

    /// <summary>cfg_product_approval_workflow_settings: whether simulation is required before submission (default true).</summary>
    Task<bool> SimulationRequired(DateOnly day, CancellationToken cancellationToken);
}

public interface ISimulationRuns
{
    Task<SimulationRun?> Get(Guid id, CancellationToken cancellationToken);

    /// <summary>The version's runs, newest first.</summary>
    Task<IReadOnlyList<SimulationRun>> For(string productCode, int version, CancellationToken cancellationToken);

    void Add(SimulationRun run);
}

public sealed record ProductVersionView(Guid Id, string ProductCode, int Version, string Domain, string Name, IReadOnlyList<string> Coverages, ProductVersionStatus Status,
    DateOnly EffectiveFrom, DateOnly? EffectiveTo, string? FamilyCode, string? ClassCode, string CoverStartsOn, string? ApprovedBy, DateTimeOffset? ApprovedAt,
    long RowVersion, string? EligibilityRulesetCode, string ReinstatementArrears, LatestRejection? LatestRejection = null)
{
    public static ProductVersionView From(ProductVersion v) => new(v.Id, v.ProductCode, v.Version, v.Domain.ToString().ToUpperInvariant(), v.Name, v.Coverages,
        v.Status, v.EffectiveFrom, v.EffectiveTo, v.FamilyCode, v.ClassCode, v.CoverStartsOn.ToString(), v.ApprovedBy, v.ApprovedAt, v.RowVersion,
        v.EligibilityRulesetCode, v.ReinstatementArrears.ToString());
}

public sealed record SimulationResultView(string CaseId, JsonElement Expected, JsonElement Actual, bool Passed);

public sealed record SimulationRunView(Guid Id, string RunNumber, string ProductCode, int ProductVersion, Guid StartedBy, DateTimeOffset StartedAt, SimulationStatus Status,
    int CasesTotal, int CasesFailed, IReadOnlyList<SimulationResultView> Results)
{
    public static SimulationRunView From(SimulationRun r) => new(r.Id, r.RunNumber, r.ProductCode, r.ProductVersion, r.StartedBy, r.StartedAt, r.Status,
        r.CasesTotal, r.CasesFailed, r.Results.OrderBy(x => x.CaseId, StringComparer.Ordinal).Select(x => new SimulationResultView(x.CaseId, Parse(x.Expected), Parse(x.Actual), x.Passed)).ToList());

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

/// <summary>ProductDefinition's RegisterProduct: version 1, in Draft.</summary>
public sealed record RegisterProduct(string ProductCode, ProductDomain Domain, string FamilyCode, string? ClassCode, string Name, IReadOnlyList<string> Coverages,
    DateOnly EffectiveFrom) : ICommand<ProductVersionView>;

/// <summary>The next version of a product, copied from its latest version (PRD-003).</summary>
public sealed record CreateDraftVersion(string ProductCode, DateOnly EffectiveFrom) : ICommand<ProductVersionView>;

/// <param name="CoverStartsOn">When cover starts on the version's policies; unchanged when omitted.</param>
/// <param name="EligibilityRulesetCode">The eligibility rule set proposals must pass; unchanged when omitted, cleared when empty.</param>
/// <param name="ReinstatementArrears">Whether arrears must be paid before reinstatement; unchanged when omitted.</param>
public sealed record EditConfiguration(string ProductCode, int Version, string Name, IReadOnlyList<string> Coverages, DateOnly EffectiveFrom, long ExpectedVersion,
    CoverStart? CoverStartsOn = null, string? EligibilityRulesetCode = null, ReinstatementArrears? ReinstatementArrears = null) : ICommand<long>, IVersionedCommand;

public sealed record ValidateVersion(string ProductCode, int Version, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public sealed record RunSimulation(string ProductCode, int Version) : ICommand<SimulationRunView>;

public sealed record SubmitForApproval(string ProductCode, int Version, string? Reason, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public sealed record WithdrawVersion(string ProductCode, int Version, DateOnly EffectiveTo, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public sealed record RetireVersion(string ProductCode, int Version, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

/// <summary>Transition "Effective date reached", run by the product job as a service identity.</summary>
public sealed record MakeVersionEffective(Guid VersionId) : ICommand<long>;

public sealed record GetProductVersion(string ProductCode, int Version) : IQuery<ProductVersionView>;

public sealed record ListProductVersions(string ProductCode) : IQuery<IReadOnlyList<ProductVersionView>>;

public sealed record ListSimulationRuns(string ProductCode, int Version) : IQuery<IReadOnlyList<SimulationRunView>>;

/// <summary>
/// A product in the product list (GAP-023): its latest version and that version's status, and the version in force
/// (Published or Effective), if any. FamilyCode and ClassCode are the latest version's; a General product's line is
/// its class code, or its family code when it has no class.
/// </summary>
public sealed record ProductSummary(string ProductCode, string Name, ProductDomain Domain, string? FamilyCode, string? ClassCode, int LatestVersion,
    ProductVersionStatus LatestStatus, DateOnly EffectiveFrom, DateOnly? EffectiveTo, int? InForceVersion);

/// <summary>Query ListProducts (GAP-023): the tenant's products by code, optionally by domain and by their latest version's status.</summary>
public sealed record ListProducts(ProductDomain? Domain, ProductVersionStatus? Status, string? AfterCode, int Limit) : IQuery<IReadOnlyList<ProductSummary>>;

public interface IProductList
{
    Task<IReadOnlyList<ProductSummary>> List(ProductDomain? domain, ProductVersionStatus? status, string? afterCode, int limit, CancellationToken cancellationToken);
}

public sealed record VersionsDueToBecomeEffective(DateOnly Today) : IQuery<IReadOnlyList<Guid>>;

public sealed class RegisterProductValidator : AbstractValidator<RegisterProduct>
{
    public RegisterProductValidator()
    {
        RuleFor(c => c.ProductCode).NotEmpty().MaximumLength(ProductVersion.MaxCodeLength);
        RuleFor(c => c.FamilyCode).NotEmpty().MaximumLength(15);
        RuleFor(c => c.ClassCode).MaximumLength(15);
        RuleFor(c => c.Name).NotEmpty().MaximumLength(100);
        RuleFor(c => c.Coverages).NotNull();
    }
}

public sealed class EditConfigurationValidator : AbstractValidator<EditConfiguration>
{
    public EditConfigurationValidator()
    {
        RuleFor(c => c.Name).NotEmpty().MaximumLength(100);
        RuleFor(c => c.Coverages).NotNull();
    }
}

internal static class Users
{
    public static Result<Guid> Require(IExecutionContext execution) =>
        execution.ActorType == ActorType.User && Guid.TryParse(execution.ActorId, out var id)
            ? id
            : Error.Forbidden(ErrorCodes.Forbidden, "Product versions are changed by identified users.");
}

internal static class Versions
{
    public static async Task<Result<ProductVersion>> Load(IProductVersionRepository versions, string code, int number, long? expected, CancellationToken cancellationToken)
    {
        var version = await versions.Get(code, number, cancellationToken);
        if (version is null)
        {
            return Error.NotFound(ErrorCodes.NotFound, $"Product {code} has no version {number}.");
        }

        return expected is { } v && version.RowVersion != v
            ? Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The product version has changed since it was read.")
            : version;
    }
}

internal sealed class RegisterProductHandler(IProductVersionRepository versions, IProductCatalogue catalogue, IBusinessDate businessDate, IClock clock, IAuditTrail audit)
    : ICommandHandler<RegisterProduct, ProductVersionView>
{
    public async Task<Result<ProductVersionView>> Handle(RegisterProduct command, CancellationToken cancellationToken)
    {
        if ((await versions.Versions(command.ProductCode, cancellationToken)).Count > 0)
        {
            return Error.Conflict(ProductErrorCodes.CodeInUse, $"Product code {command.ProductCode} is in use or was used before; codes are never reused.");
        }

        if (!await catalogue.FamilyExists(command.Domain, command.FamilyCode, command.ClassCode, businessDate.Today, cancellationToken))
        {
            return Error.Validation(ProductErrorCodes.FamilyNotFound,
                $"The product taxonomy in force has no family {command.FamilyCode}{(command.ClassCode is null ? "" : $" with class {command.ClassCode}")} for {command.Domain}.");
        }

        var version = ProductVersion.RegisterProduct(Guid.CreateVersion7(), command.ProductCode, command.Domain, command.FamilyCode, command.ClassCode, command.Name,
            command.Coverages, command.EffectiveFrom, clock.UtcNow);
        versions.Add(version);
        audit.Record(new AuditEntry("PRODUCT", "RegisterProduct", nameof(ProductVersion), version.Id.ToString(),
            new Dictionary<string, string?> { ["product"] = command.ProductCode }));
        return ProductVersionView.From(version);
    }
}

internal sealed class CreateDraftVersionHandler(IProductVersionRepository versions, IAuditTrail audit) : ICommandHandler<CreateDraftVersion, ProductVersionView>
{
    public async Task<Result<ProductVersionView>> Handle(CreateDraftVersion command, CancellationToken cancellationToken)
    {
        var all = await versions.Versions(command.ProductCode, cancellationToken);
        if (all.Count == 0)
        {
            return Error.NotFound(ErrorCodes.NotFound, $"Product {command.ProductCode} is not registered.");
        }

        if (all.FirstOrDefault(v => v.IsInProgress) is { } open)
        {
            return Error.Conflict(ProductErrorCodes.VersionInProgress, $"Version {open.Version} of {command.ProductCode} is {open.Status}; finish it first.");
        }

        var version = ProductVersion.CreateDraftVersion(Guid.CreateVersion7(), all[0], command.EffectiveFrom);
        versions.Add(version);
        audit.Record(new AuditEntry("PRODUCT", "CreateDraftVersion", nameof(ProductVersion), version.Id.ToString(),
            new Dictionary<string, string?> { ["product"] = command.ProductCode, ["version"] = version.Version.ToString(CultureInfo.InvariantCulture) }));
        return ProductVersionView.From(version);
    }
}

internal sealed class EditConfigurationHandler(IProductVersionRepository versions, ISimulationRuns runs) : ICommandHandler<EditConfiguration, long>
{
    public async Task<Result<long>> Handle(EditConfiguration command, CancellationToken cancellationToken)
    {
        var version = await Versions.Load(versions, command.ProductCode, command.Version, command.ExpectedVersion, cancellationToken);
        if (version.IsFailure)
        {
            return version.Error;
        }

        var result = version.Value.EditConfiguration(command.Name, command.Coverages, command.EffectiveFrom, command.CoverStartsOn,
            command.EligibilityRulesetCode, command.ReinstatementArrears);
        if (result.IsFailure)
        {
            return result.Error;
        }

        await SimulationRuns.MarkStale(runs, version.Value, cancellationToken);
        return version.Value.RowVersion + 1;
    }
}

internal sealed class ValidateVersionHandler(IProductVersionRepository versions, IProductCatalogue catalogue, IBusinessDate businessDate, IClock clock) : ICommandHandler<ValidateVersion, long>
{
    public async Task<Result<long>> Handle(ValidateVersion command, CancellationToken cancellationToken)
    {
        var version = await Versions.Load(versions, command.ProductCode, command.Version, command.ExpectedVersion, cancellationToken);
        if (version.IsFailure)
        {
            return version.Error;
        }

        var context = await catalogue.ValidationContext(version.Value, businessDate.Today, cancellationToken);
        var result = version.Value.ValidateVersion(context, clock.UtcNow);
        return result.IsFailure ? result.Error : version.Value.RowVersion + 1;
    }
}

/// <summary>Command RunSimulation: Rating's test case library for the version, rated as at its effective date, as a dry run (PRD-005).</summary>
internal sealed class RunSimulationHandler(IProductVersionRepository versions, ISimulationRuns runs, IPremiumSimulator simulator, IExecutionContext execution,
    IClock clock) : ICommandHandler<RunSimulation, SimulationRunView>
{
    public async Task<Result<SimulationRunView>> Handle(RunSimulation command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        var version = await Versions.Load(versions, command.ProductCode, command.Version, null, cancellationToken);
        if (user.IsFailure || version.IsFailure)
        {
            return user.IsFailure ? user.Error : version.Error!;
        }

        if (version.Value.Status != ProductVersionStatus.Validated)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "Simulations run on a validated version, so they test the configuration that is submitted.");
        }

        var results = await simulator.RunTestCases(command.ProductCode, command.Version, version.Value.EffectiveFrom, cancellationToken);
        var cases = results.Select(r => new SimulationCase(r.CaseId,
            JsonSerializer.Serialize(new { premium = r.ExpectedPremium, tolerance = r.Tolerance }, JsonSerializerOptions.Web),
            JsonSerializer.Serialize(new { premium = r.ActualPremium, error = r.ErrorCode }, JsonSerializerOptions.Web), r.Passed)).ToList();
        var run = SimulationRun.Record(Guid.CreateVersion7(), version.Value, user.Value, cases, clock.UtcNow);
        runs.Add(run);
        return SimulationRunView.From(run);
    }
}

internal sealed class SubmitForApprovalHandler(IProductVersionRepository versions, ISimulationRuns runs, IProductCatalogue catalogue, IExecutionContext execution,
    IBusinessDate businessDate, IClock clock, IAuditTrail audit) : ICommandHandler<SubmitForApproval, long>
{
    public async Task<Result<long>> Handle(SubmitForApproval command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        var version = await Versions.Load(versions, command.ProductCode, command.Version, command.ExpectedVersion, cancellationToken);
        if (user.IsFailure || version.IsFailure)
        {
            return user.IsFailure ? user.Error : version.Error!;
        }

        var passed = !await catalogue.SimulationRequired(businessDate.Today, cancellationToken)
            || (await runs.For(command.ProductCode, command.Version, cancellationToken)).FirstOrDefault()?.Status == SimulationStatus.Passed;
        audit.Record(new AuditEntry("PRODUCT", "SubmitForApproval", nameof(ProductVersion), version.Value.Id.ToString(),
            new Dictionary<string, string?> { ["product"] = command.ProductCode, ["version"] = command.Version.ToString(CultureInfo.InvariantCulture) }));
        var result = version.Value.SubmitForApproval(user.Value, passed, command.Reason ?? "", clock.UtcNow);
        return result.IsFailure ? result.Error : version.Value.RowVersion + 1;
    }
}

internal sealed class WithdrawVersionHandler(IProductVersionRepository versions, IBusinessDate businessDate, IClock clock, IAuditTrail audit) : ICommandHandler<WithdrawVersion, long>
{
    public async Task<Result<long>> Handle(WithdrawVersion command, CancellationToken cancellationToken)
    {
        var version = await Versions.Load(versions, command.ProductCode, command.Version, command.ExpectedVersion, cancellationToken);
        if (version.IsFailure)
        {
            return version.Error;
        }

        audit.Record(new AuditEntry("PRODUCT", "WithdrawVersion", nameof(ProductVersion), version.Value.Id.ToString()));
        var result = version.Value.WithdrawVersion(command.EffectiveTo, businessDate.Today, clock.UtcNow);
        return result.IsFailure ? result.Error : version.Value.RowVersion + 1;
    }
}

internal sealed class RetireVersionHandler(IProductVersionRepository versions, IAuditTrail audit) : ICommandHandler<RetireVersion, long>
{
    public async Task<Result<long>> Handle(RetireVersion command, CancellationToken cancellationToken)
    {
        var version = await Versions.Load(versions, command.ProductCode, command.Version, command.ExpectedVersion, cancellationToken);
        if (version.IsFailure)
        {
            return version.Error;
        }

        audit.Record(new AuditEntry("PRODUCT", "RetireVersion", nameof(ProductVersion), version.Value.Id.ToString()));
        var result = version.Value.RetireVersion();
        return result.IsFailure ? result.Error : version.Value.RowVersion + 1;
    }
}

internal sealed class MakeVersionEffectiveHandler(IProductVersionRepository versions, IBusinessDate businessDate) : ICommandHandler<MakeVersionEffective, long>
{
    public async Task<Result<long>> Handle(MakeVersionEffective command, CancellationToken cancellationToken)
    {
        var version = await versions.Get(command.VersionId, cancellationToken);
        if (version is null)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The product version was not found.");
        }

        var result = version.BecomeEffective(businessDate.Today);
        return result.IsFailure ? result.Error : version.RowVersion + 1;
    }
}

internal sealed class GetProductVersionHandler(IProductVersionRepository versions, IApprovalHistory history) : IQueryHandler<GetProductVersion, ProductVersionView>
{
    public async Task<Result<ProductVersionView>> Handle(GetProductVersion query, CancellationToken cancellationToken) =>
        await versions.Get(query.ProductCode, query.Version, cancellationToken) is { } version
            ? ProductVersionView.From(version) with { LatestRejection = await history.Latest(ProductApprovals.BusinessRefType, version.Id, cancellationToken) }
            : Error.NotFound(ErrorCodes.NotFound, $"Product {query.ProductCode} has no version {query.Version}.");
}

internal sealed class ListProductVersionsHandler(IProductVersionRepository versions) : IQueryHandler<ListProductVersions, IReadOnlyList<ProductVersionView>>
{
    public async Task<Result<IReadOnlyList<ProductVersionView>>> Handle(ListProductVersions query, CancellationToken cancellationToken) =>
        (await versions.Versions(query.ProductCode, cancellationToken)).Select(ProductVersionView.From).ToList();
}

internal sealed class ListProductsHandler(IProductList products) : IQueryHandler<ListProducts, IReadOnlyList<ProductSummary>>
{
    public async Task<Result<IReadOnlyList<ProductSummary>>> Handle(ListProducts query, CancellationToken cancellationToken) =>
        Result.Success(await products.List(query.Domain, query.Status, query.AfterCode, query.Limit, cancellationToken));
}

internal sealed class ListSimulationRunsHandler(ISimulationRuns runs) : IQueryHandler<ListSimulationRuns, IReadOnlyList<SimulationRunView>>
{
    public async Task<Result<IReadOnlyList<SimulationRunView>>> Handle(ListSimulationRuns query, CancellationToken cancellationToken) =>
        (await runs.For(query.ProductCode, query.Version, cancellationToken)).Select(SimulationRunView.From).ToList();
}

internal sealed class VersionsDueToBecomeEffectiveHandler(IProductVersionRepository versions) : IQueryHandler<VersionsDueToBecomeEffective, IReadOnlyList<Guid>>
{
    public async Task<Result<IReadOnlyList<Guid>>> Handle(VersionsDueToBecomeEffective query, CancellationToken cancellationToken) =>
        Result.Success(await versions.InStatus(ProductVersionStatus.Published, query.Today, 500, cancellationToken));
}

internal static class SimulationRuns
{
    /// <summary>A run tests one configuration: when it changes, earlier runs no longer count (PRD-005).</summary>
    public static async Task MarkStale(ISimulationRuns runs, ProductVersion version, CancellationToken cancellationToken)
    {
        foreach (var run in await runs.For(version.ProductCode, version.Version, cancellationToken))
        {
            run.MarkStale();
        }
    }
}

/// <summary>
/// Product's policies. Workflow's decision on a PRODUCT_PUBLICATION approval: granted → ApproveVersion, then
/// PublishVersion (domain-model.json: ApprovalGranted → PublishVersion), closing the version it replaces at its
/// effective date; rejected → back to Draft. A change to the coverage library or benefit dependency rules
/// revalidates Validated versions (StaticDataVersionActivated → "Revalidate drafts that depend on changed static data").
/// </summary>
public sealed class ProductPolicies(IProductVersionRepository versions, ISimulationRuns runs, IProductCatalogue catalogue, IBusinessDate businessDate, IClock clock) :
    IIntegrationEventHandler<ApprovalGranted>,
    IIntegrationEventHandler<ApprovalRejected>,
    IIntegrationEventHandler<StaticDataVersionActivated>
{
    private static readonly string[] ValidationTables = ["cfg_coverage_and_benefit_library", "cfg_benefit_dependency_rules"];

    public async Task Handle(ApprovalGranted granted, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (granted.BusinessRefType != ProductApprovals.BusinessRefType || await versions.Get(granted.BusinessRefId, cancellationToken) is not { } version)
        {
            return;
        }

        var now = clock.UtcNow;
        if (version.ApproveVersion(granted.CheckerId, now).IsFailure)
        {
            return;
        }

        if (version.SupersedesId is { } supersedes && await versions.Get(supersedes, cancellationToken) is { } previous)
        {
            previous.CloseAt(version.EffectiveFrom);
        }

        version.PublishVersion(now);
    }

    public async Task Handle(ApprovalRejected rejected, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (rejected.BusinessRefType != ProductApprovals.BusinessRefType || await versions.Get(rejected.BusinessRefId, cancellationToken) is not { } version
            || version.RejectionApplied)
        {
            return;
        }

        // Every ending (rejected, expired, withdrawn) returns it to Draft; one it would not take parks the delivery (D-5, ADR-022).
        if (version.RejectApproval().IsFailure)
        {
            throw new PrerequisiteMissingException(PrerequisiteCodes.ApprovalEndingNotApplied, $"{rejected.ApprovalType}:{rejected.ApprovalId}");
        }

        await SimulationRuns.MarkStale(runs, version, cancellationToken);
    }

    public async Task Handle(StaticDataVersionActivated activated, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (activated.Record.Schema != "product" || !ValidationTables.Contains(activated.Record.Table))
        {
            return;
        }

        foreach (var id in await versions.InStatus(ProductVersionStatus.Validated, null, 500, cancellationToken))
        {
            if (await versions.Get(id, cancellationToken) is { } version
                && version.Revalidate(await catalogue.ValidationContext(version, businessDate.Today, cancellationToken)))
            {
                await SimulationRuns.MarkStale(runs, version, cancellationToken);
            }
        }
    }
}
