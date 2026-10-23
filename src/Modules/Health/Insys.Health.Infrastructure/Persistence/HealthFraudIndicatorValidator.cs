/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Health
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
using Insys.BuildingBlocks.Domain.Expressions;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Health.Infrastructure.StaticData;

namespace Insys.Health.Infrastructure.Persistence;

/// <summary>A Health fraud indicator's logic must be a valid expression, refused when drafted rather than skipped at a claim's submission (F009).</summary>
internal sealed class HealthFraudIndicatorValidator : IStaticRecordValidator
{
    public string Schema => "health";

    public string Table => "cfg_health_fraud_indicator_definitions";

    public IReadOnlyDictionary<string, string[]> Validate(StaticRecord record) =>
        record is HealthFraudIndicatorDefinitions indicator && Expression.Parse(indicator.Logic) is { IsFailure: true } parsed
            ? new Dictionary<string, string[]> { ["logic"] = [parsed.Error.Message] }
            : new Dictionary<string, string[]>();
}
