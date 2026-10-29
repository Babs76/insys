/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : General
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
using Insys.Billing.Contracts;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.ClaimsFramework.Contracts;
using Insys.Documents.Contracts;
using Insys.General.Application;
using Insys.General.Contracts;
using Insys.General.Infrastructure.Persistence;
using Insys.General.Infrastructure.StaticData;
using Insys.Rating.Contracts;
using Insys.Workflow.Contracts;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Insys.General.Infrastructure;

public static class GeneralInfrastructure
{
    public static IServiceCollection AddGeneralInfrastructure(this IServiceCollection services)
    {
        // Its approval kinds and the screens that open their records (BR-005).
        BuildingBlocks.Contracts.Approvals.ApprovalKinds.Declare(Insys.General.Contracts.GeneralApprovals.EndorsementBusinessRefType, "GENERAL_ENDORSEMENTS", Insys.General.Contracts.GeneralApprovals.EndorsementApprovalType);
        BuildingBlocks.Contracts.Approvals.ApprovalKinds.Declare(Insys.General.Contracts.GeneralApprovals.SettlementBusinessRefType, "GENERAL_CLAIMS", Insys.General.Contracts.GeneralApprovals.SettlementApprovalType);
        BuildingBlocks.Contracts.Approvals.ApprovalKinds.RaisedWith("GENERAL", Insys.General.Contracts.GeneralApprovals.EndorsementApprovalType,
            Insys.General.Contracts.GeneralApprovals.SettlementApprovalType);
        // ADR-026 §2: this module's event consumers run under its JOB function.
        BuildingBlocks.Application.Context.JobFunctions.ForConsumers(Persistence.GeneralDbContext.SchemaName, "GENERAL_EVENT_PROCESSING");
        const string schema = GeneralDbContext.SchemaName;
        services.AddModuleDbContext<GeneralDbContext>(schema, "General");
        // D-34: a legal hold on a General policy or claim itself, placed only within the caller's scope (F103).
        services.AddLegalHoldTargets(schema, new Dictionary<string, string>
        {
            ["GENERAL_POLICY"] = "SELECT product_code, org_node_code FROM general.policy WHERE id = @id",
            ["GENERAL_CLAIM"] = "SELECT product_code, org_node_code FROM general.claim WHERE id = @id",
        });
        // BR-030: the quotes, proposals (through their quote) and policies on a product version.
        services.AddProductVersionReferences(schema, """
            SELECT 'GENERAL quote ' || quote_number FROM general.quote WHERE product_code = @code AND product_version = @version
            UNION ALL
            SELECT 'GENERAL proposal on quote ' || q.quote_number FROM general.proposal p JOIN general.quote q ON q.tenant_id = p.tenant_id AND q.id = p.quote_id
            WHERE q.product_code = @code AND q.product_version = @version
            UNION ALL
            SELECT 'GENERAL policy ' || policy_number FROM general.policy WHERE product_code = @code AND product_version = @version
            ORDER BY 1
            """);
        services.AddStaticData<GeneralDbContext>(schema, GeneralStaticData.Descriptors);
        services.AddModuleHandlers<GeneralDbContext>(typeof(CreateQuote).Assembly);
        services.AddDomainEventMapper<GeneralEventMapper>(schema);
        services.AddScoped<IGeneralRepository, GeneralRepository>();
        services.AddScoped<Workflow.Contracts.IPortalCaseLinks, Application.PortalGeneralClaimCaseLinks>();
        services.AddSingleton<Contracts.IGeneralClaimRecipients, GeneralClaimRecipients>();
        services.AddSingleton<Workflow.Contracts.IApprovalScopeSource, GeneralApprovalScopes>();
        services.AddScoped<IGeneralLists, GeneralLists>();
        services.AddScoped<IAggregationAlerts, AggregationAlerts>();
        services.AddScoped<IGeneralPortalQueries, GeneralPortalQueries>();
        services.AddScoped<IGeneralCodes, GeneralCodes>();
        services.AddSingleton<BuildingBlocks.Application.IReadinessCheck, GeneralReferralReadiness>(); // onboarding readiness GENERAL_REFERRAL_AUTHORITY
        services.AddSingleton<Distribution.Contracts.IPolicyDistribution, GeneralPolicyDistribution>();
        services.AddIntegrationEventHandler<PremiumCalculated, GeneralPolicies, GeneralDbContext>(schema, "PremiumCalculated");
        services.AddIntegrationEventHandler<PremiumNotCalculated, GeneralPolicies, GeneralDbContext>(schema, "PremiumNotCalculated");
        services.AddIntegrationEventHandler<Workflow.Contracts.CaseDecided, ReopenClaimsOnUpheldCase, GeneralDbContext>(schema, "CaseDecided");
        services.AddIntegrationEventHandler<ApprovalGranted, GeneralPolicies, GeneralDbContext>(schema, "ApprovalGranted");
        services.AddIntegrationEventHandler<ApprovalRejected, GeneralPolicies, GeneralDbContext>(schema, "ApprovalRejected");
        services.AddIntegrationEventHandler<ClaimPaymentCompleted, GeneralPolicies, GeneralDbContext>(schema, "ClaimPaymentCompleted");
        services.AddIntegrationEventHandler<DocumentGenerated, GeneralPolicies, GeneralDbContext>(schema, "DocumentGenerated");
        // BR-038: a policy's invoices, documents and cessions, as they arrive.
        services.AddIntegrationEventHandler<Billing.Contracts.InvoiceIssued, PolicyEffectsRecorder, GeneralDbContext>(schema, "PolicyEffect.InvoiceIssued");
        services.AddIntegrationEventHandler<DocumentGenerated, PolicyEffectsRecorder, GeneralDbContext>(schema, "PolicyEffect.DocumentGenerated");
        services.AddIntegrationEventHandler<Reinsurance.Contracts.CessionCalculated, PolicyEffectsRecorder, GeneralDbContext>(schema, "PolicyEffect.CessionCalculated");
        services.AddIntegrationEventHandler<GeneralEndorsementApproved, GeneralPolicies, GeneralDbContext>(schema, "GeneralEndorsementApproved");
        services.AddIntegrationEventHandler<GeneralPolicyEndorsed, GeneralPolicies, GeneralDbContext>(schema, "GeneralPolicyEndorsed");
        services.AddIntegrationEventHandler<GeneralPolicyIssued, GeneralPolicies, GeneralDbContext>(schema, "GeneralPolicyIssued");
        services.AddIntegrationEventHandler<AssessorAppointed, GeneralPolicies, GeneralDbContext>(schema, "AssessorAppointed");
        services.AddIntegrationEventHandler<InvoiceSettled, GeneralPolicies, GeneralDbContext>(schema, "InvoiceSettled");
        // F006: grace for General instalments.
        services.AddIntegrationEventHandler<Billing.Contracts.ArrearsDetected, GeneralGrace, GeneralDbContext>(schema, "Grace.ArrearsDetected");
        services.AddIntegrationEventHandler<InvoiceSettled, GeneralGrace, GeneralDbContext>(schema, "Grace.InvoiceSettled");
        services.AddIntegrationEventHandler<GeneralPolicyLapsedUnpaid, DeclineClaimsOnLapse, GeneralDbContext>(schema);
        // Wave 4 §4.2: the renewal review's rule decisions and renewal quote.
        services.AddIntegrationEventHandler<Rules.Contracts.RuleDecisionRecorded, RenewalReviewSteps, GeneralDbContext>(schema, "RuleDecision");
        services.AddIntegrationEventHandler<GeneralRenewalRatingRequested, RenewalReviewSteps, GeneralDbContext>(schema, "Rating");
        services.AddOptions<GeneralJobOptions>().BindConfiguration("General:Jobs");
        services.AddOptions<AggregationOptions>().BindConfiguration("General:Aggregation");
        services.AddSingleton<GeneralJobs>();
        if (!InfrastructureRegistration.IsBuildTimeDocumentGeneration)
        {
            services.AddHostedService<GeneralJobsService>();
        }

        services.AddSingleton<BuildingBlocks.Application.Context.IPartyLinkedRecords, Persistence.GeneralPartyRecords>(); // D-34: the anonymisation check
        services.AddSingleton<GeneralPortalRelations>(); // external-user access §2: other modules' portal reads
        services.AddSingleton<BuildingBlocks.Application.Context.IPortalRecordRelations>(sp => sp.GetRequiredService<GeneralPortalRelations>());
        services.AddSingleton<BuildingBlocks.Application.Context.IBrokeredPolicies>(sp => sp.GetRequiredService<GeneralPortalRelations>());
        services.AddSingleton<BuildingBlocks.Application.Context.IBrokeredClients>(sp => sp.GetRequiredService<GeneralPortalRelations>());
        // Onboarding readiness DOMAIN_CODES: once the tenant sets GENERAL up (its numbering scheme), its own reference data is in force too.
        BuildingBlocks.Infrastructure.Readiness.ReadinessRegistration.AddSqlReadiness(services, Persistence.GeneralDbContext.SchemaName, ["DOMAIN_REFERENCE_DATA"], async (context, connection, transaction, ct) =>
        {
            var missing = new List<string>();
            if (await BuildingBlocks.Infrastructure.Readiness.ReadinessSql.Count(connection, transaction, $"SELECT count(*) FROM general.cfg_policy_numbering_schemes WHERE tenant_id = @t AND {BuildingBlocks.Infrastructure.Readiness.ReadinessSql.InForce}", ct,
                    ("t", context.TenantId), ("inForce", BuildingBlocks.Infrastructure.StaticData.StaticLifecycle.Configuration.InForce), ("day", context.Day)) > 0)
            {
            if (await BuildingBlocks.Infrastructure.Readiness.ReadinessSql.Count(connection, transaction, $"SELECT count(*) FROM general.cfg_cancellation_reasons_and_refund_rules WHERE tenant_id = @t AND {BuildingBlocks.Infrastructure.Readiness.ReadinessSql.InForce}", ct,
                    ("t", context.TenantId), ("inForce", BuildingBlocks.Infrastructure.StaticData.StaticLifecycle.Configuration.InForce), ("day", context.Day)) == 0)
            {
                missing.Add("general.cfg_cancellation_reasons_and_refund_rules");
            }

            // F006, F007 (Wave 4): a grace rule for each product and frequency on sale, and a scale for each short-period reason.
            static string InForce(string a) => $"{a}.record_status = ANY(@inForce) AND {a}.effective_from <= @day AND ({a}.effective_to IS NULL OR {a}.effective_to > @day)";
            missing.AddRange(await BuildingBlocks.Infrastructure.Readiness.ReadinessSql.Strings(connection, transaction, $"""
                SELECT DISTINCT 'general.cfg_grace_period_rules: ' || f.product_code || ' ' || f.frequency FROM general.cfg_product_billing_frequencies f
                WHERE f.tenant_id = @t AND {InForce("f")} AND NOT EXISTS (SELECT 1 FROM general.cfg_grace_period_rules g
                  WHERE g.tenant_id = @t AND {InForce("g")} AND g.product_code = f.product_code AND g.frequency = f.frequency)
                UNION
                SELECT DISTINCT 'general.cfg_short_period_scales: ' || r.reason_code FROM general.cfg_cancellation_reasons_and_refund_rules r
                WHERE r.tenant_id = @t AND {InForce("r")} AND r.refund_basis = 'SHORT_PERIOD' AND NOT EXISTS (SELECT 1 FROM general.cfg_short_period_scales b
                  WHERE b.tenant_id = @t AND {InForce("b")} AND b.scale_code = r.short_period_scale)
                UNION
                -- Wave 4 §4.2: a reviewed product needs its review rule.
                SELECT DISTINCT 'general.cfg_renewal_review_rules: ' || r.product_code FROM general.cfg_renewal_rules r
                WHERE r.tenant_id = @t AND {InForce("r")} AND r.review AND NOT EXISTS (SELECT 1 FROM general.cfg_renewal_review_rules v
                  WHERE v.tenant_id = @t AND {InForce("v")} AND v.product_code = r.product_code)
                UNION
                -- Wave 4 addition 2: a re-rated product's renewal notice is at least the tenant's notice for changed terms (31 days by default).
                SELECT DISTINCT 'general.cfg_renewal_rules: ' || r.product_code || ' notice_days ' || r.notice_days || ' < ' ||
                       coalesce((SELECT s.changed_terms_notice_days FROM general.cfg_renewal_settings s WHERE s.tenant_id = @t AND {InForce("s")}
                                 ORDER BY s.effective_from DESC LIMIT 1), 31)
                FROM general.cfg_renewal_rules r
                WHERE r.tenant_id = @t AND {InForce("r")} AND r.re_rating
                  AND r.notice_days < coalesce((SELECT s.changed_terms_notice_days FROM general.cfg_renewal_settings s WHERE s.tenant_id = @t AND {InForce("s")}
                                               ORDER BY s.effective_from DESC LIMIT 1), 31)
                ORDER BY 1
                """, ct, ("t", context.TenantId), ("inForce", BuildingBlocks.Infrastructure.StaticData.StaticLifecycle.Configuration.InForce), ("day", context.Day)));
            }

            return [BuildingBlocks.Application.ReadinessResult.Of("DOMAIN_REFERENCE_DATA", "DOMAIN_CODES",
                "Each domain the tenant sets up has its own reference data: Life claim and benefit types, General cancellation reasons, grace rules and short-period scales, Health plans", missing)];
        });
        return services;
    }
}

