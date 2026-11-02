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
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Insys.Seed;

// Usage: dotnet run --project tools/Insys.Seed -- --api http://localhost:5000 --signing-key <Authentication:DevelopmentSigningKey> --issuer <issuer> --operator-tenant <id> --platform-1 <subject> --platform-2 <subject>
// It signs its personas' tokens with the development key, which the API accepts only in Development and Testing, so it can't run against production.
// A deployment signing in through Keycloak comes with a later slice.
var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 0; i < args.Length - 1; i += 2)
{
    options[args[i].TrimStart('-')] = args[i + 1];
}

foreach (var required in new[] { "api", "signing-key", "issuer", "operator-tenant", "platform-1", "platform-2" })
{
    if (!options.ContainsKey(required))
    {
        Console.Error.WriteLine($"--{required} is required.");
        return 2;
    }
}

Persona As(Guid tenant, string subject)
{
    var client = new HttpClient { BaseAddress = new Uri(options["api"]) };
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(tenant, subject));
    return new Persona(client);
}

string Token(Guid tenant, string subject)
{
    static string Part(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var unsigned = Part(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" })) + "."
        + Part(JsonSerializer.SerializeToUtf8Bytes(new { iss = options["issuer"], aud = "insys-core-api", sub = subject, tenant_id = tenant.ToString(), azp = "insys-web", nbf = now - 60, iat = now, exp = now + 7200 }));
    return unsigned + "." + Part(HMACSHA256.HashData(Encoding.UTF8.GetBytes(options["signing-key"]), Encoding.ASCII.GetBytes(unsigned)));
}

var operatorTenant = Guid.Parse(options["operator-tenant"]);
var seed = new OftzaSeed(new SeedContext(As(operatorTenant, options["platform-1"]), As(operatorTenant, options["platform-2"]), As, () => Task.Delay(TimeSpan.FromSeconds(3))));
try
{
    await seed.Run();
}
catch (SeedRefusedException refused)
{
    Console.Error.WriteLine(refused.Message);
    return 1;
}

Console.WriteLine($"{OftzaSeed.TenantCode}: {seed.TenantId}. Active, with its sample records.");
return 0;
