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
using System.Globalization;
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.General.Domain.Claims;

namespace Insys.General.Application;

/// <param name="PolicyId">The policy's id here, as its migration returned it.</param>
public sealed record MigratedClaim(string SourceReference, Guid PolicyId, Guid? RiskObjectId, string CoverageCode, DateOnly LossDate, [property: CodeList(CodeLists.LossCauses, "cause_code")] string CauseCode, [property: CodeList(CodeLists.Perils, "peril_code")] string PerilCode, string? Description, string ReserveType, decimal Reserve);

/// <summary>B-013: claims open in a source system, after the batch's policies. All or nothing, against the file's count and total reserve.</summary>
public sealed record MigrateGeneralClaims(string BatchCode, int ControlCount, decimal ControlReserve, IReadOnlyList<MigratedClaim> Rows) : ICommand<PolicyMigrationResult>;

public sealed class MigrateGeneralClaimsValidator : AbstractValidator<MigrateGeneralClaims>
{
    public MigrateGeneralClaimsValidator()
    {
        RuleFor(c => c.BatchCode).NotEmpty().MaximumLength(30);
        RuleFor(c => c.Rows).NotEmpty().Must(r => r.Count <= 2000).WithMessage("A file has at most 2000 rows.");
    }
}

internal sealed class MigrateGeneralClaimsHandler(IGeneralRepository general, IBusinessDate businessDate, IClock clock, IAuditTrail audit) : ICommandHandler<MigrateGeneralClaims, PolicyMigrationResult>
{
    public async Task<Result<PolicyMigrationResult>> Handle(MigrateGeneralClaims command, CancellationToken cancellationToken)
    {
        var total = command.Rows.Sum(r => r.Reserve);
        var rejected = new List<MigrationRejectedLine>();
        if (command.ControlCount != command.Rows.Count)
        {
            rejected.Add(new MigrationRejectedLine(0, $"The file has {command.Rows.Count} rows; its control count says {command.ControlCount}."));
        }

        if (command.ControlReserve != total)
        {
            rejected.Add(new MigrationRejectedLine(0, $"The file's reserve is {total}; its control total says {command.ControlReserve}."));
        }

        var known = (await general.ClaimSourceReferencesKnown(command.Rows.Select(r => r.SourceReference).ToList(), cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ready = new List<(MigratedClaim Row, Domain.Policies.GeneralPolicy Policy)>();
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

            if (row.Reserve < 0 || decimal.Round(row.Reserve, 2) != row.Reserve || row.LossDate > businessDate.Today || string.IsNullOrWhiteSpace(row.ReserveType))
            {
                reasons.Add("A claim has a reserve type, a reserve that isn't negative to two decimal places, and a loss date not in the future.");
            }

            if (await general.Policy(row.PolicyId, cancellationToken) is { } policy)
            {
                ready.Add((row, policy));
            }
            else
            {
                reasons.Add("The policy isn't here: load the batch's policies first.");
            }

            if (reasons.Count > 0)
            {
                rejected.Add(new MigrationRejectedLine(i + 1, string.Join("; ", reasons)));
            }
        }

        audit.Record(new AuditEntry("GENERAL", "MigrateGeneralClaims", nameof(GeneralClaim), command.BatchCode, new Dictionary<string, string?>
        {
            ["rows"] = command.Rows.Count.ToString(CultureInfo.InvariantCulture), ["rejected"] = rejected.Count.ToString(CultureInfo.InvariantCulture),
        }));
        if (rejected.Count > 0)
        {
            return new PolicyMigrationResult(0, 0, rejected);
        }

        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var (row, policy) in ready)
        {
            var id = ids[row.SourceReference] = Guid.CreateVersion7();
            general.Add(GeneralClaim.Migrate(id, command.BatchCode, row.SourceReference, policy.Id, row.RiskObjectId, row.CoverageCode, row.LossDate, row.CauseCode, row.PerilCode, row.Description, row.ReserveType, row.Reserve, policy.Currency, policy.ProductCode, policy.OrgNodeCode, clock.UtcNow));
        }

        return new PolicyMigrationResult(command.Rows.Count, total, [], ids);
    }
}
