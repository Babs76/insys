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
using Insys.BuildingBlocks.Api.Functions;

namespace Insys.ProductFactory.Api;

/// <summary>
/// The ProductFactory module's functions (ADR-026): each screen with the read operations it covers, each action with its
/// operation, by endpoint name, in the sub-domains of docs/specs/INSYS_NAVIGATION.md. Portal screens are declared once
/// per portal. The function catalogue is generated from these.
/// </summary>
internal static class ProductFactoryFunctions
{
    public static readonly IReadOnlyList<FunctionDeclaration> All =
    [
        .. new FunctionMap([Audiences.Staff], null)
        .Screen("PRODUCTS", "PRODUCT_FACTORY", "Definitions & versions", "product-factory/products", "ProductListProducts", "ProductListVersions", "ProductGetVersion", "ProductListSimulations")
        .Action("PRODUCT_REGISTER_PRODUCT", "PRODUCTS", "ProductRegisterProduct")
        .Action("PRODUCT_CREATE_DRAFT_VERSION", "PRODUCTS", "ProductCreateDraftVersion")
        .Action("PRODUCT_EDIT_CONFIGURATION", "PRODUCTS", "ProductEditConfiguration")
        .Action("PRODUCT_VALIDATE_VERSION", "PRODUCTS", "ProductValidateVersion")
        .Action("PRODUCT_RUN_SIMULATION", "PRODUCTS", "ProductRunSimulation")
        .Action("PRODUCT_SUBMIT_FOR_APPROVAL", "PRODUCTS", "ProductSubmitForApproval")
        .Action("PRODUCT_WITHDRAW_VERSION", "PRODUCTS", "ProductWithdrawVersion")
        .Action("PRODUCT_RETIRE_VERSION", "PRODUCTS", "ProductRetireVersion")
            .Functions,
    ];
}
