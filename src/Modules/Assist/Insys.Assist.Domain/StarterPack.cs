/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Assist
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
using Insys.BuildingBlocks.Domain;

namespace Insys.Assist.Domain;

public static class PackKinds
{
    public const string Base = "BASE";
    public const string Country = "COUNTRY";
    public const string Region = "REGION";
    public const string Line = "LINE";

    public static readonly IReadOnlySet<string> All = new HashSet<string>([Base, Country, Region, Line], StringComparer.Ordinal);
}

/// <summary>Who may use a version (ai-assist.md §5.3): every tenant, on the platform's approval, or the one tenant whose compliance team approved it (the fallback).</summary>
public static class PackScopes
{
    public const string AllTenants = "ALL_TENANTS";
    public const string TenantOnly = "TENANT_ONLY";
}

public static class PackStatuses
{
    public const string Draft = "DRAFT";
    public const string Reviewed = "REVIEWED";
    public const string Approved = "APPROVED";
}

/// <summary>Where a row's mark came from (D-20).</summary>
public static class MarkSources
{
    /// <summary>The row says.</summary>
    public const string Explicit = "EXPLICIT";

    /// <summary>`regulated` read from the row's class (FINANCE or COMPLIANCE), an interpretation until the row says.</summary>
    public const string Interim = "INTERIM";

    /// <summary>No `tailorable` mark, so not tailorable.</summary>
    public const string Default = "DEFAULT";
}

public static class PackErrorCodes
{
    /// <summary>A regulated value can come only from an approved overlay (§5.3, AIA-082).</summary>
    [ErrorCode(422, "error.assist.pack_not_approved", Means = "A regulated value can come only from an overlay version that has been approved for your tenant, and this one hasn't been.",
        Remedy = "Use an approved version, or ask for this one to be reviewed and approved.")]
    public const string NotApproved = "ASSIST_PACK_NOT_APPROVED";

    /// <summary>The approval names content other than the version's own (§5.3: the approval is bound to the version's hash).</summary>
    [ErrorCode(409, "error.assist.pack_changed", Means = "The approval was for different content from the version as it now stands.",
        Remedy = "Read the version again, check its review sheet, and approve the content it now has.")]
    public const string Changed = "ASSIST_PACK_CHANGED";
}

/// <summary>
/// One version of a starter pack: the base pack, a country or regional overlay, or a line overlay (ai-assist.md §5.2, D-18), with the overlay lifecycle of §5.3.
/// Its rows (<see cref="StarterPackValue"/>) never change after loading; its content hash covers them, their marks and the marks' sources.
/// <list type="bullet">
/// <item>The normal route (scope ALL_TENANTS): DRAFT, then REVIEWED with the partner's review recorded as evidence, then APPROVED by the platform's compliance lead.</item>
/// <item>The fallback (TENANT_ONLY): the tenant's own version, approved by its compliance officer with no partner review (D-19).</item>
/// <item>In both, whoever prepared the version or marked it REVIEWED can't approve it, and the approval names the hash it approves.</item>
/// </list>
/// </summary>
public sealed class StarterPack : AggregateRoot<Guid>
{
    private StarterPack()
    {
    }

    public string PackCode { get; private set; } = "";

    public string Kind { get; private set; } = "";

    public int Version { get; private set; }

    /// <summary>The pack this one builds on, such as a national overlay on the CIMA one.</summary>
    public string? BuildsOn { get; private set; }

    public string? Country { get; private set; }

    public string? Line { get; private set; }

    public string Scope { get; private set; } = "";

    public string Status { get; private set; } = PackStatuses.Draft;

    public string ContentHash { get; private set; } = "";

    public int RowCount { get; private set; }

    public string PreparedBy { get; private set; } = "";

    public string? ReviewedBy { get; private set; }

    /// <summary>The local compliance partner, outside INSYS (§5.3).</summary>
    public string? ReviewPartner { get; private set; }

    /// <summary>The partner's reviewer, by name.</summary>
    public string? ReviewerName { get; private set; }

    public DateOnly? ReviewedOn { get; private set; }

    /// <summary>The signed review document, kept as evidence on the version.</summary>
    public Guid? ReviewDocumentId { get; private set; }

    public string? ApprovedBy { get; private set; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    public static Result<StarterPack> Load(string packCode, string kind, int version, string? buildsOn, string? country, string? line, string scope, string contentHash,
        int rowCount, string preparedBy)
    {
        if (!PackKinds.All.Contains(kind))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "kind is BASE, COUNTRY, REGION or LINE.");
        }

