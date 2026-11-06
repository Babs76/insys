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
// --accounts true also makes the INSYS Preview's generic accounts; with Keycloak, their new passwords go to stdout as JSON, and nothing else does.
// Or, where the API takes only Keycloak's tokens: --api <url> --keycloak-url <url> [--keycloak-realm insys] --keycloak-admin <master realm admin> --operator-tenant <id>
// --platform-1 <Keycloak username> --platform-2 <Keycloak username>, with the admin's password in INSYS_SEED_KEYCLOAK_ADMIN_PASSWORD (never on the command line).
var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 0; i < args.Length - 1; i += 2)
{
    options[args[i].TrimStart('-')] = args[i + 1];
}

var keycloak = options.TryGetValue("keycloak-url", out var keycloakUrl)
    ? new KeycloakSignIn(new HttpClient { BaseAddress = new Uri(keycloakUrl.TrimEnd('/') + "/") }, options.GetValueOrDefault("keycloak-realm", "insys"), options["keycloak-admin"],
        Environment.GetEnvironmentVariable("INSYS_SEED_KEYCLOAK_ADMIN_PASSWORD") ?? throw new InvalidOperationException("Set INSYS_SEED_KEYCLOAK_ADMIN_PASSWORD."))
    : null;
foreach (var required in keycloak is null ? new[] { "api", "signing-key", "issuer", "operator-tenant", "platform-1", "platform-2" } : ["api", "keycloak-admin", "operator-tenant", "platform-1", "platform-2"])
{
    if (!options.ContainsKey(required))
    {
        Console.Error.WriteLine($"--{required} is required.");
        return 2;
    }
}

Persona As(Guid tenant, string subject)
{
    if (keycloak is not null)
    {
        // The platform's users are named as they are in Keycloak; a tenant's carry its code.
        var username = tenant == Guid.Parse(options["operator-tenant"]) ? subject : KeycloakSignIn.Username(OftzaSeed.TenantCode, subject);
        return new Persona(new HttpClient(keycloak.For(username)) { BaseAddress = new Uri(options["api"]) }) { Settle = Settle };
    }

    var client = new HttpClient { BaseAddress = new Uri(options["api"]) };
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(tenant, subject));
    return new Persona(client) { Settle = Settle };
}

string Token(Guid tenant, string subject)
{
    static string Part(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var unsigned = Part(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" })) + "."
        + Part(JsonSerializer.SerializeToUtf8Bytes(new { iss = options["issuer"], aud = "insys-core-api", sub = subject, tenant_id = tenant.ToString(), azp = "insys-web", nbf = now - 60, iat = now, exp = now + 7200 }));
    return unsigned + "." + Part(HMACSHA256.HashData(Encoding.UTF8.GetBytes(options["signing-key"]), Encoding.ASCII.GetBytes(unsigned)));
}

// A deployment's API relays its outbox itself, so the seed settles as the tests' DrainOutbox does: until /health's outbox-lag check says no
// event is pending. A fixed 3 s was too short for a slow relay (rotate-passwords run 41937659695: a checker's 404, and a rehearsal's 403 on
// a grant not yet applied). At most 30 s, so an event stuck elsewhere doesn't hold every step; a checker's own wait is longer (PendingWaitSeconds).
var health = new HttpClient { BaseAddress = new Uri(options["api"]), Timeout = TimeSpan.FromSeconds(10) };
async Task Settle()
{
    var deadline = DateTime.UtcNow.AddSeconds(30);
    while (DateTime.UtcNow < deadline)
    {
        try
        {
            using var response = await health.GetAsync("/health");   // 503 while a check is unhealthy, with the same body
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (body.RootElement.GetProperty("checks").GetProperty("outbox-lag").GetProperty("data").GetProperty("pending").GetInt32() == 0)
            {
                return;
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // No answer, or not the shape expected: wait as before.
        }

        await Task.Delay(TimeSpan.FromSeconds(1));
    }
}

var operatorTenant = Guid.Parse(options["operator-tenant"]);
var seed = new OftzaSeed(new SeedContext(As(operatorTenant, options["platform-1"]), As(operatorTenant, options["platform-2"]), As, Settle, OwnRealm: keycloak is not null, Accounts: options.ContainsKey("accounts")));
if (keycloak is not null)
{
    await keycloak.Start();
}

try
{
    await seed.Run();
}
catch (SeedRefusedException refused)
{
    Console.Error.WriteLine(refused.Message);
    return 1;
}
finally
{
    if (keycloak is not null)
    {
        await keycloak.Stop();
    }
}

// With --accounts true and Keycloak: each generic account's new password, as JSON on stdout and nowhere else, for the caller to encrypt at once.
if (keycloak is not null && options.ContainsKey("accounts"))
{
    var passwords = new List<object>();
    foreach (var (username, role) in OftzaSeed.PreviewAccounts)
    {
        // signIn: exactly what to type at the sign-in page, the tenant's code included; displayName: what INSYS shows for the account.
        var signIn = KeycloakSignIn.Username(OftzaSeed.TenantCode, username);
        passwords.Add(new { signIn, displayName = OftzaSeed.DisplayName(role), username, role, password = await keycloak.SetPassword(signIn) });
    }

    foreach (var (username, role) in OftzaSeed.OperatorAccounts)
    {
        var signIn = KeycloakSignIn.Username(OftzaSeed.OperatorTenantCode, username);
        passwords.Add(new { signIn, displayName = OftzaSeed.DisplayName(role), username, role, password = await keycloak.SetPassword(signIn) });
    }

    Console.Out.WriteLine(JsonSerializer.Serialize(new { tenant = OftzaSeed.TenantCode, accounts = passwords }));
    Console.Error.WriteLine($"{OftzaSeed.TenantCode}: {seed.TenantId}, with its {passwords.Count} accounts.");
    return 0;
}

Console.WriteLine($"{OftzaSeed.TenantCode}: {seed.TenantId}. Active, with its sample records.");
return 0;
