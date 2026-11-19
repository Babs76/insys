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
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.ProductFactory.Application;
using Insys.ProductFactory.Contracts;
using Insys.ProductFactory.Domain.Products;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.ProductFactory.Infrastructure;

/// <summary>AI-1's Draft version through Product Factory's own commands, for the user the scope acts for (AIA-017).</summary>
internal sealed class ProductDrafts(IServiceProvider services, ExecutionScope execution) : IProductDrafts
{
    // The endpoints' functions (ProductFactoryFunctions), so the job is refused exactly when the user would be.
    private const string ReadFunction = "PRODUCTS";
    private const string CreateFunction = "PRODUCT_CREATE_DRAFT_VERSION";
    private const string EditFunction = "PRODUCT_EDIT_CONFIGURATION";

    public async Task<ProductDraftTarget?> Describe(string productCode, CancellationToken cancellationToken)
    {
        if (ActingUser.Check(execution, ProductPermissions.ProductRead, [ReadFunction], isRead: true) is not null)
        {
            return null;
        }

        var dispatcher = services.GetRequiredService<IDispatcher>();
        var versions = await dispatcher.Query(new ListProductVersions(productCode), cancellationToken);
        if (versions.IsFailure || versions.Value.Count == 0)
        {
            return null;
        }

        var latest = versions.Value[0];
        var domain = Enum.Parse<ProductDomain>(latest.Domain, ignoreCase: true);
        var codes = await dispatcher.Query(new ListCoverageCodes(domain), cancellationToken);
        if (codes.IsFailure)
        {
            return null;
        }

        return new ProductDraftTarget(latest.ProductCode, latest.Domain, latest.Version, latest.Name, latest.Coverages, codes.Value, latest.CoverStartsOn,
            latest.ReinstatementArrears);
    }

    public async Task<ProductDraftOutcome> Create(string productCode, DateOnly effectiveFrom, ProductDraftFields fields, CancellationToken cancellationToken)
    {
        if (ActingUser.Check(execution, ProductPermissions.ProductCreate, [CreateFunction]) is { } refused)
        {
            return new ProductDraftOutcome(null, refused.Code, refused.Message);
        }

        var dispatcher = services.GetRequiredService<IDispatcher>();
        var created = await dispatcher.Send(new CreateDraftVersion(productCode, effectiveFrom), cancellationToken);
        if (created.IsFailure)
        {
            return new ProductDraftOutcome(null, created.Error.Code, created.Error.Message);
        }

        var draft = created.Value;
        if (fields is { Name: null, Coverages: null, CoverStartsOn: null, ReinstatementArrears: null })
        {
            return new ProductDraftOutcome(draft.Version);
        }

        if (ActingUser.Check(execution, ProductPermissions.ProductUpdate, [EditFunction]) is { } refusedEdit)
        {
            return new ProductDraftOutcome(draft.Version, refusedEdit.Code, refusedEdit.Message);
        }

        var edited = await dispatcher.Send(new EditConfiguration(productCode, draft.Version, fields.Name ?? draft.Name, fields.Coverages ?? draft.Coverages, draft.EffectiveFrom,
            draft.RowVersion, Parse<CoverStart>(fields.CoverStartsOn), ReinstatementArrears: Parse<ReinstatementArrears>(fields.ReinstatementArrears)), cancellationToken);
        return edited.IsFailure ? new ProductDraftOutcome(draft.Version, edited.Error.Code, edited.Error.Message) : new ProductDraftOutcome(draft.Version);
    }

    public async Task<ProductDraftVersion?> Version(string productCode, int version, CancellationToken cancellationToken)
    {
        if (ActingUser.Check(execution, ProductPermissions.ProductRead, [ReadFunction], isRead: true) is not null)
        {
            return null;
        }

        var read = await services.GetRequiredService<IDispatcher>().Query(new GetProductVersion(productCode, version), cancellationToken);
        return read.IsFailure ? null : new ProductDraftVersion(read.Value.ProductCode, read.Value.Version, read.Value.Status.ToString(), read.Value.Name,
            read.Value.Coverages, read.Value.EffectiveFrom, read.Value.CoverStartsOn, read.Value.ReinstatementArrears, read.Value.RowVersion);
    }

    public async Task<ProductDraftOutcome> Edit(string productCode, int version, ProductDraftFields fields, DateOnly? effectiveFrom, CancellationToken cancellationToken)
    {
        if (ActingUser.Check(execution, ProductPermissions.ProductUpdate, [EditFunction]) is { } refused)
        {
            return new ProductDraftOutcome(version, refused.Code, refused.Message);
        }

        var dispatcher = services.GetRequiredService<IDispatcher>();
        var current = await dispatcher.Query(new GetProductVersion(productCode, version), cancellationToken);
        if (current.IsFailure)
        {
            return new ProductDraftOutcome(version, current.Error.Code, current.Error.Message);
        }

        var v = current.Value;
        var edited = await dispatcher.Send(new EditConfiguration(productCode, version, fields.Name ?? v.Name, fields.Coverages ?? v.Coverages, effectiveFrom ?? v.EffectiveFrom,
            v.RowVersion, Parse<CoverStart>(fields.CoverStartsOn ?? v.CoverStartsOn), ReinstatementArrears: Parse<ReinstatementArrears>(fields.ReinstatementArrears ?? v.ReinstatementArrears)),
            cancellationToken);
        return edited.IsFailure ? new ProductDraftOutcome(version, edited.Error.Code, edited.Error.Message) : new ProductDraftOutcome(version);
    }

    private static T? Parse<T>(string? value) where T : struct, Enum => value is null ? null : Enum.Parse<T>(value, ignoreCase: true);
}
