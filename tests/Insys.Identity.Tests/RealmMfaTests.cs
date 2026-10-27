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
using System.Text.Json;
using Insys.Testing;

namespace Insys.Identity.Tests;

/// <summary>
/// BR-008: the integration guide (§5) says how each client gets its second factor, and that step-up with acr_values is not
/// configured. This holds the realm (deploy/keycloak/insys-realm.json) to what the guide says, so neither drifts unnoticed.
/// </summary>
public sealed class RealmMfaTests
{
    private static readonly JsonElement Realm =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Root, "deploy", "keycloak", "insys-realm.json"))).RootElement;

    [Fact]
    public void BrokerAndProviderPortals_AlwaysAskForAOneTimeCode_TheMemberPortalOnlyOnceSetUp_BR008()
    {
        Flow(BrowserFlowOf("broker-portal")).ShouldBe("portal-mfa-browser");
        Flow(BrowserFlowOf("provider-portal")).ShouldBe("portal-mfa-browser");
        Flow(BrowserFlowOf("member-portal")).ShouldBe("member-browser");
        Steps("portal-mfa-forms").ShouldBe(["auth-username-password-form REQUIRED", "auth-otp-form REQUIRED"]);
        Steps("member-forms").ShouldBe(["auth-username-password-form REQUIRED", "member-otp CONDITIONAL"]);
        Steps("member-otp").ShouldBe(["conditional-user-configured REQUIRED", "auth-otp-form REQUIRED"]);
    }

    [Fact]
    public void StaffUseTheRealmsBrowserFlow_AndStepUpWithAcrValuesIsNotConfigured_BR008()
    {
        BrowserFlowOf("insys-web").ShouldBeNull("staff use the realm's browser flow");
        var attributes = Realm.TryGetProperty("attributes", out var found) && found.ValueKind == JsonValueKind.Object
            ? found.EnumerateObject().Select(a => a.Name).ToList()
            : [];
        attributes.ShouldNotContain("acr.loa.map", "no levels of assurance: acr_values changes nothing (guide §5)");
        Realm.GetProperty("authenticationFlows").EnumerateArray()
            .SelectMany(f => f.GetProperty("authenticationExecutions").EnumerateArray())
            .Select(e => e.TryGetProperty("authenticator", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null)
            .ShouldNotContain("conditional-level-of-authentication", "no step-up condition");
    }

    private static string? BrowserFlowOf(string clientId)
    {
        var client = Realm.GetProperty("clients").EnumerateArray().Single(c => c.GetProperty("clientId").GetString() == clientId);
        return client.TryGetProperty("authenticationFlowBindingOverrides", out var overrides) && overrides.TryGetProperty("browser", out var browser)
            && browser.ValueKind == JsonValueKind.String ? browser.GetString() : null;
    }

    private static string Flow(string? id) =>
        Realm.GetProperty("authenticationFlows").EnumerateArray().Single(f => f.GetProperty("id").GetString() == id).GetProperty("alias").GetString()!;

    private static string[] Steps(string alias) =>
        Realm.GetProperty("authenticationFlows").EnumerateArray().Single(f => f.GetProperty("alias").GetString() == alias)
            .GetProperty("authenticationExecutions").EnumerateArray()
            .Select(e => $"{(e.TryGetProperty("authenticator", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : e.GetProperty("flowAlias").GetString())} {e.GetProperty("requirement").GetString()}")
            .ToArray();
}
