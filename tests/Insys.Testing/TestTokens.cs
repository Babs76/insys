/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Testing
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
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Insys.Testing;

/// <summary>Mints bearer tokens signed with the test-only development key.</summary>
public static class TestTokens
{
    public const string Issuer = "insys-test";
    public const string Audience = "insys-core-api";
    public const string SigningKey = "insys-test-signing-key-for-automated-tests-only-0123456789abcdef";

    /// <summary>A token with only sub and tenant_id (OQ-014); permissions come from IAM role assignments.</summary>
    /// <summary>The simulator provider's callback secret in tests (Payments:Providers:SIMULATOR:CallbackSecret).</summary>
    public const string SimulatorCallbackSecret = "test-simulator-callback-secret";

    public static string For(Guid? tenantId, string subject)
    {
        var claims = new List<Claim> { new("sub", subject) };
        if (tenantId is not null)
        {
            claims.Add(new Claim("tenant_id", tenantId.Value.ToString()));
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(30),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256),
        });
    }

    /// <summary>Configuration every test host shares.</summary>
    public static Dictionary<string, string?> HostSettings(TestDatabase database) => new()
    {
        ["ConnectionStrings:Template"] = database.RoleConnectionTemplate,
        ["Platform:OperatorTenantId"] = TestTenants.Operator.ToString(),
        ["Audit:Partitions:Enabled"] = "false",
        ["Authentication:Authority"] = "",
        ["Authentication:Issuer"] = Issuer,
        ["Authentication:Audience"] = Audience,
        ["Authentication:DevelopmentSigningKey"] = SigningKey,
        ["Outbox:RelayEnabled"] = "false",
        ["Workflow:TimersEnabled"] = "false",
        ["Billing:Jobs:Enabled"] = "false",
        ["Payments:Jobs:Enabled"] = "false",
        ["Payments:Providers:SIMULATOR:CallbackSecret"] = SimulatorCallbackSecret,
        ["FieldProtection:ActiveKeyId"] = "test1",
        ["FieldProtection:Keys:test1"] = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()),
        ["FieldProtection:HashKey"] = Convert.ToBase64String(Enumerable.Range(32, 32).Select(i => (byte)i).ToArray()),
    };
}
