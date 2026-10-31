/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : General
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
using System.Text.Json;
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.General.Domain;
using Insys.General.Domain.Policies;
using Insys.General.Domain.Underwriting;
using Insys.ProductFactory.Contracts;

namespace Insys.General.Application;

// OQ-158, B-013: a batch of in-force policies from a source system.

public sealed record MigratedRisk([property: CodeList(CodeLists.RiskObjectTypes, "object_type")] string ObjectType, JsonElement Attributes, decimal? Valuation, [property: CodeList(CodeLists.ValuationBases, "code")] string? ValuationBasis, decimal SumInsured, IReadOnlyList<CoverageRequest> Coverages);

public sealed record MigratedPolicy(string SourceReference, string ProductCode, int ProductVersion, Guid PolicyholderPartyId, Guid? PayerPartyId, DateOnly PeriodStart, DateOnly PeriodEnd, string Currency, decimal AnnualPremium,
    [property: CodeList(CodeLists.BillingFrequencies, "code")] string BillingFrequency, [property: CodeList(CodeLists.CollectionMethods, "method_code")] string CollectionMethod, string? IntermediaryCode, IReadOnlyList<MigratedRisk> Risks);

/// <param name="BatchCode">The migration batch; its reconciliation reports what this file loaded.</param>
/// <param name="ControlCount">The source system's count of policies.</param>
/// <param name="ControlPremium">The source system's total annual premium.</param>
public sealed record MigrateGeneralPolicies(string BatchCode, int ControlCount, decimal ControlPremium, IReadOnlyList<MigratedPolicy> Rows) : ICommand<PolicyMigrationResult>;

public sealed record MigrationRejectedLine(int Line, string Reason);

/// <param name="Loaded">The policies loaded: all of the file, or none.</param>
public sealed record PolicyMigrationResult(int Loaded, decimal LoadedPremium, IReadOnlyList<MigrationRejectedLine> RejectedLines);

public sealed class MigrateGeneralPoliciesValidator : AbstractValidator<MigrateGeneralPolicies>
{
    public const int MaxRows = 2000;

    public MigrateGeneralPoliciesValidator()
    {
        RuleFor(c => c.BatchCode).NotEmpty().MaximumLength(30);
        RuleFor(c => c.Rows).NotEmpty().Must(r => r.Count <= MaxRows).WithMessage($"A file has at most {MaxRows} rows.");
    }
}

