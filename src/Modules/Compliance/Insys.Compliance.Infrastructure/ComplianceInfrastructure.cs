/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Compliance
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
using Insys.Compliance.Application;
using Insys.Compliance.Infrastructure.Persistence;
using Insys.Compliance.Infrastructure.StaticData;
using Insys.Finance.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Compliance.Infrastructure;

public static class ComplianceInfrastructure
{
    public static IServiceCollection AddComplianceInfrastructure(this IServiceCollection services)
    {
        // Its dashboard figure codes, for the OpenAPI enum of DashboardFigure.code.
        BuildingBlocks.Application.DashboardFigureCodes.Declare(Insys.Compliance.Application.ReturnFigures.ByStatus, Insys.Compliance.Application.ReturnFigures.AwaitingAction, Insys.Compliance.Application.ReturnFigures.Overdue);
        // ADR-026 §2: this module's event consumers run under its JOB function.
        BuildingBlocks.Application.Context.JobFunctions.ForConsumers(Persistence.ComplianceDbContext.SchemaName, "COMPLIANCE_EVENT_PROCESSING");
        const string schema = ComplianceDbContext.SchemaName;
        services.AddModuleDbContext<ComplianceDbContext>(schema, "Compliance");
        services.AddStaticData<ComplianceDbContext>(schema, ComplianceStaticData.Descriptors);
        services.AddModuleHandlers<ComplianceDbContext>(typeof(PrepareReturn).Assembly);
        services.AddDomainEventMapper<ComplianceEventMapper>(schema);
        services.AddScoped<IComplianceRepository, ComplianceRepository>();
        services.AddScoped<IComplianceCodes, ComplianceCodes>();
        services.AddScoped<ReturnValues>();
        services.AddIntegrationEventHandler<AccountingPeriodClosed, ReturnPolicies, ComplianceDbContext>(schema, "AccountingPeriodClosed");
        return services;
    }
}
