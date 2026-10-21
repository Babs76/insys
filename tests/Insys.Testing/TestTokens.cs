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

    public static string For(Guid? tenantId, string subject, params string[] permissions)
    {
        var claims = new List<Claim> { new("sub", subject) };
        if (tenantId is not null)
        {
            claims.Add(new Claim("tenant_id", tenantId.Value.ToString()));
        }

        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
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
    public static Dictionary<string, string?> HostSettings(string appConnectionString) => new()
    {
        ["ConnectionStrings:Insys"] = appConnectionString,
        ["Authentication:Authority"] = "",
        ["Authentication:Issuer"] = Issuer,
        ["Authentication:Audience"] = Audience,
        ["Authentication:DevelopmentSigningKey"] = SigningKey,
        ["Outbox:RelayEnabled"] = "false",
        ["FieldProtection:ActiveKeyId"] = "test1",
        ["FieldProtection:Keys:test1"] = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()),
        ["FieldProtection:HashKey"] = Convert.ToBase64String(Enumerable.Range(32, 32).Select(i => (byte)i).ToArray()),
    };
}