internal sealed class MigrateGeneralPoliciesHandler(IGeneralRepository general, IGeneralCodes codes, ISellableProducts products, Party.Contracts.IPartyDirectory parties, ITenantContext tenant,
    IBusinessDate businessDate, IClock clock, IAuditTrail audit) : ICommandHandler<MigrateGeneralPolicies, PolicyMigrationResult>
{
    public async Task<Result<PolicyMigrationResult>> Handle(MigrateGeneralPolicies command, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var today = businessDate.Today;
        var rejected = new List<MigrationRejectedLine>();
        var total = command.Rows.Sum(r => r.AnnualPremium);
        if (command.ControlCount != command.Rows.Count)
        {
            rejected.Add(new MigrationRejectedLine(0, $"The file has {command.Rows.Count} rows; its control count says {command.ControlCount}."));
        }

        if (command.ControlPremium != total)
        {
            rejected.Add(new MigrationRejectedLine(0, $"The file's premium is {total}; its control total says {command.ControlPremium}."));
        }

        var known = (await general.SourceReferencesKnown(command.Rows.Select(r => r.SourceReference).ToList(), cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ready = new List<(MigratedPolicy Row, List<(MigratedRisk Risk, RiskObject Object)> Risks)>();
        for (var i = 0; i < command.Rows.Count; i++)
        {
            var row = command.Rows[i];
            var reasons = new List<string>();
            if (string.IsNullOrWhiteSpace(row.SourceReference) || row.SourceReference.Length > 40)
            {
                reasons.Add("The source reference is required, at most 40 characters.");
            }
            else if (known.Contains(row.SourceReference) || !seen.Add(row.SourceReference))
            {
                reasons.Add("This source reference is already loaded, or appears earlier in the file.");
            }

            if (row.PeriodEnd < today || row.PeriodEnd < row.PeriodStart)
            {
                reasons.Add("Only a policy whose period hasn't ended is migrated.");
            }

            if (row.AnnualPremium <= 0 || row.Currency.Length != 3)
            {
                reasons.Add("A policy has a positive premium and a three-letter currency.");
            }

            if (await products.Version(tenantId, row.ProductCode, row.ProductVersion, today, cancellationToken) is null)
            {
                reasons.Add($"{row.ProductCode} v{row.ProductVersion} isn't a published product version.");
            }

            if (!await parties.Exists(tenantId, row.PolicyholderPartyId, cancellationToken) || (row.PayerPartyId is { } payer && !await parties.Exists(tenantId, payer, cancellationToken)))
            {
                reasons.Add("The policyholder or payer isn't a party of the tenant.");
            }

            var risks = new List<(MigratedRisk, RiskObject)>();
            foreach (var risk in row.Risks)
            {
                var schema = await RiskSchemas.Load(codes, risk.ObjectType, today, cancellationToken);
                var values = Attributes.Read(risk.Attributes);
                if (schema.IsFailure || values.IsFailure)
                {
                    reasons.Add(schema.IsFailure ? schema.Error.Message : values.Error!.Message);
                    continue;
                }

                var made = RiskObject.RegisterRiskObject(Guid.CreateVersion7(), schema.Value, values.Value, risk.Attributes.GetRawText(), risk.Valuation, risk.ValuationBasis, new RiskLocation(null, null, null), today,
                    clock.UtcNow, row.Currency.ToUpperInvariant(), row.PolicyholderPartyId);
                if (made.IsFailure)
                {
                    reasons.Add(made.Error.Message);
                }
                else
                {
                    risks.Add((risk, made.Value));
                }
            }

            if (row.Risks.Count == 0 || row.Risks.Any(r => r.SumInsured <= 0 || r.Coverages.Count == 0))
            {
                reasons.Add("A policy has at least one risk, each with a sum insured and a coverage.");
            }

            if (reasons.Count > 0)
            {
                rejected.Add(new MigrationRejectedLine(i + 1, string.Join("; ", reasons)));
            }
            else
            {
                ready.Add((row, risks));
            }
        }

        // All or nothing: no line's content is logged or audited.
        audit.Record(new AuditEntry("GENERAL", "MigrateGeneralPolicies", nameof(GeneralPolicy), command.BatchCode, new Dictionary<string, string?>
        {
            ["rows"] = command.Rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), ["rejected"] = rejected.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }));
        if (rejected.Count > 0)
        {
            return new PolicyMigrationResult(0, 0, rejected);
        }

        foreach (var (row, risks) in ready)
        {
            var id = Guid.CreateVersion7();
            var number = await PolicyNumbers.Next(codes, id, today, cancellationToken);
            if (number.IsFailure)
            {
                return number.Error;
            }

            foreach (var (_, riskObject) in risks)
            {
                general.Add(riskObject);
            }

            general.Add(GeneralPolicy.Migrate(id, number.Value, command.BatchCode, row.SourceReference, row.ProductCode, row.ProductVersion, row.PolicyholderPartyId, row.Currency.ToUpperInvariant(), row.PeriodStart, row.PeriodEnd,
                row.IntermediaryCode, new BillingArrangement(Guid.CreateVersion7(), row.PayerPartyId ?? row.PolicyholderPartyId, row.BillingFrequency, row.CollectionMethod),
                risks.Select(r => new RiskRequest(r.Object.Id, r.Risk.SumInsured, r.Risk.Coverages)).ToList(), row.AnnualPremium, today, clock.UtcNow));
        }

        return new PolicyMigrationResult(command.Rows.Count, total, []);
    }
}