        if (scope is not (PackScopes.AllTenants or PackScopes.TenantOnly))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "scope is ALL_TENANTS or TENANT_ONLY.");
        }

        if (version < 1 || rowCount < 1)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A pack version is numbered from 1 and has at least one row.");
        }

        return new StarterPack
        {
            Id = Guid.CreateVersion7(), PackCode = Guard.MaxLength(Guard.NotNullOrWhiteSpace(packCode), 40), Kind = kind, Version = version, BuildsOn = buildsOn, Country = country,
            Line = line, Scope = scope, ContentHash = contentHash, RowCount = rowCount, PreparedBy = preparedBy,
        };
    }

    /// <summary>The partner's review, recorded with its evidence (§5.3). Only a draft is reviewed.</summary>
    public Result MarkReviewed(string by, string partner, string reviewerName, DateOnly reviewedOn, Guid reviewDocumentId)
    {
        if (Status != PackStatuses.Draft)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Only a draft version is marked reviewed; this one is {Status}.");
        }

        if (string.IsNullOrWhiteSpace(partner) || string.IsNullOrWhiteSpace(reviewerName))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A review names the partner and the reviewer.");
        }

        (Status, ReviewedBy, ReviewPartner, ReviewerName, ReviewedOn, ReviewDocumentId) = (PackStatuses.Reviewed, by, partner.Trim(), reviewerName.Trim(), reviewedOn, reviewDocumentId);
        MarkChanged();
        return Result.Success();
    }

    /// <summary>
    /// The approval (§5.3, D-19), bound to <paramref name="contentHash"/>. <paramref name="route"/> is the scope the approver's function approves: the platform's
    /// lead approves ALL_TENANTS after a review; a tenant's compliance officer approves its own TENANT_ONLY version.
    /// </summary>
    public Result Approve(string by, string contentHash, string route, DateTimeOffset at)
    {
        if (route != Scope)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, Scope == PackScopes.AllTenants
                ? "A version for every tenant is approved by the platform's compliance lead."
                : "A version for one tenant only is approved by that tenant's compliance officer.");
        }

        if (Status == PackStatuses.Approved || (Scope == PackScopes.AllTenants && Status != PackStatuses.Reviewed))
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, Status == PackStatuses.Approved
                ? "The version is approved already."
                : "A version for every tenant is approved only after the partner's review is recorded.");
        }

        if (by == PreparedBy || by == ReviewedBy)
        {
            return Error.Forbidden(ErrorCodes.MakerCheckerSamePerson, "Whoever prepared the version or marked it reviewed can't approve it.");
        }

        if (!string.Equals(contentHash, ContentHash, StringComparison.Ordinal))
        {
            return Error.Conflict(PackErrorCodes.Changed, "The approval names other content than the version's.");
        }

        (Status, ApprovedBy, ApprovedAt) = (PackStatuses.Approved, by, at);
        MarkChanged();
        return Result.Success();
    }
}

/// <summary>One row of a pack version, as loaded and never changed: its table and code, class, marks with their sources (D-20) and values.</summary>
public sealed class StarterPackValue : AggregateRoot<Guid>
{
    private StarterPackValue()
    {
    }

    public Guid PackId { get; private set; }

    public string Scope { get; private set; } = "";

    public int LineNo { get; private set; }

    public string TableName { get; private set; } = "";

    public string RowCode { get; private set; } = "";

    public string Class { get; private set; } = "";

    public bool Regulated { get; private set; }

    public string RegulatedSource { get; private set; } = "";

    public bool Tailorable { get; private set; }

    public string TailorableSource { get; private set; } = "";

    /// <summary>The row's values, as JSON.</summary>
    public string Values { get; private set; } = "{}";

    /// <summary>The row's own status in the pack file, such as DRAFT-FOR-REVIEW.</summary>
    public string? RowStatus { get; private set; }

    public string? Note { get; private set; }

    public string? BasisToCheck { get; private set; }

    /// <summary>Whether AI may tailor the row (7.8): only on an explicit mark, never the interim reading (D-20).</summary>
    public bool MayBeTailored => Tailorable && TailorableSource == MarkSources.Explicit && RegulatedSource == MarkSources.Explicit && !Regulated;

    public static StarterPackValue For(Guid packId, string scope, int lineNo, string tableName, string rowCode, string @class, bool regulated, string regulatedSource,
        bool tailorable, string tailorableSource, string values, string? rowStatus, string? note, string? basisToCheck) => new()
        {
            Id = Guid.CreateVersion7(), PackId = packId, Scope = scope, LineNo = lineNo, TableName = tableName, RowCode = rowCode, Class = @class, Regulated = regulated,
            RegulatedSource = regulatedSource, Tailorable = tailorable, TailorableSource = tailorableSource, Values = values, RowStatus = rowStatus, Note = note, BasisToCheck = basisToCheck,
        };
}
