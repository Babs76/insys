/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Api.Host
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
using Insys.Billing.Infrastructure;
using Insys.BuildingBlocks.Api;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Infrastructure.Messaging;
using Insys.General.Infrastructure;
using Insys.Health.Infrastructure;
using Insys.Life.Infrastructure;
using Insys.ProductFactory.Infrastructure;

namespace Insys.Api.Platform;

public sealed class DailyJobRunnerOptions
{
    /// <summary>Test-only (ADR-025). Allowed in Development and Testing; the host refuses to start with it anywhere else.</summary>
    public bool Enabled { get; set; }
}

public sealed record DailyJobsRequest(DateOnly AsOf);

public sealed record DailyJobsRun(DateOnly AsOf, int ProductVersions, int Invoiced, int Aged, int General, int Life, int Health, int EventsDelivered, int ExposureRows = 0);

/// <summary>
/// Runs the daily jobs for one tenant as if its business date were <c>asOf</c> (ADR-025, front-end request 40), so that
/// time-driven processes such as a missed premium leading to a lapse can be tested. Only the caller's tenant is touched.
/// Events the jobs raise are delivered before and after the domain jobs, so their effects follow in the same run.
/// </summary>
public sealed class DailyJobRunner(IServiceProvider services, OutboxProcessor relay)
{
    public async Task<DailyJobsRun> Run(Guid tenantId, DateOnly asOf, CancellationToken cancellationToken)
    {
        var tenant = new OneTenant(tenantId);
        using var _ = BusinessDateOverride.Use(asOf);
        var events = 0;
        var products = await Create<ProductJobs>(tenant).Run(cancellationToken);
        var (invoiced, aged) = await Create<BillingJobs>(tenant).Run(cancellationToken);
        events += await Drain(cancellationToken);
        var general = await Create<GeneralJobs>(tenant).Run(cancellationToken);
        var exposure = await Create<GeneralJobs>(tenant).RefreshExposure(cancellationToken); // with the F062 alert
        var life = await Create<LifeJobs>(tenant).Run(cancellationToken);
        var health = await Create<HealthJobs>(tenant).Run(cancellationToken);
        events += await Drain(cancellationToken);
        return new DailyJobsRun(asOf, products, invoiced, aged, general, life, health, events, exposure);
    }

    private T Create<T>(ITenantDirectory tenant) => ActivatorUtilities.CreateInstance<T>(services, tenant);

    private async Task<int> Drain(CancellationToken cancellationToken)
    {
        var total = 0;
        for (var pass = 0; pass < 20; pass++)
        {
            var published = await relay.ProcessOnce(cancellationToken);
            total += published;
            if (published == 0)
            {
                break;
            }
        }

        return total;
    }

    private sealed class OneTenant(Guid tenantId) : ITenantDirectory
    {
        public Task<IReadOnlyList<Guid>> GetActiveTenantIds(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Guid>>([tenantId]);
    }
}

/// <summary>Refuses to start a host that enables the test-only daily job runner outside Development and Testing (ADR-025).</summary>
public sealed class DailyJobRunnerGuard(IConfiguration configuration, IHostEnvironment environment) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (InsysAuthenticationOptions.AllowsDevelopmentKey(environment))
        {
            return Task.CompletedTask;
        }

        var allowed = string.Join(" and ", InsysAuthenticationOptions.DevelopmentKeyEnvironments);
        if (configuration.GetValue<bool>("Testing:DailyJobRunner:Enabled"))
        {
            throw new InvalidOperationException(
                $"Testing:DailyJobRunner:Enabled is set in the {environment.EnvironmentName} environment. The test-only daily job runner is allowed only in "
                + $"{allowed}; remove the setting.");
        }

        // The test-only fixed business date (Wave 1 batch 4) would make every tenant's today wrong in production.
        return string.IsNullOrEmpty(configuration["BusinessDate:TestDate"])
            ? Task.CompletedTask
            : throw new InvalidOperationException(
                $"BusinessDate:TestDate is set in the {environment.EnvironmentName} environment. It is allowed only in {allowed}; remove the setting.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class DailyJobRunnerEndpoints
{
    public static void AddDailyJobRunner(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHostedService<DailyJobRunnerGuard>();
        services.AddOptions<DailyJobRunnerOptions>().Bind(configuration.GetSection("Testing:DailyJobRunner"));
        services.AddSingleton<DailyJobRunner>();
    }

    /// <summary>Maps POST /api/v1/testing/daily-jobs only when the runner is enabled (and so only in Development or Testing).</summary>
    public static void MapDailyJobRunner(this IEndpointRouteBuilder v1, IConfiguration configuration, IHostEnvironment environment)
    {
        if (!configuration.GetValue<bool>("Testing:DailyJobRunner:Enabled") || !InsysAuthenticationOptions.AllowsDevelopmentKey(environment))
        {
            return;
        }

        v1.MapPost("/testing/daily-jobs", async (DailyJobsRequest request, DailyJobRunner runner, ITenantContext tenant, CancellationToken ct) =>
                Results.Ok(await runner.Run(tenant.RequireTenantId(), request.AsOf, ct)))
            .WithTags("Testing")
            .WithName("TestingRunDailyJobs")
            .WithSummary("Test only: run the daily jobs for the caller's tenant as of a business date (ADR-025)")
            .WithDescription("Development and Testing only. Runs Product Factory, Billing, General, Life and Health daily jobs with asOf as the tenant's "
                + "business date, delivering the events they raise. Not in production documents.")
            .Produces<DailyJobsRun>(StatusCodes.Status200OK)
            .ExcludeFromDescription()
            // Any user of the tenant: a test-only route with no function in the catalogue (ADR-026).
            .RequireSignedInUser();
    }
}