public sealed class GeneralJobOptions
{
    public bool Enabled { get; set; } = true;

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>
/// The daily transitions, one record per transaction, as a service identity: issued policies come into force when
/// their period starts; renewal invitations go out the rule's notice days before a period ends; policies whose rule
/// renews them by themselves are renewed when it ends (GEN-004); policies in force that were not renewed expire after
/// it; issued quotes expire after their validity; and the day's exposure snapshot is refreshed (OQ-066).
/// </summary>
public sealed class GeneralJobs(IServiceScopeFactory scopes, ITenantDirectory tenants, TenantBusinessDates businessDates, IFunctionAvailability availability, ILogger<GeneralJobs> logger)
{
    /// <summary>The JOB function this job runs under (ADR-026 §2).</summary>
    public const string Function = "GENERAL_DAILY_JOBS";

    public const string ServiceName = "general-jobs";

    public async Task<int> Run(CancellationToken cancellationToken)
    {
        var done = 0;
        foreach (var tenantId in await tenants.Running(availability, Function, logger, cancellationToken))
        {
            // The tenant's business date (GAP-044, ADR-025).
            var day = businessDates.TodayFor(tenantId);
            DueWork work;
            await using (var scope = Scope(tenantId))
            {
                var found = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(new FindDueWork(day, 500), cancellationToken);
                if (found.IsFailure)
                {
                    continue;
                }

                work = found.Value;
            }

            foreach (var id in work.PoliciesToRenew)
            {
                await using var scope = Scope(tenantId);
                var renewed = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new RenewPolicy(id), cancellationToken);
                if (renewed.IsSuccess)
                {
                    done++;
                }
                else
                {
                    logger.LogWarning("General job RenewPolicy on {Id} failed: {Code}", id, renewed.Error.Code);
                }
            }

            // Wave 4 §4.2: renewal reviews open before their invitations.
            foreach (var policyId in work.PoliciesToReview ?? [])
            {
                await using var scope = Scope(tenantId);
                var reviewed = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new ReviewRenewal(policyId, day), cancellationToken);
                if (reviewed.IsSuccess)
                {
                    done++;
                }
                else
                {
                    logger.LogWarning("General job ReviewRenewal on {Id} failed: {Code}", policyId, reviewed.Error.Code);
                }
            }

