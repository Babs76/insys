namespace Insys.StaticData.Generator;

/// <summary>The owning module of each schema (db/roles/module_roles.csv; ADR-002).</summary>
internal sealed record ModuleInfo(string Name, string Route, string PrimarySchema);

internal static class Modules
{
    public static readonly IReadOnlyDictionary<string, ModuleInfo> BySchema = new Dictionary<string, ModuleInfo>
    {
        ["integration"] = new("Integration", "integration", "integration"),
        ["channel"] = new("Integration", "integration", "integration"),
        ["sdm"] = new("StaticData", "static-data", "sdm"),
        ["refdata"] = new("StaticData", "static-data", "sdm"),
        ["ops"] = new("StaticData", "static-data", "sdm"),
        ["party"] = new("Party", "party", "party"),
        ["product"] = new("ProductFactory", "product-factory", "product"),
        ["rating"] = new("Rating", "rating", "rating"),
        ["rules"] = new("Rules", "rules", "rules"),
        ["workflow"] = new("Workflow", "workflow", "workflow"),
        ["document"] = new("Documents", "documents", "document"),
        ["billing"] = new("Billing", "billing", "billing"),
        ["payment"] = new("Payments", "payments", "payment"),
        ["claims"] = new("ClaimsFramework", "claims-framework", "claims"),
        ["life"] = new("Life", "life", "life"),
        ["general"] = new("General", "general", "general"),
        ["health"] = new("Health", "health", "health"),
        ["finance"] = new("Finance", "finance", "finance"),
        ["commission"] = new("Distribution", "distribution", "commission"),
        ["reinsurance"] = new("Reinsurance", "reinsurance", "reinsurance"),
        ["reporting"] = new("Compliance", "compliance", "reporting"),
        ["analytics"] = new("Analytics", "analytics", "analytics"),
        ["tenant"] = new("Tenancy", "tenancy", "tenant"),
        ["iam"] = new("Identity", "identity", "iam"),
        ["audit"] = new("Audit", "audit", "audit"),
    };
}
