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
using Insys.BuildingBlocks.Contracts;

namespace Insys.Compliance.Contracts;

public static class CompliancePermissions
{
    public const string ReturnRead = "regulatory_return.read";
    public const string ReturnCreate = "regulatory_return.create";
    public const string ReturnUpdate = "regulatory_return.update";
    public const string ReturnApprove = "regulatory_return.approve";
    public const string ReportRead = "report.read";
    public const string ReportCreate = "report.create";
}

/// <summary>A return approved by a compliance officer was submitted to its regulator.</summary>
[IntegrationEvent("reporting.RegulatoryReturnSubmitted")]
public sealed record RegulatoryReturnSubmitted(Guid ReturnId, string ReturnCode, string PeriodCode, string Regulator, string? RegulatorRef) : IIntegrationEvent;
