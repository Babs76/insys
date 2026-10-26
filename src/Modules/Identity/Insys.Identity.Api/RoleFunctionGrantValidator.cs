/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Identity
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
using Insys.BuildingBlocks.Api.Functions;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Identity.Infrastructure.StaticData;

namespace Insys.Identity.Api;

/// <summary>
/// BR-019: a role's function grant names a function in the catalogue (ADR-026) that a role can hold: a screen or action,
/// not a JOB function (a tenant switch). A grant naming anything else was approved and granted nothing; now it is refused
/// with a field error, on create, edit and import alike. The catalogue in code is the database catalogue (FunctionCatalogueTests).
/// </summary>
internal sealed class RoleFunctionGrantValidator : IStaticRecordValidator
{
    public string Schema => "iam";

    public string Table => "cfg_role_function_assignments";

    public string Rules => "functionCode is a screen or action function in the function catalogue (identity/static/function-catalogue); a JOB function, or a code "
        + "the catalogue does not have, is refused (422 on functionCode, BR-019).";

    public IReadOnlyDictionary<string, string[]> Validate(StaticRecord record)
    {
        if (record is not RoleFunctionAssignments grant)
        {
            return new Dictionary<string, string[]>();
        }

        var function = FunctionRegistry.All().FirstOrDefault(f => f.Function.Code == grant.FunctionCode)?.Function;
        return function switch
        {
            null => new Dictionary<string, string[]> { ["functionCode"] = [$"{grant.FunctionCode} is not a function in the catalogue."] },
            { Kind: FunctionKind.Job } => new Dictionary<string, string[]> { ["functionCode"] = [$"{grant.FunctionCode} is a JOB function: a tenant switch, not a grant."] },
            _ => new Dictionary<string, string[]>(),
        };
    }
}
