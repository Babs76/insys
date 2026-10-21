namespace Insys.Testing;

/// <summary>Fixed tenants every test database has: the platform operator and two ordinary tenants.</summary>
public static class TestTenants
{
    public static readonly Guid Operator = Guid.Parse("0199f000-0000-7000-8000-000000000001");
    public static readonly Guid A = Guid.Parse("0199f000-0000-7000-8000-00000000000a");
    public static readonly Guid B = Guid.Parse("0199f000-0000-7000-8000-00000000000b");
}
