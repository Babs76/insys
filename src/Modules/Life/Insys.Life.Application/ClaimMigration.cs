/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Life
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
using Insys.Life.Domain.Claims;
using Insys.Life.Domain.Policies;

namespace Insys.Life.Application;

/// <param name="PolicyId">The policy's id here, as its migration returned it.</param>
/// <param name="InsuredPartyId">The insured life on the policy.</param>
public sealed record MigratedLifeClaim(string SourceReference, Guid PolicyId, Guid InsuredPartyId, Guid ClaimantPartyId, string ClaimType, DateOnly EventDate, string? CauseCode, string ReserveType, decimal Reserve);

/// <summary>B-013: claims open in a source system, after the batch's policies. All or nothing, against the file's count and total reserve.</summary>
public sealed record MigrateLifeClaims(string BatchCode, int ControlCount, decimal ControlReserve, string Currency, IReadOnlyList<MigratedLifeClaim> Rows) : ICommand<LifeMigrationResult>;

public sealed class MigrateLifeClaimsValidator : AbstractValidator<MigrateLifeClaims>
{
    public MigrateLifeClaimsValidator()
    {
        RuleFor(c => c.BatchCode).NotEmpty().MaximumLength(30);
        RuleFor(c => c.Rows).NotEmpty().Must(r => r.Count <= 2000).WithMessage("A file has at most 2000 rows.");
    }
}

internal sealed class MigrateLifeClaimsHandler(ILifeRepository life, IBusinessDate businessDate, IClock clock, IAuditTrail audit) : ICommandHandler<MigrateLifeClaims, LifeMigrationResult>
{
    public async Task<Result<LifeMigrationResult>> Handle(MigrateLifeClaims command, CancellationToken cancellationToken)
    {
        var total = command.Rows.Sum(r => r.Reserve);
        var rejected = new List<LifeMigrationRejectedLine>();
        if (command.ControlCount != command.Rows.Count)
        {
            rejected.Add(new LifeMigrationRejectedLine(0, $"The file has {command.Rows.Count} rows; its control count says {command.ControlCount}."));
        }

        if (command.ControlReserve != total)
        {
            rejected.Add(new LifeMigrationRejectedLine(0, $"The file's reserve is {total}; its control total says {command.ControlReserve}."));
        }

        var known = (await life.ClaimSourceReferencesKnown(command.Rows.Select(r => r.SourceReference).ToList(), cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ready = new List<(MigratedLifeClaim Row, LifePolicy Policy, Guid InsuredLife)>();
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

            if (row.Reserve < 0 || decimal.Round(row.Reserve, 2) != row.Reserve || row.EventDate > businessDate.Today || string.IsNullOrWhiteSpace(row.ReserveType))
            {
                reasons.Add("A claim has a reserve type, a reserve that isn't negative to two decimal places, and an event date not in the future.");
            }

            var policy = await life.Policy(row.PolicyId, cancellationToken);
            if (policy is not null && !string.Equals(policy.Currency, command.Currency, StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add($"The policy's currency isn't the file's, {command.Currency}.");
            }

            if (policy?.Lives.FirstOrDefault(l => l.PartyId == row.InsuredPartyId) is { } insured)
            {
                ready.Add((row, policy, insured.Id));
            }
            else
            {
                reasons.Add("The policy, or that insured life on it, isn't here: load the batch's policies first.");
            }

            if (reasons.Count > 0)
            {
                rejected.Add(new LifeMigrationRejectedLine(i + 1, string.Join("; ", reasons)));
            }
        }

        audit.Record(new AuditEntry("LIFE", "MigrateLifeClaims", nameof(LifeClaim), command.BatchCode, new Dictionary<string, string?>
        {
            ["rows"] = command.Rows.Count.ToString(CultureInfo.InvariantCulture), ["rejected"] = rejected.Count.ToString(CultureInfo.InvariantCulture),
        }));
        if (rejected.Count > 0)
        {
            return new LifeMigrationResult(0, 0, rejected, null, command.Currency);
        }

        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var (row, policy, insuredLife) in ready)
        {
            var id = ids[row.SourceReference] = Guid.CreateVersion7();
            life.Add(LifeClaim.Migrate(id, command.BatchCode, row.SourceReference, policy, row.ClaimType, row.EventDate, row.CauseCode, row.ClaimantPartyId, insuredLife, row.ReserveType, row.Reserve, clock.UtcNow));
        }

        return new LifeMigrationResult(command.Rows.Count, total, [], ids, command.Currency.ToUpperInvariant());
    }
}
