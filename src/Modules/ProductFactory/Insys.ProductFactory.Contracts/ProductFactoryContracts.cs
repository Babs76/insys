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
