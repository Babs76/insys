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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.BuildingBlocks.Cloud;

/// <summary>
/// Registers the Huawei Cloud adapters the configuration selects (ADR-016, ADR-018): DEW KMS when envelope encryption
/// uses "dew", and OBS when documents are stored in the object store. Nothing is registered in development and CI, which
/// use the local providers.
/// </summary>
public static class CloudRegistration
{
    public static IServiceCollection AddInsysCloud(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DewKmsOptions>().Bind(configuration.GetSection("Huawei:Kms"));
        services.AddOptions<ObsOptions>().Bind(configuration.GetSection("Huawei:Obs"));
        if (string.Equals(configuration["FieldProtection:Provider"], "envelope", StringComparison.OrdinalIgnoreCase)
            && string.Equals(configuration["FieldProtection:Envelope:Kms"], "dew", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IDewKmsApi, DewKmsApi>();
            services.AddSingleton<IKeyManagementService, DewKeyManagementService>();
        }

        if (string.Equals(configuration["Documents:Storage:Provider"], "object-store", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IObjectStore, S3ObjectStore>();
        }

        return services;
    }
}
