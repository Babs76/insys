using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Insys.StaticData.Generator;

// Generates the static data entities, inputs, EF mappings, descriptors and endpoints for every static data table: those
// of V0002, and those later migrations create or extend (Schema.ReadAll). Each needs an entry in the catalogue, or in
// docs/specs/static-data-catalogue-additions.json for tables added after the original specification.
// Usage: dotnet run --project tools/Insys.StaticData.Generator [-- <repository root>]
// Output is deterministic and committed. Never edit the generated files; change the generator or the inputs.
var root = args.Length > 0 ? args[0] : FindRoot();
Emit.Notice = File.ReadAllLines(Path.Combine(root, "tools", "source-header.txt"));
var tables = Schema.ReadAll(Path.Combine(root, "db", "migrations"));
using var catalogue = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "docs", "specs", "static-data-catalogue.json")));
var additionsPath = Path.Combine(root, "docs", "specs", "static-data-catalogue-additions.json");
using var additions = JsonDocument.Parse(File.Exists(additionsPath) ? File.ReadAllText(additionsPath) : """{"entities":[]}""");

var entities = new List<Entity>();
foreach (var item in catalogue.RootElement.GetProperty("entities").EnumerateArray().Concat(additions.RootElement.GetProperty("entities").EnumerateArray()))
{
    var tableName = item.GetProperty("table").GetString()!;
    if (!tables.TryGetValue(tableName, out var table))
    {
        // "tenant.tenant (core)" is the tenant register in V0001, owned by the Tenancy module's own code.
        Console.WriteLine($"skip {tableName}: not a static data table in the migrations");
        continue;
    }

    var module = Modules.BySchema[table.Schema];
    var name = item.GetProperty("entity").GetString()!;
    entities.Add(new Entity(table, module, name, Names.Pascal(name), Names.Kebab(name), EntityCode(table.Name),
        item.GetProperty("scope").GetString()!, item.GetProperty("tier").GetInt32(), table.Statuses.Contains("PUBLISHED") ? "Configuration" : "Reference"));
}

var unmatched = tables.Keys.Except(entities.Select(e => e.Table.FullName)).ToList();
if (unmatched.Count > 0)
{
    throw new InvalidOperationException($"Tables without a catalogue entry: {string.Join(", ", unmatched)}");
}

foreach (var group in entities.GroupBy(e => e.Module.Name))
{
    var duplicates = group.GroupBy(e => e.ClassName).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
    if (duplicates.Count > 0 || group.GroupBy(e => e.Route).Any(g => g.Count() > 1))
    {
        throw new InvalidOperationException($"{group.Key}: duplicate entity names {string.Join(", ", duplicates)}");
    }

    var infrastructure = Path.Combine(root, "src", "Modules", group.Key, $"Insys.{group.Key}.Infrastructure", "StaticData", "Generated");
    var api = Path.Combine(root, "src", "Modules", group.Key, $"Insys.{group.Key}.Api", "StaticData", "Generated");
    var ordered = group.OrderBy(e => e.Table.FullName, StringComparer.Ordinal).ToList();
    var files = ordered.ToDictionary(e => Path.Combine(infrastructure, $"{e.ClassName}.g.cs"), Emit.Entity);
    files[Path.Combine(infrastructure, $"{group.Key}StaticData.g.cs")] = Emit.Model(group.Key, ordered);
    files[Path.Combine(api, $"{group.Key}StaticDataEndpoints.g.cs")] = Emit.Endpoints(group.Key, ordered);
    Sync(infrastructure, files);
    Sync(api, files);
    Console.WriteLine($"{group.Key}: {ordered.Count} tables");
}

Console.WriteLine($"Generated {entities.Count} static data tables.");
return 0;

static string EntityCode(string table) =>
    table.Length <= 40 ? table : $"{table[..31]}_{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(table)))[..8]}";

