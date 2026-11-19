/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Identity.Tests
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
using Insys.Identity.Application.Users;
using Insys.Identity.Domain.Users;
using Shouldly;
using Xunit;

namespace Insys.Identity.Tests;

/// <summary>A role's name to show (front-end request, 18 November 2026): its own label in the caller's languages, else its role template's name, else none.</summary>
public sealed class RoleLabelTests
{
    private sealed class Templates : IRoleTemplateNames
    {
        public string? NameOf(string roleCode) => roleCode == "CLAIMS_HANDLER" ? "Claims handler" : null;
    }

    private static RoleFacts Role(string code, Dictionary<string, string>? labels = null) =>
        new(code, true, new HashSet<string>(), false) { Labels = labels };

    [Fact]
    public void ARole_IsShownByItsOwnLabel_InTheFirstLanguageOfTheChainThatHasOne()
    {
        var role = Role("R_1", new() { ["en"] = "Claims clerk", ["zu"] = "Umabhalane wezimangalo" });
        RoleLabels.For(role, ["zu", "en"], new Templates()).ShouldBe("Umabhalane wezimangalo");
        RoleLabels.For(role, ["af", "en"], new Templates()).ShouldBe("Claims clerk", "the chain falls back to English");
    }

    [Fact]
    public void ARoleWithNoLabelOfItsOwn_IsShownByItsTemplatesName_ElseNone()
    {
        RoleLabels.For(Role("CLAIMS_HANDLER"), ["en"], new Templates()).ShouldBe("Claims handler");
        RoleLabels.For(Role("CLAIMS_HANDLER", new() { ["fr"] = "Gestionnaire" }), ["en"], new Templates()).ShouldBe("Claims handler",
            "a label in no language of the chain falls back to the template");
        RoleLabels.For(Role("R_NONE"), ["en"], new Templates()).ShouldBeNull("the client shows the code");
        RoleLabels.For(Role("CLAIMS_HANDLER"), ["en"], null).ShouldBeNull();
    }
}