            foreach (var reminder in work.RenewalReminders ?? [])
            {
                await using var scope = Scope(tenantId);
                var reminded = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new RemindRenewal(reminder.PolicyId, reminder.DaysBefore, day), cancellationToken);
                if (reminded.IsSuccess)
                {
                    done++;
                }
                else
                {
                    logger.LogWarning("General job RemindRenewal on {Id} failed: {Code}", reminder.PolicyId, reminded.Error.Code);
                }
            }

            foreach (var (id, action) in work.PoliciesToStart.Select(id => (id, DueAction.StartPeriod))
                         .Concat(work.PoliciesToInvite.Select(id => (id, DueAction.InviteRenewal)))
                         .Concat(work.PoliciesToExpire.Select(id => (id, DueAction.ExpirePolicy)))
                         .Concat(work.QuotesToExpire.Select(id => (id, DueAction.ExpireQuote)))
                         .Concat((work.PoliciesPastGrace ?? []).Select(id => (id, DueAction.CancelForNonPayment))))
            {
                await using var scope = Scope(tenantId);
                var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new RunDue(id, action, day), cancellationToken);
                if (result.IsSuccess)
                {
                    done++;
                }
                else
                {
                    logger.LogWarning("General job {Action} on {Id} failed: {Code}", action, id, result.Error.Code);
                }
            }
        }

        return done;
    }

    /// <summary>The day's exposure snapshot, per tenant.</summary>
    public async Task<int> RefreshExposure(CancellationToken cancellationToken)
    {
        var rows = 0;
        foreach (var tenantId in await tenants.Running(availability, Function, logger, cancellationToken))
        {
            await using var scope = Scope(tenantId);
            var refreshed = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new RefreshExposureSnapshot(businessDates.TodayFor(tenantId)), cancellationToken);
            if (refreshed.IsSuccess)
            {
                rows += refreshed.Value;
            }
            else
            {
                logger.LogWarning("Exposure snapshot for tenant {TenantId} failed: {Code}", tenantId, refreshed.Error.Code);
            }
        }

        return rows;
    }

    private AsyncServiceScope Scope(Guid tenantId)
    {
        var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(tenantId, ServiceName);
        return scope;
    }
}

public sealed class GeneralJobsService(GeneralJobs jobs, IOptions<GeneralJobOptions> options, ILogger<GeneralJobsService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try
            {
                await jobs.Run(stoppingToken);
                await jobs.RefreshExposure(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("General jobs failed: {ExceptionType}", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
