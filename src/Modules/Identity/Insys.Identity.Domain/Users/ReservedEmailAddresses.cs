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
namespace Insys.Identity.Domain.Users;

/// <summary>
/// Addresses under the domains reserved for testing and documentation (RFC 2606, RFC 6761): .test, .example and .invalid, and
/// example.com, example.net and example.org. No mail reaches them, so INSYS sends no invitation or other identity email to them (Ade,
/// 6 November 2026): the INSYS Preview's generic accounts use example.test, and once its Keycloak sends real email, each invitation would
/// only bounce. A user is still required to have an email (keycloak-user-provisioning); only the sending is skipped.
/// </summary>
public static class ReservedEmailAddresses
{
    private static readonly string[] TopLevel = ["test", "example", "invalid"];
    private static readonly string[] SecondLevel = ["example.com", "example.net", "example.org"];

    public static bool IsReserved(string? email)
    {
        var at = email?.LastIndexOf('@') ?? -1;
        if (at < 0)
        {
            return false;
        }

        var domain = email![(at + 1)..].Trim().TrimEnd('.').ToLowerInvariant();
        return TopLevel.Any(t => domain == t || domain.EndsWith("." + t, StringComparison.Ordinal))
            || SecondLevel.Any(d => domain == d || domain.EndsWith("." + d, StringComparison.Ordinal));
    }
}
