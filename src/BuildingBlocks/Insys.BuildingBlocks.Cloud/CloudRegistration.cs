/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Cloud
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
using Insys.BuildingBlocks.Application.Security;
using Insys.BuildingBlocks.Application.Storage;
using Insys.BuildingBlocks.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Insys.BuildingBlocks.Cloud;

/// <summary>
/// Registers the cloud adapters the configuration selects (ADR-027): a key service when envelope encryption uses one, and an object store when documents are kept in one. Nothing is registered in
/// development and CI, which use the local providers. Only this project references cloud SDKs.
/// </summary>
public static class CloudRegistration
{
    public static IServiceCollection AddInsysCloud(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DewKmsOptions>().Configure(o =>
        {
            configuration.GetSection("Huawei:Kms").Bind(o);
            configuration.GetSection("Kms").Bind(o);
        });
        services.AddOptions<ObjectStoreOptions>().Configure(o =>
        {
            if (configuration.GetSection("Huawei:Obs").Exists())
            {
                // The old section's defaults, for a deployment that relied on them. The neutral section has none: it must name its store.
                o.ServiceUrl = "https://obs.af-south-1.myhuaweicloud.com";
                o.Region = "af-south-1";
            }

            configuration.GetSection("Huawei:Obs").Bind(o);
            configuration.GetSection("ObjectStore").Bind(o);
        });
        services.AddHostedService(sp => new OldCloudSettingsWarning(CloudSettings.OldNamesUsed(configuration), sp.GetRequiredService<ILogger<OldCloudSettingsWarning>>()));
        if (string.Equals(configuration["FieldProtection:Provider"], "envelope", StringComparison.OrdinalIgnoreCase) && CloudSettings.KmsProvider(configuration) == CloudSettings.Dew)
        {
            services.AddSingleton<IDewKmsApi, DewKmsApi>();
            services.AddSingleton<IKeyManagementService, DewKeyManagementService>();
        }

        if (CloudSettings.ObjectStoreProvider(configuration) is not null)
        {
            services.AddSingleton<IObjectStore, S3ObjectStore>();
        }

        return services;
    }
}

/// <summary>Says at start-up which replaced settings a deployment still uses; they stop working a release after ADR-027 phase 1.</summary>
internal sealed class OldCloudSettingsWarning(IReadOnlyList<string> oldNames, ILogger<OldCloudSettingsWarning> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var name in oldNames)
        {
            logger.LogWarning("Replaced setting in use, which works for one more release: {Setting}", name);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
