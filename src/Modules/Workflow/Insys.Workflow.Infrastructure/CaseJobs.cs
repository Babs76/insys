/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Workflow
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
using Insys.BuildingBlocks.Application;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Infrastructure.Readiness;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Workflow.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Insys.Workflow.Infrastructure;

/// <summary>
/// The CASE_TIMELINES job (ADR-026 JOB function): tells complainants of steps past their dates (CaseUpdated, OVERDUE)
/// and disposes of closed cases past their retention unless under a legal hold (OQ-106). Per tenant that runs the
/// function, one case per transaction, as a service identity.
/// </summary>
public sealed class CaseTimelineJobs(IServiceScopeFactory scopes, ITenantDirectory tenants, IFunctionAvailability availability, IClock clock, ILogger<CaseTimelineJobs> logger)
{
    public const string Function = CaseFunctions.Timelines;

    public const string ServiceName = "case-timelines";

    public async Task<(int Overdue, int Disposed)> Run(CancellationToken cancellationToken)
    {
        var (overdue, disposed) = (0, 0);
        foreach (var tenantId in await tenants.Running(availability, Function, logger, cancellationToken))
        {
            var (o, d) = await RunForTenant(tenantId, cancellationToken);
            overdue += o;
            disposed += d;
        }

        return (overdue, disposed);
    }

    public async Task<(int Overdue, int Disposed)> RunForTenant(Guid tenantId, CancellationToken cancellationToken)
    {
        var (overdue, disposed) = (0, 0);
        CasesDueView due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(tenantId, ServiceName);
            var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(new CasesDue(clock.UtcNow), cancellationToken);
            if (result.IsFailure)
            {
                return (0, 0);
            }

            due = result.Value;
        }

        foreach (var id in due.Overdue)
        {
            overdue += await Send(tenantId, new MarkCaseOverdue(id), cancellationToken) ? 1 : 0;
        }

        foreach (var id in due.ForDisposal)
        {
            disposed += await Send(tenantId, new DisposeCase(id), cancellationToken) ? 1 : 0;
        }

        return (overdue, disposed);
    }

    private async Task<bool> Send(Guid tenantId, ICommand<bool> command, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(tenantId, ServiceName);
        var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(command, cancellationToken);
        if (result.IsFailure)
        {
            logger.LogWarning("{Command} failed: {Code}", command.GetType().Name, result.Error.Code);
        }

        return result is { IsSuccess: true, Value: true };
    }
}

public sealed class CaseTimelineJobsService(CaseTimelineJobs jobs, IOptions<Persistence.WorkflowOptions> options, ILogger<CaseTimelineJobsService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.TimersEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.Value.TimerInterval);
        do
        {
            try
            {
                await jobs.Run(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Case timeline jobs failed: {ExceptionType}", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>
/// OQ-110 (§10): the case configuration onboarding needs, in SHARED_OPERATIONAL_SETUP: a complaint case type with a review
/// type, its ACKNOWLEDGE and RESOLVE timelines, the business calendars those timelines name, and the ombud schemes it names.
/// </summary>
internal static class CaseReadiness
{
    public const string Step = "SHARED_OPERATIONAL_SETUP";

    public static async Task<IReadOnlyList<ReadinessResult>> Check(ReadinessContext context, NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var configuration = StaticLifecycle.Configuration.InForce;
        var reference = StaticLifecycle.Reference.InForce;
        var types = await ReadinessSql.Strings(connection, transaction,
            $"SELECT case_type_code FROM workflow.cfg_case_types WHERE tenant_id = @t AND family = 'COMPLAINT' AND review_case_type_code IS NOT NULL AND {ReadinessSql.InForce} ORDER BY case_type_code",
            cancellationToken, ("t", context.TenantId), ("inForce", configuration), ("day", context.Day));

        var timelines = new List<string>();
        var calendars = new List<string>();
        var schemes = new List<string>();
        foreach (var type in types)
        {
            var steps = await ReadinessSql.Strings(connection, transaction,
                $"SELECT step FROM workflow.cfg_case_timelines WHERE tenant_id = @t AND case_type_code = @type AND {ReadinessSql.InForce}",
                cancellationToken, ("t", context.TenantId), ("type", type), ("inForce", configuration), ("day", context.Day));
            timelines.AddRange(new[] { "ACKNOWLEDGE", "RESOLVE" }.Except(steps).Select(s => $"{type}:{s}"));

            var named = await ReadinessSql.Strings(connection, transaction,
                $"SELECT DISTINCT calendar_code FROM workflow.cfg_case_timelines WHERE tenant_id = @t AND case_type_code = @type AND basis = 'BUSINESS' AND calendar_code IS NOT NULL AND {ReadinessSql.InForce}",
                cancellationToken, ("t", context.TenantId), ("type", type), ("inForce", configuration), ("day", context.Day));
            foreach (var calendar in named)
            {
                if (await ReadinessSql.Count(connection, transaction,
                        $"SELECT count(*) FROM workflow.ref_business_calendars WHERE (tenant_id = @t OR tenant_id IS NULL) AND calendar_code = @c AND {ReadinessSql.InForce}",
                        cancellationToken, ("t", context.TenantId), ("c", calendar), ("inForce", reference), ("day", context.Day)) == 0)
                {
                    calendars.Add(calendar);
                }
            }

            var ombud = await ReadinessSql.Strings(connection, transaction,
                $"SELECT DISTINCT unnest(ombud_scheme_codes) FROM workflow.cfg_case_types WHERE tenant_id = @t AND case_type_code = @type AND {ReadinessSql.InForce}",
                cancellationToken, ("t", context.TenantId), ("type", type), ("inForce", configuration), ("day", context.Day));
            foreach (var scheme in ombud)
            {
                if (await ReadinessSql.Count(connection, transaction,
                        $"SELECT count(*) FROM workflow.ref_ombud_schemes WHERE (tenant_id = @t OR tenant_id IS NULL) AND scheme_code = @s AND {ReadinessSql.InForce}",
                        cancellationToken, ("t", context.TenantId), ("s", scheme), ("inForce", reference), ("day", context.Day)) == 0)
                {
                    schemes.Add(scheme);
                }
            }
        }

        return
        [
            ReadinessResult.Of(Step, "COMPLAINT_CASE_TYPE", "A complaint case type that names its review type (PPR 18.6)", types.Count > 0 ? [] : ["workflow.cfg_case_types"]),
            ReadinessResult.Of(Step, "COMPLAINT_TIMELINES", "ACKNOWLEDGE and RESOLVE timelines for each complaint case type",
                types.Count > 0 ? timelines.Distinct().ToList() : ["workflow.cfg_case_timelines"]),
            ReadinessResult.Of(Step, "CASE_TIMELINE_CALENDARS", "The business calendars the complaint timelines name", calendars.Distinct().ToList()),
            ReadinessResult.Of(Step, "CASE_OMBUD_SCHEMES", "The ombud schemes the complaint case types name", schemes.Distinct().ToList()),
        ];
    }
}
