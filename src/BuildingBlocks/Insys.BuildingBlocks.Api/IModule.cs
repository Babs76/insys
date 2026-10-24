/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Api
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
using Insys.BuildingBlocks.Api.StaticData;
using Insys.BuildingBlocks.Application.Context;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Insys.BuildingBlocks.Api;

/// <summary>A bounded-context module as the host sees it. Each module's Api project provides one.</summary>
public interface IModule
{
    /// <summary>The module's name, also its OpenAPI tag.</summary>
    string Name { get; }

    void Register(IServiceCollection services, IConfiguration configuration);

    /// <summary>Maps the module's endpoints onto the versioned /api/v1 group.</summary>
    void MapEndpoints(IEndpointRouteBuilder api);

    /// <summary>
    /// Whether the module holds business data (ADR-024). A business module refuses the operator tenant: its endpoints answer
    /// 403 OPERATOR_TENANT_NOT_BUSINESS there, except its static data and platform endpoints. Tenancy, Identity, Static
    /// data, Audit, Integration and Workflow administer the platform and say false.
    /// </summary>
    bool HoldsBusinessData => true;
}

/// <summary>Marks an endpoint of a business module, which refuses the operator tenant (ADR-024).</summary>
public sealed record OperatorTenantRefusedMetadata;

public static class RequestAccessScope
{
    /// <summary>
    /// F103 (addenda/access-scopes.md §4): the record scope the handlers apply is the caller's scope for the endpoint's
    /// required permission; a GET or HEAD only reads, so a record outside it is not found rather than refused.
    /// </summary>
    public static RouteGroupBuilder ApplyRequestScope(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (http.GetEndpoint()?.Metadata.GetMetadata<RequiredPermissionMetadata>()?.Permission is { } permission)
            {
                http.RequestServices.GetRequiredService<ExecutionScope>().UseRequest(permission, HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method));
            }

            return await next(context);
        });
        return group;
    }
}

public static class OperatorTenantRefusal
{
    /// <summary>
    /// ADR-024: the group's endpoints refuse the operator tenant with 403 OPERATOR_TENANT_NOT_BUSINESS, reads as well as
    /// changes, before the handler runs. Static data and platform endpoints are left open: global configuration is
    /// maintained from the operator tenant (ADR-003).
    /// </summary>
    public static RouteGroupBuilder RefuseOperatorTenant(this RouteGroupBuilder group)
    {
        group.WithMetadata(new OperatorTenantRefusedMetadata());
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var metadata = http.GetEndpoint()?.Metadata;

            if (metadata?.GetMetadata<StaticDataEndpointMetadata>() is null && metadata?.GetMetadata<PlatformOperatorEndpointMetadata>() is null)
            {
                var operatorTenant = http.RequestServices.GetRequiredService<IOptions<PlatformOptions>>().Value.OperatorTenantId;
                if (operatorTenant != Guid.Empty && http.RequestServices.GetRequiredService<ITenantContext>().TenantId == operatorTenant)
                {
                    return Problems.For(Domain.ErrorCodes.OperatorTenantNotBusiness,
                        "The platform operator tenant holds no business data; sign in to a business tenant (ADR-024).", StatusCodes.Status403Forbidden);
                }
            }

            return await next(context);
        });
        return group;
    }
}
