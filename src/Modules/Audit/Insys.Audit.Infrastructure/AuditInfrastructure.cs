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
using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Audit.Infrastructure.Persistence;
using Insys.Audit.Infrastructure.StaticData;
using Insys.Audit.Application;
using Insys.Audit.Infrastructure.Partitions;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Contracts.Auditing;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Audit.Infrastructure;

public static class AuditInfrastructure
{
    public static IServiceCollection AddAuditInfrastructure(this IServiceCollection services)
    {
        services.AddModuleDbContext<AuditDbContext>(AuditDbContext.SchemaName, "Audit");
        services.AddStaticData<AuditDbContext>(AuditDbContext.SchemaName, AuditStaticData.Descriptors);
        services.AddModuleHandlers<AuditDbContext>(typeof(ListAuditEvents).Assembly);
        services.AddScoped<IAuditEventQueries, AuditEventQueries>();
        services.AddScoped<IAuditSink, AuditSink>();
        services.AddIntegrationEventHandler<SensitiveOperationRecorded, SensitiveOperationRecordedHandler, AuditDbContext>(AuditDbContext.SchemaName);

        services.AddOptions<PartitionOptions>().BindConfiguration("Audit:Partitions");
        services.AddSingleton<PartitionMaintainer>();
        if (!InfrastructureRegistration.IsBuildTimeDocumentGeneration)
        {
            services.AddHostedService<PartitionMaintenanceService>();
        }

        services.AddHealthChecks().AddCheck<PartitionHorizonHealthCheck>("partitions", tags: ["ready"]);
        return services;
    }
}
