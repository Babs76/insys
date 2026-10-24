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

    /// <summary>
    /// Whether the module's commands are channel-scoped (F005, addenda/channel-permissions.md §2): they create or change a
    /// customer's business. General, Life, Health and the Claims Framework say true; static data and platform endpoints
    /// are never channel-scoped.
    /// </summary>
    bool ChannelScoped => false;
}

/// <summary>Marks a channel-scoped command (F005): a group of a channel-scoped module, or one endpoint (payments out).</summary>
public sealed record ChannelScopedMetadata;

/// <summary>Marks a command that always goes to maker-checker approval, which satisfies a channel's requires_approval (F005 §3).</summary>
public sealed record ChannelApprovalFlowMetadata;

/// <summary>Marks an endpoint of a portal (external-user access §5): external users of the kinds named may use it, and no other.</summary>
public sealed record PortalEndpointMetadata(IReadOnlyList<string> Kinds);

/// <summary>The channel-scoped transactions, read once from the endpoints: the permission of each channel-scoped command (F005 §2).</summary>
internal sealed class EndpointChannelScopedTransactions(EndpointDataSource endpoints) : IChannelScopedTransactions
{
    private readonly Lazy<IReadOnlySet<string>> _all = new(() => endpoints.Endpoints
        .Where(e => e.Metadata.GetMetadata<ChannelScopedMetadata>() is not null && e.Metadata.GetMetadata<StaticDataEndpointMetadata>() is null
                    && e.Metadata.GetMetadata<PlatformOperatorEndpointMetadata>() is null && e.Metadata.GetMetadata<PortalEndpointMetadata>() is null
                    && e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods.Any(m => m is not ("GET" or "HEAD")) == true)
        .Select(e => e.Metadata.GetMetadata<RequiredPermissionMetadata>()?.Permission)
        .OfType<string>()
        .ToHashSet(StringComparer.Ordinal));

    public IReadOnlySet<string> All => _all.Value;
}

public static class ChannelScoping
{
    public static TBuilder ChannelScoped<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.WithMetadata(new ChannelScopedMetadata());

    public static TBuilder HasApprovalFlow<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.WithMetadata(new ChannelApprovalFlowMetadata());

    /// <summary>The endpoint is for the external users of these kinds (ExternalSubject.Party, .Intermediary, .Provider).</summary>
    public static TBuilder ForPortal<TBuilder>(this TBuilder builder, params string[] kinds)
        where TBuilder : IEndpointConventionBuilder => builder.WithMetadata(new PortalEndpointMetadata(kinds));
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
            var metadata = http.GetEndpoint()?.Metadata;
            if (await ExternalUserRefusal(http, metadata) is { } refused)
            {
                return refused;
            }

            if (metadata?.GetMetadata<RequiredPermissionMetadata>()?.Permission is { } permission)
            {
                var scope = http.RequestServices.GetRequiredService<ExecutionScope>();
                var isRead = HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method);
                scope.UseRequest(permission, isRead);

                // F005 §3: a channel-scoped command is checked against the channel of the token's client, after the permission.
                if (!isRead && metadata.GetMetadata<ChannelScopedMetadata>() is not null && metadata.GetMetadata<StaticDataEndpointMetadata>() is null
                    && metadata.GetMetadata<PlatformOperatorEndpointMetadata>() is null && scope.ActorType == ActorType.User && scope.TenantId is { } tenantId
                    && http.RequestServices.GetService<IChannelPolicy>() is { } policy)
                {
                    var day = http.RequestServices.GetRequiredService<IBusinessDate>().Today;
                    var decision = await policy.Decide(tenantId, scope.ClientId, permission, scope.RoleCodes, day, http.RequestAborted);
                    if (decision.RefusalCode is { } code)
                    {
                        return Problems.For(code, decision.RefusalMessage ?? "The channel does not allow this transaction.", StatusCodes.Status403Forbidden);
                    }

                    if (decision.IsScoped)
                    {
                        var gate = new ChannelGate(decision, metadata.GetMetadata<ChannelApprovalFlowMetadata>() is not null);
                        if (gate.RefusalForEveryProduct is { } refusal)
                        {
                            return Problems.For(refusal.Code, refusal.Message, StatusCodes.Status403Forbidden);
                        }

                        scope.UseChannel(gate);
                    }
                }
            }

            return await next(context);
        });
        return group;
    }

    /// <summary>
    /// External-user access §5 and §6, for an external user only: the endpoint is one of their portal's; the request comes
    /// through the portal channel of their audience; and, where the portal requires it, the token shows MFA.
    /// </summary>
    private static async Task<IResult?> ExternalUserRefusal(HttpContext http, EndpointMetadataCollection? metadata)
    {
        var scope = http.RequestServices.GetRequiredService<ExecutionScope>();
        if (scope.External is not { } external || scope.TenantId is not { } tenantId)
        {
            return null;
        }

        if (metadata?.GetMetadata<PortalEndpointMetadata>() is not { } portal || !portal.Kinds.Contains(external.Kind))
        {
            return Problems.For(Domain.ErrorCodes.ExternalUserNotAllowed, "This is not an endpoint of your portal.", StatusCodes.Status403Forbidden);
        }

        var day = http.RequestServices.GetRequiredService<IBusinessDate>().Today;
        if (http.RequestServices.GetService<IChannelPolicy>() is not { } channels
            || await channels.ChannelOf(tenantId, scope.ClientId, day, http.RequestAborted) is not { } channel || channel.Audience != external.ChannelAudience)
        {
            return Problems.For(Domain.ErrorCodes.ChannelNotEnabled, $"Portal users act only through the {external.ChannelAudience.ToLowerInvariant()} portal.",
                StatusCodes.Status403Forbidden);
        }

        var mfa = external.AlwaysNeedsMfa
            || (http.RequestServices.GetService<IPortalAuthentication>() is { } policies && await policies.MfaRequired(tenantId, scope.ClientId, day, http.RequestAborted));
        return mfa && !scope.MultiFactor
            ? Problems.For(Domain.ErrorCodes.PortalMfaRequired, "This portal requires multi-factor authentication; sign in again with your second factor.",
                StatusCodes.Status403Forbidden)
            : null;
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