// Writes only files whose content changed and deletes only files no longer generated, so a synced folder (for
// example iCloud Drive) does not see whole directories replaced.
static void Sync(string directory, Dictionary<string, string> files)
{
    Directory.CreateDirectory(directory);
    foreach (var (path, content) in files.Where(f => Path.GetDirectoryName(f.Key) == directory))
    {
        var normalised = content.ReplaceLineEndings("\n");
        if (!File.Exists(path) || File.ReadAllText(path) != normalised)
        {
            File.WriteAllText(path, normalised, new UTF8Encoding(false));
        }
    }

    foreach (var stale in Directory.GetFiles(directory).Where(f => !files.ContainsKey(f)))
    {
        File.Delete(stale);
    }
}

static string FindRoot()
{
    for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "Insys.slnx")))
        {
            return directory.FullName;
        }
    }

    throw new InvalidOperationException("Run from inside the repository, or pass its root.");
}

internal sealed record Entity(Table Table, ModuleInfo Module, string Name, string ClassName, string Route, string EntityCode, string Scope, int Tier, string Lifecycle);

internal static class Names
{
    public static string Pascal(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(c);
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        var result = string.Concat(words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        return char.IsDigit(result[0]) ? "N" + result : result;
    }

    public static string Camel(string pascal) => char.ToLowerInvariant(pascal[0]) + pascal[1..];

    public static string Kebab(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        return builder.ToString().Trim('-');
    }

    public static string Literal(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}

internal sealed record Field(Column Column, string Property, string EntityType, string InputType, string Apply, List<string> Config, List<string> Checks, bool JsonIgnore);

internal static class Emit
{
    /// <summary>The OpenFactor source file header (tools/source-header.txt), set before any file is emitted.</summary>
    public static string[] Notice { get; set; } = [];

    /// <summary>The source file header for the owning module, then the auto-generated marker.</summary>
    private static string Header(string module) =>
        "/*\n" + string.Concat(Notice.Select(line => $" * {line.Replace("{COMPONENT}", module, StringComparison.Ordinal)}\n")) + " */\n" + Marker;

    private const string Marker = """
        // <auto-generated>
        // Generated by tools/Insys.StaticData.Generator from db/migrations/V0002__static_data_schema.sql and
        // docs/specs/static-data-catalogue.json. Do not edit; run the generator instead.
        // </auto-generated>
        #nullable enable

        """;

    public static string Entity(Entity entity)
    {
        var fields = Fields(entity);
        var ns = $"Insys.{entity.Module.Name}.Infrastructure.StaticData";
        var s = new StringBuilder(Header(entity.Module.Name));
        s.AppendLine("using System.Text.Json;");
        s.AppendLine("using System.Text.Json.Serialization;");
        s.AppendLine("using Insys.BuildingBlocks.Application.Security;");
        s.AppendLine("using Insys.BuildingBlocks.Infrastructure.StaticData;");
        s.AppendLine();
        s.AppendLine($"namespace {ns};");
        s.AppendLine();
        s.AppendLine($"/// <summary>{Xml(entity.Name)}: {entity.Table.FullName}. Scope {entity.Scope}, Tier {entity.Tier}, {entity.Lifecycle.ToLowerInvariant()} lifecycle.</summary>");
        s.AppendLine($"public sealed class {entity.ClassName} : StaticRecord<{entity.ClassName}Input>");
        s.AppendLine("{");
        foreach (var field in fields)
        {
            if (field.JsonIgnore)
            {
                s.AppendLine("    [JsonIgnore]");
            }

            var initializer = field.EntityType switch
            {
                "string" => " = \"\";",
                "string[]" or "bool[]" => " = [];",
                "byte[]" => " = [];",
                _ => "",
            };
            s.AppendLine($"    public {field.EntityType} {field.Property} {{ get; set; }}{initializer}");
            s.AppendLine();
        }

        s.AppendLine($"    public override void ApplyFields({entity.ClassName}Input input, IFieldProtector protector)");
        s.AppendLine("    {");
        foreach (var line in fields.Select(f => f.Apply).Where(a => a.Length > 0).Distinct())
        {
            s.AppendLine($"        {line}");
        }

        s.AppendLine("    }");
        s.AppendLine("}");
        s.AppendLine();
        s.AppendLine($"/// <summary>The fields a client sends for {Xml(entity.Name)}.</summary>");
        s.AppendLine($"public sealed record {entity.ClassName}Input : StaticRecordInput");
        s.AppendLine("{");
        foreach (var field in fields.Where(f => f.InputType.Length > 0))
        {
            s.AppendLine($"    public {field.InputType} {InputName(field)} {{ get; init; }}");
            s.AppendLine();
        }

        s.AppendLine("    protected override void ValidateFields(StaticFieldErrors errors)");
        s.AppendLine("    {");
        foreach (var check in fields.SelectMany(f => f.Checks))
        {
            s.AppendLine($"        {check}");
        }

        s.AppendLine("    }");
        s.AppendLine("}");
        return s.ToString();
    }

    public static string Model(string module, List<Entity> entities)
    {
        var s = new StringBuilder(Header(module));
        s.AppendLine("using Insys.BuildingBlocks.Infrastructure.StaticData;");
        s.AppendLine("using Microsoft.EntityFrameworkCore;");
        s.AppendLine();
        s.AppendLine($"namespace Insys.{module}.Infrastructure.StaticData;");
        s.AppendLine();
        s.AppendLine($"/// <summary>The {entities.Count} static data tables owned by the {module} module.</summary>");
        s.AppendLine($"public static class {module}StaticData");
        s.AppendLine("{");
        s.AppendLine("    public static readonly IReadOnlyList<StaticDataDescriptor> Descriptors =");
        s.AppendLine("    [");
        foreach (var e in entities)
        {
            s.AppendLine($"        new({Names.Literal(e.Table.Schema)}, {Names.Literal(e.Table.Name)}, {Names.Literal(e.EntityCode)}, {Names.Literal(e.Name)}, "
                + $"{Names.Literal(e.Module.Route)}, {Names.Literal(e.Route)}, StaticScope.{e.Scope}, {e.Tier}, StaticLifecycle.{e.Lifecycle}, "
                + $"typeof({e.ClassName}), typeof({e.ClassName}Input)),");
        }

        s.AppendLine("    ];");
        s.AppendLine();
        s.AppendLine("    public static void Configure(ModelBuilder modelBuilder)");
        s.AppendLine("    {");
        foreach (var e in entities)
        {
            s.AppendLine($"        modelBuilder.Entity<{e.ClassName}>(entity =>");
            s.AppendLine("        {");
            s.AppendLine($"            entity.ToTable({Names.Literal(e.Table.Name)}, {Names.Literal(e.Table.Schema)});");
            s.AppendLine("            StaticModel.ConfigureCommon(entity);");
            foreach (var field in Fields(e))
            {
                s.AppendLine($"            entity.Property(x => x.{field.Property}).HasColumnName({Names.Literal(field.Column.Name)}){string.Concat(field.Config)};");
            }

            s.AppendLine("        });");
        }

        s.AppendLine("    }");
        s.AppendLine("}");
        return s.ToString();
    }

    public static string Endpoints(string module, List<Entity> entities)
    {
        var s = new StringBuilder(Header(module));
        s.AppendLine("using Insys.BuildingBlocks.Api.StaticData;");
        s.AppendLine($"using Insys.{module}.Infrastructure.Persistence;");
        s.AppendLine($"using Insys.{module}.Infrastructure.StaticData;");
        s.AppendLine("using Microsoft.AspNetCore.Routing;");
        s.AppendLine();
        s.AppendLine($"namespace Insys.{module}.Api.StaticData;");
        s.AppendLine();
        s.AppendLine($"public static class {module}StaticDataEndpoints");
        s.AppendLine("{");
        s.AppendLine($"    public const string Tag = \"{module} static data\";");
        s.AppendLine();
        s.AppendLine("    public static void MapStaticData(IEndpointRouteBuilder api)");
        s.AppendLine("    {");
        for (var i = 0; i < entities.Count; i++)
        {
            // Tables whose rows an aggregate manages (its own lifecycle and approval) get the read endpoints only.
            var readOnly = AggregateManagedTables.Contains(entities[i].Table.FullName) ? ", readOnly: true" : "";
            s.AppendLine($"        api.MapStaticEntity<{module}DbContext, {entities[i].ClassName}, {entities[i].ClassName}Input>({module}StaticData.Descriptors[{i}], Tag{readOnly});");
        }

        s.AppendLine("    }");
        s.AppendLine("}");
        return s.ToString();
    }

    /// <summary>Static tables that are an aggregate's state: ProductVersion manages product versions (domain-model.json, PRD-002 to PRD-007).</summary>
    private static readonly HashSet<string> AggregateManagedTables = ["product.cfg_product_definitions_and_versions"];

    /// <summary>Columns V0012 made nullable and deprecated (ADR-010): record_status, or an aggregate, holds the state instead.</summary>
    private static readonly HashSet<string> DeprecatedColumns =
    [
        "product.cfg_product_definitions_and_versions.status",
        "integration.cfg_api_catalogue_entries.status",
        "integration.cfg_event_schema_versions.status",
        "finance.cfg_accounting_calendar_and_periods.status",
    ];

    private static string InputName(Field field) => field.Column.Name.EndsWith("_enc", StringComparison.Ordinal)
        ? Names.Pascal(field.Column.Name[..^4])
        : field.Property;

    private static List<Field> Fields(Entity entity)
    {
        var fields = new List<Field>();
        var columns = entity.Table.Columns.Where(c => !Schema.CommonColumns.Contains(c.Name) && !DeprecatedColumns.Contains($"{entity.Table.FullName}.{c.Name}")).ToList();
        foreach (var column in columns)
        {
            var property = Names.Pascal(column.Name);
            if (property == entity.ClassName)
            {
                property += "Value";
            }

            var json = Names.Camel(property);
            var required = column.NotNull;
            var config = new List<string>();
            var checks = new List<string>();
            string entityType, inputType, apply;
            var ignore = false;

            if (column.Name.EndsWith("_enc", StringComparison.Ordinal))
            {
                // A masked field (DBA-014): the client sends plain text; _enc, _hash and _last4 are derived and only _last4 is returned.
                var baseName = column.Name[..^4];
                var plain = Names.Pascal(baseName);
                var hasLast4 = columns.Any(c => c.Name == baseName + "_last4");
                entityType = required ? "byte[]" : "byte[]?";
                inputType = "string?";
                apply = required
                    ? $"var {Names.Camel(plain)} = protector.Protect(input.{plain}!); {property} = {Names.Camel(plain)}.Encrypted; {plain}Hash = {Names.Camel(plain)}.Hash;"
                      + (hasLast4 ? $" {plain}Last4 = {Names.Camel(plain)}.Last4;" : "")
                    : $"var {Names.Camel(plain)} = input.{plain} is null ? null : protector.Protect(input.{plain}); {property} = {Names.Camel(plain)}?.Encrypted; {plain}Hash = {Names.Camel(plain)}?.Hash;"
                      + (hasLast4 ? $" {plain}Last4 = {Names.Camel(plain)}?.Last4;" : "");
                ignore = true;
                if (required)
                {
                    checks.Add($"errors.Required(\"{Names.Camel(plain)}\", {plain});");
                }

                fields.Add(new Field(column, property, entityType, inputType, apply, config, checks, ignore));
                continue;
            }

            if (columns.Any(c => c.Name.EndsWith("_enc", StringComparison.Ordinal)
                    && (column.Name == c.Name[..^4] + "_hash" || column.Name == c.Name[..^4] + "_last4")))
            {
                var maskedType = column.MaxLength is { } n ? $".HasMaxLength({n})" : "";
                fields.Add(new Field(column, property, required ? "string" : "string?", "", "", [maskedType], [], column.Name.EndsWith("_hash", StringComparison.Ordinal)));
                continue;
            }

            var sql = column.SqlType;
            (entityType, inputType) = sql switch
            {
                _ when sql.StartsWith("varchar", StringComparison.Ordinal) || sql.StartsWith("char(", StringComparison.Ordinal) || sql == "text" => ("string", "string?"),
                "uuid" => ("Guid", "Guid?"),
                "integer" => ("int", "int?"),
                "bigint" => ("long", "long?"),
                "smallint" => ("short", "short?"),
                _ when sql.StartsWith("numeric", StringComparison.Ordinal) => ("decimal", "decimal?"),
                "boolean" => ("bool", "bool?"),
                "date" => ("DateOnly", "DateOnly?"),
                "timestamptz" => ("DateTimeOffset", "DateTimeOffset?"),
                "jsonb" => ("JsonElement", "JsonElement?"),
                "text[]" => ("string[]", "string[]?"),
                "boolean[]" => ("bool[]", "bool[]?"),
                "interval" => ("string", "string?"),
                "int4range" => ("string", "string?"),
                "numrange" => ("string", "string?"),
                "cidr[]" => ("string[]", "string[]?"),
                _ => throw new InvalidOperationException($"{entity.Table.FullName}.{column.Name}: unmapped type {sql}"),
            };

            var valueType = entityType is "Guid" or "int" or "long" or "short" or "decimal" or "bool" or "DateOnly" or "DateTimeOffset" or "JsonElement";
            if (!required)
            {
                entityType += "?";
            }

            apply = required
                ? $"{property} = input.{property}{(valueType ? ".GetValueOrDefault()" : "!")};"
                : $"{property} = input.{property};";

            if (column.MaxLength is { } max)
            {
                config.Add($".HasMaxLength({max})");
                checks.Add($"errors.MaxLength(\"{json}\", {property}, {max});");
            }

            if (sql.StartsWith("numeric(", StringComparison.Ordinal))
            {
                var parts = sql[8..^1].Split(',').Select(p => p.Trim()).ToArray();
                config.Add(parts.Length == 2 ? $".HasPrecision({parts[0]}, {parts[1]})" : $".HasPrecision({parts[0]})");
            }

            switch (sql)
            {
                case "jsonb":
                    config.Add(".HasColumnType(\"jsonb\")");
                    break;
                case "interval":
                    config.Add($".HasConversion(StaticConverters.{(required ? "" : "Optional")}Duration).HasColumnType(\"interval\")");
                    checks.Add($"errors.Valid(\"{json}\", {property}, StaticConverters.IsDuration, \"an ISO 8601 duration such as P3M\");");
                    break;
                case "int4range":
                    config.Add($".HasConversion(StaticConverters.{(required ? "" : "Optional")}IntRange).HasColumnType(\"int4range\")");
                    checks.Add($"errors.Valid(\"{json}\", {property}, StaticConverters.IsIntRange, \"a range such as [18,30)\");");
                    break;
                case "numrange":
                    config.Add($".HasConversion(StaticConverters.{(required ? "" : "Optional")}DecimalRange).HasColumnType(\"numrange\")");
                    checks.Add($"errors.Valid(\"{json}\", {property}, StaticConverters.IsDecimalRange, \"a range such as [0,1000.50)\");");
                    break;
                case "cidr[]":
                    config.Add($".HasConversion(StaticConverters.{(required ? "" : "Optional")}CidrArray).HasColumnType(\"cidr[]\")");
                    checks.Add($"if ({property} is not null) {{ foreach (var item in {property}) {{ errors.Valid(\"{json}\", item, StaticConverters.IsCidr, \"CIDR notation such as 10.0.0.0/8\"); }} }}");
                    break;
            }

            if (required)
            {
                checks.Insert(0, $"errors.Required(\"{json}\", {property});");
            }

            if (column.AllowedValues is { } allowed)
            {
                var list = "[" + string.Join(", ", allowed.Select(Names.Literal)) + "]";
                checks.Add(column.ArrayValues ? $"errors.AllOneOf(\"{json}\", {property}, {list});" : $"errors.OneOf(\"{json}\", {property}, {list});");
            }

            if (column.Between is { } between)
            {
                checks.Add($"if ({property} is < {between.Min} or > {between.Max}) {{ errors.Add(\"{json}\", \"{json} must be between {between.Min} and {between.Max}.\"); }}");
            }

            if (column.Pattern is { } pattern)
            {
                checks.Add($"errors.Valid(\"{json}\", {property}, v => System.Text.RegularExpressions.Regex.IsMatch(v, {Verbatim(pattern)}), \"in the format {Escape(pattern)}\");");
            }

            fields.Add(new Field(column, property, entityType, inputType, apply, config, checks, ignore));
        }

        return fields;
    }

    private static string Verbatim(string value) => "@\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string Xml(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);
}
