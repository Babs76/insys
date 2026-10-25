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
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Identity.Infrastructure.Persistence;

namespace Insys.Identity.Infrastructure.StaticData;

/// <summary>ADR-026: a role holds functions (iam.cfg_role_function_assignments); its permission set is retired.</summary>
internal sealed class RoleDefinitionValidator : IStaticRecordValidator
{
    public string Schema => IdentityDbContext.SchemaName;

    public string Table => "cfg_roles_and_permission_sets";

    public string Rules => "permissions is retired (ADR-026): a role is granted functions through role-function assignments, so it must be empty.";

    public IReadOnlyDictionary<string, string[]> Validate(StaticRecord record) =>
        record is RolesAndPermissionSets { Permissions.Length: > 0 }
            ? new Dictionary<string, string[]> { ["permissions"] = ["Retired: grant the role functions through role-function assignments (ADR-026)."] }
            : new Dictionary<string, string[]>();
}

/// <summary>ADR-026 §4: a segregation-of-duties rule pairs two functions; the permission pair is retired.</summary>
internal sealed class DutyConflictRuleValidator : IStaticRecordValidator
{
    public string Schema => IdentityDbContext.SchemaName;

    public string Table => "cfg_segregation_of_duties_conflict_rules";

    public string Rules => "functionA and functionB are required and differ; permissionA and permissionB are retired (ADR-026 §4) and must be empty.";

    public IReadOnlyDictionary<string, string[]> Validate(StaticRecord record)
    {
        var errors = new Dictionary<string, string[]>();
        if (record is not SegregationOfDutiesConflictRules rule)
        {
            return errors;
        }

        if (string.IsNullOrEmpty(rule.FunctionA))
        {
            errors["functionA"] = ["Required: a rule pairs two functions (ADR-026 §4)."];
        }

        if (string.IsNullOrEmpty(rule.FunctionB))
        {
            errors["functionB"] = ["Required: a rule pairs two functions (ADR-026 §4)."];
        }
        else if (rule.FunctionA == rule.FunctionB)
        {
            errors["functionB"] = ["Must differ from functionA."];
        }

        if (!string.IsNullOrEmpty(rule.PermissionA) || !string.IsNullOrEmpty(rule.PermissionB))
        {
            errors["permissionA"] = ["Retired: name the functions (ADR-026 §4)."];
        }

        return errors;
    }
}
