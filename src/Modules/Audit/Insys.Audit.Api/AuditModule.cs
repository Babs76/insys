/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Audit
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
using Insys.BuildingBlocks.Api;
using Insys.Audit.Api.StaticData;
using Insys.Audit.Infrastructure;
using Insys.Audit.Application;
using Insys.Audit.Contracts;
using Insys.BuildingBlocks.Application.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Audit.Api;

/// <summary>The Audit module. So far it serves its static data only; its aggregates are built in a later phase.</summary>
public sealed class AuditModule : IModule
{
    public string Name => "Audit";

    public IReadOnlyList<BuildingBlocks.Api.Functions.FunctionDeclaration> Functions => AuditFunctions.All;

    /// <summary>A platform module: open to the operator tenant (ADR-024).</summary>
    public bool HoldsBusinessData => false;

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddAuditInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        AuditStaticDataEndpoints.MapStaticData(api);
        api.MapGroup("/audit/events").WithTags(Name).MapGet("/", async (string? targetType, string? targetId, string? actorId, string? cursor, int? limit,
                IDispatcher dispatcher, CancellationToken ct) =>
            {
                if (!Cursor.TryDecode(cursor, out var afterId))
                {
                    return Problems.For(BuildingBlocks.Domain.ErrorCodes.ValidationFailed, "The cursor is not valid.", StatusCodes.Status422UnprocessableEntity);
                }

                var pageSize = Cursor.ClampLimit(limit);
                var result = await dispatcher.Query(new ListAuditEvents(targetType, targetId, actorId, afterId, pageSize), ct);
                return result.ToHttp(items => Results.Ok(new CursorPage<AuditEventView>(items, items.Count == pageSize ? Cursor.Encode(items[^1].Id) : null)));
            })
            .Produces<CursorPage<AuditEventView>>(StatusCodes.Status200OK)
            .WithName("ListAuditEvents")
            .WithSummary("List audit events")
            .WithDescription("The tenant's audit trail of sensitive operations and denied requests, newest first (IAM-006, TEN-005).")
            .Produces<CursorPage<AuditEventView>>()
            .RequirePermission(AuditPermissions.Read);

        api.MapGroup("/audit/records").WithTags(Name).MapGet("/{targetType}/{targetId}", async (string targetType, string targetId, string? cursor, int? limit,
                IDispatcher dispatcher, CancellationToken ct) =>
            {
                if (!Cursor.TryDecode(cursor, out var afterId))
                {
                    return Problems.For(BuildingBlocks.Domain.ErrorCodes.ValidationFailed, "The cursor is not valid.", StatusCodes.Status422UnprocessableEntity);
                }

                var pageSize = Cursor.ClampLimit(limit);
                var result = await dispatcher.Query(new ListAuditEvents(targetType, targetId, null, afterId, pageSize), ct);
                return result.ToHttp(items => Results.Ok(new CursorPage<AuditEventView>(items, items.Count == pageSize ? Cursor.Encode(items[^1].Id) : null)));
            })
            .Produces<CursorPage<AuditEventView>>(StatusCodes.Status200OK)
            .WithName("GetRecordHistory")
            .WithSummary("Get a record's history")
            .WithDescription("Every change to one record, newest first, for the record action bar. targetType is the record's aggregate type as the "
                + "API names it (UserAccount, Tenant, Party, LifePolicy ...) and targetId its ID. Each command or event policy that adds or changes the "
                + "record leaves an entry: its own audit entry (IAM-006), or a RECORD_CHANGED entry naming the command. Failed attempts at audited "
                + "operations are included with their outcome.")
            .RequirePermission(AuditPermissions.Read);
    }
}
