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
    public static string For(Guid? tenantId, string subject) => For(tenantId, subject, DateTime.UtcNow);

    /// <summary>A token valid around <paramref name="now"/>, for a host whose clock is fixed (business date tests).</summary>
    /// <param name="clientId">The OAuth client the token was issued to (azp), which names the request's channel (F005).</param>
    /// <param name="multiFactor">Whether the token shows a second factor (amr otp), as Keycloak's AMR mapper adds after OTP (external-user access §6).</param>
    public static string For(Guid? tenantId, string subject, DateTime now, string? clientId = null, bool multiFactor = false)
    {
        var claims = new List<Claim> { new("sub", subject) };
        if (tenantId is not null)
        {
            claims.Add(new Claim("tenant_id", tenantId.Value.ToString()));
        }

        if (clientId is not null)
        {
            claims.Add(new Claim("azp", clientId));
        }

        if (multiFactor)
        {
            claims.Add(new Claim("amr", "pwd"));
            claims.Add(new Claim("amr", "otp"));
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Subject = new ClaimsIdentity(claims),
            // Valid at the given time and at the real time, whichever the host checks a token against.
            NotBefore = (now < DateTime.UtcNow ? now : DateTime.UtcNow).AddMinutes(-5),
            IssuedAt = now,
            Expires = (now > DateTime.UtcNow ? now : DateTime.UtcNow).AddMinutes(30),
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
        ["Outbox:Parking:JobEnabled"] = "false",
        ["Testing:DailyJobRunner:Enabled"] = "true",
        ["Workflow:TimersEnabled"] = "false",
        ["Billing:Jobs:Enabled"] = "false",
        ["Payments:Jobs:Enabled"] = "false",
        ["FieldProtection:ActiveKeyId"] = "test1",
        ["FieldProtection:Keys:test1"] = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()),
        ["FieldProtection:HashKey"] = Convert.ToBase64String(Enumerable.Range(32, 32).Select(i => (byte)i).ToArray()),
    };
}
