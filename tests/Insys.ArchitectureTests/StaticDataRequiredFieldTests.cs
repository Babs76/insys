/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : ArchitectureTests
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
using System.Reflection;
using System.Text.Json;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Testing;

namespace Insys.ArchitectureTests;

/// <summary>
/// A static data field's mandatory flag in the catalogue (docs/specs/static-data-catalogue*.json), the API's validation
/// and the OpenAPI document's required list say the same thing (BR-084, BR-085). The generator takes the flag from the
/// catalogue; these tests fail when the catalogue changes without regenerating, a field escapes the catalogue, or the
/// contract drifts from the validation.
/// </summary>
public sealed class StaticDataRequiredFieldTests
{
    /// <summary>The record model's own fields, merged into the common columns (V0002's header); code and changeReason are always required.</summary>
    private static readonly HashSet<string> CommonFields = ["code", "version", "effective_from", "effective_to", "description", "approved_by"];

    /// <summary>Catalogue fields stored under another column name (reserved words, V0002's header).</summary>
    private static readonly Dictionary<string, string> Renamed = new() { ["end"] = "end_date", ["limit"] = "limit_value", ["window"] = "window_duration", ["start"] = "start_date" };

    /// <summary>Status columns V0012 deprecated (ADR-010): record_status, or an aggregate, holds the state, so no input carries them.</summary>
    private static readonly HashSet<string> Deprecated =
    [
        "product.cfg_product_definitions_and_versions.status",
        "integration.cfg_api_catalogue_entries.status",
        "integration.cfg_event_schema_versions.status",
        "finance.cfg_accounting_calendar_and_periods.status",
    ];

    private static readonly string[] Always = ["changeReason", "code"];

    /// <summary>Tables an aggregate writes through its own operations, so the contract has no static data input for them (the generator's AggregateManagedTables).</summary>
    private static readonly HashSet<string> AggregateManaged = ["product.cfg_product_definitions_and_versions"];

    public static TheoryData<string> Tables()
    {
        var data = new TheoryData<string>();
        foreach (var descriptor in Descriptors().Values.OrderBy(d => d.Schema + "." + d.Table, StringComparer.Ordinal))
        {
            data.Add($"{descriptor.Schema}.{descriptor.Table}");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public void TheFieldsTheApiRequires_AreTheCataloguesMandatoryFields_BR084_BR085(string table)
    {
        var descriptor = Descriptors()[table];
        var fields = Catalogue()[table];
        var inputFields = InputFields(descriptor.InputType);

        var undeclared = inputFields.Except(fields.Keys.Where(f => !CommonFields.Contains(f) && !Deprecated.Contains($"{table}.{f}")).Select(f => Json(descriptor, f)))
            .Order(StringComparer.Ordinal).ToList();
        undeclared.ShouldBeEmpty($"{table}: fields the API takes that the catalogue does not list");
        var unknown = fields.Keys.Where(f => !CommonFields.Contains(f) && !Deprecated.Contains($"{table}.{f}") && !inputFields.Contains(Json(descriptor, f))).ToList();
        unknown.ShouldBeEmpty($"{table}: catalogue fields the API does not take");

        var mandatory = fields.Where(f => f.Value && !CommonFields.Contains(f.Key) && !Deprecated.Contains($"{table}.{f.Key}")).Select(f => Json(descriptor, f.Key))
            .Concat(Always).Order(StringComparer.Ordinal).ToList();
        Required(descriptor).ShouldBe(mandatory, $"{table}: the fields the API refuses to go without, against the catalogue's mandatory fields");
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public void TheOpenApiInput_ListsAsRequired_ExactlyTheFieldsTheApiRequires_BR084_BR085(string table)
    {
        if (AggregateManaged.Contains(table))
        {
            return;
        }

        var descriptor = Descriptors()[table];
        // Schema names carry the module's name once (SchemaNames): CaseTypesInput is WorkflowCaseTypesInput, RatingTablesInput stays.
        var module = descriptor.InputType.Namespace!.Split('.')[1];
        var name = descriptor.InputType.Name.StartsWith(module, StringComparison.Ordinal) ? descriptor.InputType.Name : module + descriptor.InputType.Name;

        // Tenants write their tables through the static data document; platform-global tables only the operator writes.
        var schema = OpenApi().Select(d => d.RootElement.GetProperty("components").GetProperty("schemas"))
            .Select(s => s.TryGetProperty(name, out var found) ? found : (JsonElement?)null).FirstOrDefault(s => s is not null);
        schema.ShouldNotBeNull($"{name} is in neither docs/api/openapi-static-data.json nor openapi-platform-operator.json");
        var listed = schema.Value.TryGetProperty("required", out var required) ? required.EnumerateArray().Select(r => r.GetString()!).Order(StringComparer.Ordinal).ToList() : [];
        listed.ShouldBe(Required(descriptor), $"{name}: the contract's required list against the API's validation");
    }

    /// <summary>The fields an empty input is refused for, as "… is required": what the API actually requires.</summary>
    private static List<string> Required(StaticDataDescriptor descriptor)
    {
        var input = (StaticRecordInput)Activator.CreateInstance(descriptor.InputType)!;
        return input.Validate().Where(e => e.Value.Contains($"{e.Key} is required.")).Select(e => e.Key).Order(StringComparer.Ordinal).ToList();
    }

    private static HashSet<string> InputFields(Type input) =>
        input.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).ToHashSet();

    /// <summary>The JSON name the generator gives a catalogue field: Pascal case, with Value added where it would repeat the class name.</summary>
    private static string Json(StaticDataDescriptor descriptor, string field)
    {
        var property = string.Concat(Renamed.GetValueOrDefault(field, field).Split('_').Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));
        if (property == descriptor.EntityType.Name)
        {
            property += "Value";
        }

        return JsonNamingPolicy.CamelCase.ConvertName(property);
    }

    private static Dictionary<string, StaticDataDescriptor> Descriptors() => Solution.Assemblies
        .SelectMany(a => a.GetTypes())
        .Where(t => t is { IsAbstract: true, IsSealed: true } && t.Name.EndsWith("StaticData", StringComparison.Ordinal))
        .Select(t => t.GetField("Descriptors", BindingFlags.Public | BindingFlags.Static)?.GetValue(null))
        .OfType<IReadOnlyList<StaticDataDescriptor>>()
        .SelectMany(d => d)
        .ToDictionary(d => $"{d.Schema}.{d.Table}");

    private static Dictionary<string, Dictionary<string, bool>> Catalogue() =>
        new[] { "static-data-catalogue.json", "static-data-catalogue-additions.json" }
            .SelectMany(file => JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Root, "docs", "specs", file))).RootElement.GetProperty("entities").EnumerateArray())
            .Where(e => e.TryGetProperty("fields", out _))
            .ToDictionary(e => e.GetProperty("table").GetString()!,
                e => e.GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("name").GetString()!, f => f.GetProperty("mandatory").GetBoolean()));

    private static readonly Lazy<JsonDocument[]> Documents = new(() => new[] { "openapi-static-data.json", "openapi-platform-operator.json" }
        .Select(file => JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Root, "docs", "api", file)))).ToArray());

    private static JsonDocument[] OpenApi() => Documents.Value;
}
