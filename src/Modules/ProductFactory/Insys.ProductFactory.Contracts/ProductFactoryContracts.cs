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
using Insys.BuildingBlocks.Contracts;

namespace Insys.ProductFactory.Contracts;

public static class ProductPermissions
{
    public const string ProductRead = "product.read";
    public const string ProductCreate = "product.create";
    public const string ProductUpdate = "product.update";
    public const string ProductPublish = "product.publish";
}

/// <summary>The approval type requested from Workflow when a product version is submitted (PRD-006).</summary>
public static class ProductApprovals
{
    public const string BusinessRefType = "PRODUCT_VERSION";
    public const string ApprovalType = "PRODUCT_PUBLICATION";
}

[IntegrationEvent("product.ProductRegistered")]
public sealed record ProductRegistered(Guid ProductId, string ProductCode, string Domain, string FamilyCode) : IIntegrationEvent;

[IntegrationEvent("product.ProductVersionValidated")]
public sealed record ProductVersionValidated(Guid VersionId, string ProductCode, int Version) : IIntegrationEvent;

[IntegrationEvent("product.ProductVersionApproved")]
public sealed record ProductVersionApproved(Guid VersionId, string ProductCode, int Version, Guid CheckerId) : IIntegrationEvent;

/// <summary>A product version was published: it is in force from its effective date. Rating loads its configuration.</summary>
[IntegrationEvent("product.ProductVersionPublished")]
public sealed record ProductVersionPublished(Guid VersionId, string ProductCode, int Version, string Domain, DateOnly EffectiveFrom) : IIntegrationEvent;

[IntegrationEvent("product.ProductVersionWithdrawn")]
public sealed record ProductVersionWithdrawn(Guid VersionId, string ProductCode, int Version, DateOnly EffectiveTo) : IIntegrationEvent;

/// <summary>The product AI-1 drafts a version of (AIA-012): its latest version's own fields, and the coverage codes its domain may use.</summary>
/// <param name="CoverStartsOn">Issue or FirstPayment.</param>
/// <param name="ReinstatementArrears">None or Required.</param>
public sealed record ProductDraftTarget(string ProductCode, string Domain, int LatestVersion, string Name, IReadOnlyList<string> Coverages,
    IReadOnlyList<string> CoverageCodes, string CoverStartsOn, string ReinstatementArrears);

/// <summary>The version's own fields AI-1 proposes (OQ-184 (a)); a null field keeps the copied version's value.</summary>
public sealed record ProductDraftFields(string? Name, IReadOnlyList<string>? Coverages, string? CoverStartsOn, string? ReinstatementArrears);

/// <summary>The outcome of a step: the Draft's version number, or the refusal's code and message as the endpoint would give them.</summary>
public sealed record ProductDraftOutcome(int? Version, string? ErrorCode = null, string? Message = null)
{
    public bool Succeeded => ErrorCode is null;
}

/// <summary>
/// AI-1's Draft version (AIA-017), made through the same commands as <c>POST …/versions</c> and <c>PUT …/versions/{n}</c>, under the identity of the
/// scope's user, so their validation, audit and maker-checker apply. Each step is checked as its endpoint is: the function, the permission and the
/// product's scope (F103). Called only from a scope acting for a user (<c>ActingUserScopes</c>); a service scope is refused.
/// </summary>
public interface IProductDrafts
{
    /// <summary>The product as the user may read it, or null when it isn't registered or is outside their scope.</summary>
    Task<ProductDraftTarget?> Describe(string productCode, CancellationToken cancellationToken);

    /// <summary>Creates the next version as a draft copy of the latest, then edits its own fields, each in its own transaction.</summary>
    Task<ProductDraftOutcome> Create(string productCode, DateOnly effectiveFrom, ProductDraftFields fields, CancellationToken cancellationToken);
}
