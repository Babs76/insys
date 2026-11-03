/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Seed
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
namespace Insys.Seed;

/// <summary>
/// The INSYS Preview's generic accounts (docs/plans/insys-preview.md, Users): each holds one role template and nothing more, with a second
/// administrator approving the privileged ones, as for any user. The portal accounts are linked to who they are. The operator tenant's accounts
/// (OPERATOR01/02, STEWARD01/02) come from the migrator's operator bootstrap, not from here.
/// </summary>
public sealed partial class OftzaSeed
{
    public static readonly IReadOnlyList<(string Username, string Role)> PreviewAccounts =
    [
        .. Pair("admin", "TENANT_ADMIN"), .. Pair("product", "PRODUCT_MANAGER"), .. Pair("undwrit", "GENERAL_UNDERWRITER"), .. Pair("lifeuw", "LIFE_UNDERWRITER"),
        .. Pair("claims", "CLAIMS_HANDLER"), .. Pair("claimsmgr", "CLAIMS_MANAGER"), .. Pair("healthclm", "HEALTH_CLAIMS_ASSESSOR"),
        .. Pair("healthmem", "HEALTH_MEMBERSHIP_ADMINISTRATO"), .. Pair("fin", "FINANCE_OFFICER"), .. Pair("finmgr", "FINANCE_MANAGER"),
        ("pay01", "FINANCE_OFFICER"), ("pay02", "FINANCE_MANAGER"), .. Pair("dist", "DISTRIBUTION_ADMINISTRATOR"), .. Pair("reins", "REINSURANCE_OFFICER"),
        .. Pair("compl", "COMPLIANCE_OFFICER"), .. Pair("dpo", "DATA_PROTECTION_OFFICER"), .. Pair("cs", "CUSTOMER_SERVICE_AGENT"), .. Pair("ops", "OPERATIONS_ANALYST"),
        .. Pair("integ", "INTEGRATION_ADMINISTRATOR"), .. Pair("audit", "AUDITOR"), .. Pair("healthaud", "HEALTH_AUDITOR"), .. Pair("approver", "OFTZA_APPROVER"),
        ("broker01", "BROKER"), ("member01", "MEMBER"), ("provider01", "PROVIDER"),
    ];

    private static (string, string)[] Pair(string name, string role) => [($"{name}01", role), ($"{name}02", role)];

    /// <summary>Makes each account the seed hasn't yet, with its role, and links the portal accounts. Usernames are in lower case, as Keycloak keeps them.</summary>
    private async Task Accounts()
    {
        foreach (var (username, role) in PreviewAccounts)
        {
            await EnsureUser(username, role);
        }

        await PortalLinks("member01", "broker01", "provider01");
    }
}
