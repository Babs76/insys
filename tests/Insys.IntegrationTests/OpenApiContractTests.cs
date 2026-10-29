/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : IntegrationTests
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
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Insys.BuildingBlocks.Api;
using Insys.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// The three OpenAPI documents (core, static-data, platform-operator) are contracts. The build generates each from the code
/// (obj/openapi), and each must match its snapshot in docs/api; to accept a change on purpose, run the tests with
/// INSYS_UPDATE_OPENAPI=1, commit the updated files and build the host again. The host serves the committed copies, never
/// building them on request, and outside Development and Testing only to a signed-in user (Ade, 29 October 2026).
/// </summary>
public sealed partial class OpenApiContractTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    public static TheoryData<string> Documents => new(ApiDocuments.All.Select(d => d.Name));

    private InsysApiFactory _factory = null!;

    public async ValueTask InitializeAsync() => _factory = await fixture.Host();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The document the build generated from the code (obj/openapi): what the contract tests inspect.</summary>
    private static async Task<string> Generated(string document)
    {
        var generated = Path.Combine(RepositoryPaths.Root, "src", "Host", "Insys.Api", "obj", "openapi", $"openapi_{document}.json");
        File.Exists(generated).ShouldBeTrue($"Build the host first; it writes obj/openapi/openapi_{document}.json.");
        return await File.ReadAllTextAsync(generated);
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public async Task OpenApiDocument_MatchesItsCommittedSnapshot(string document)
    {
        var current = Normalise(await Generated(document));
        if (Environment.GetEnvironmentVariable("INSYS_UPDATE_OPENAPI") == "1")
        {
            await File.WriteAllTextAsync(Snapshot(document), current);
        }

        File.Exists(Snapshot(document)).ShouldBeTrue($"{Snapshot(document)} is missing; run the tests once with INSYS_UPDATE_OPENAPI=1.");
        Normalise(await File.ReadAllTextAsync(Snapshot(document))).ShouldBe(current,
            $"The {document} API contract changed. If intended, run with INSYS_UPDATE_OPENAPI=1, commit {Snapshot(document)} and build the host again.");
    }

    /// <summary>The host serves the committed copy as it is, built into the host, never generated on request (Ade, 29 October 2026).</summary>
    [Theory]
    [MemberData(nameof(Documents))]
    public async Task TheServedDocument_IsTheCommittedCopy(string document)
    {
        var served = await _factory.CreateClient().GetAsync($"/openapi/{document}.json");
        served.StatusCode.ShouldBe(HttpStatusCode.OK, "in Testing, as in Development, anyone may read it");
        served.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        (await served.Content.ReadAsStringAsync()).ShouldBe(await File.ReadAllTextAsync(Snapshot(document)),
            "the host serves docs/api as committed; build the host again after updating it");
        (await _factory.CreateClient().GetAsync("/openapi/other.json")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Ade, 29 October 2026: in production the API description is not public. A host outside Development and Testing serves
    /// none of the documents without sign-in; here no token is accepted, so every request is refused.
    /// </summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task OutsideDevelopmentAndTesting_NoDocumentIsServed_WithoutSignIn(string environment)
    {
        await using var factory = new InsysApiFactory(fixture.Database,
            new Dictionary<string, string?>
            {
                ["Authentication:DevelopmentSigningKey"] = "", ["Testing:DailyJobRunner:Enabled"] = "false",
                ["Identity:PermissionCache:AllowMultipleInstances"] = "true", // the class's shared host runs on the same database
            }, environment);
        var anonymous = factory.CreateClient();
        foreach (var (name, _, _) in ApiDocuments.All)
        {
            (await anonymous.GetAsync($"/openapi/{name}.json")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, name);
        }
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public async Task OpenApiDocument_ListsVersionedPaths_WithBearerSecurity(string document)
    {
        using var json = JsonDocument.Parse(await Generated(document));

        json.RootElement.GetProperty("paths").EnumerateObject().ShouldNotBeEmpty();
        json.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).ShouldAllBe(p => p.StartsWith("/api/v1/"));
        json.RootElement.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("bearer", out _).ShouldBeTrue();
    }

    /// <summary>GAP-005: the ProblemDetails extensions, and each error status as one shared response with its headers.</summary>
    [Theory]
    [MemberData(nameof(Documents))]
    public async Task OpenApiDocument_DeclaresTheErrorContract_GAP005(string document)
    {
        using var json = JsonDocument.Parse(await Generated(document));
        var components = json.RootElement.GetProperty("components");

        var problem = components.GetProperty("schemas").GetProperty("ProblemDetails").GetProperty("properties");
        foreach (var extension in new[] { "code", "errors", "correlationId", "traceId", "detailLanguage" })
        {
            problem.TryGetProperty(extension, out _).ShouldBeTrue(extension);
        }

        var responses = components.GetProperty("responses");
        responses.GetProperty("Problem429").GetProperty("headers").TryGetProperty("Retry-After", out _).ShouldBeTrue();
        responses.GetProperty("Problem503").GetProperty("headers").TryGetProperty("Retry-After", out _).ShouldBeTrue();
        responses.EnumerateObject().Where(r => !r.Value.GetProperty("headers").TryGetProperty("X-Correlation-Id", out _)).ShouldBeEmpty();
        json.RootElement.GetProperty("paths").EnumerateObject().SelectMany(p => p.Value.EnumerateObject())
            .SelectMany(o => o.Value.GetProperty("responses").EnumerateObject())
            .Where(r => r.Name[0] is '4' or '5' && !r.Value.TryGetProperty("$ref", out _)).ShouldBeEmpty();
    }

    /// <summary>GAP-031: every module type is named with its module, so same-named types get a schema each; GAP-008: state fields are enums.</summary>
    [Fact]
    public async Task CoreDocument_KeepsSameNamedModuleTypesApart_AndDeclaresStateEnums_GAP008()
    {
        using var core = JsonDocument.Parse(await Generated("core"));
        var schemas = core.RootElement.GetProperty("components").GetProperty("schemas");

        foreach (var name in new[] { "LifeProposalStatus", "GeneralProposalStatus", "LifeProposalView", "GeneralProposalView" })
        {
            schemas.TryGetProperty(name, out _).ShouldBeTrue(name);
        }

        schemas.GetProperty("LifeProposalStatus").GetProperty("enum").EnumerateArray().Select(v => v.GetString()).ShouldContain("InUnderwriting");
        schemas.GetProperty("WorkflowApprovalView").GetProperty("properties").GetProperty("status").GetProperty("$ref").GetString().ShouldBe("#/components/schemas/WorkflowApprovalStatus");
        schemas.GetProperty("PartyBankAccountView").GetProperty("properties").GetProperty("status").GetProperty("enum").EnumerateArray()
            .Select(v => v.GetString()).ShouldBe(["PENDING_VERIFICATION", "ACTIVE", "SUPERSEDED", "REJECTED"]);
    }

    /// <summary>
    /// BR-026: a party's kind is the summary's enum, its identifiers carry a masked number, each coded field names its static
    /// data table (with the list, for a table of several), and the search log's from and to are described as instants.
    /// </summary>
    [Fact]
    public async Task PartyContract_DeclaresKindMaskedNumbersCodeListsAndInstants_BR026()
    {
        using var core = JsonDocument.Parse(await Generated("core"));
        var schemas = core.RootElement.GetProperty("components").GetProperty("schemas");
        string Property(string schema, string property, string extension) =>
            schemas.GetProperty(schema).GetProperty("properties").GetProperty(property).GetProperty(extension).GetString()!;

        schemas.GetProperty("PartyView").GetProperty("properties").GetProperty("kind").ToString()
            .ShouldBe(schemas.GetProperty("PartySummary").GetProperty("properties").GetProperty("kind").ToString());
        schemas.GetProperty("PartyIdentifierView").GetProperty("properties").TryGetProperty("maskedNumber", out _).ShouldBeTrue();
        foreach (var (schema, property, table) in new[]
                 {
                     ("PartyIdentifierView", "docType", "party.ref_identity_document_types"), ("PartyIdentifierView", "country", "refdata.ref_countries"),
                     ("PartyAddressView", "country", "refdata.ref_countries"), ("PartyAddressView", "subdivision", "refdata.ref_country_subdivisions"),
                     ("PartyContactPointView", "channelType", "party.ref_contact_channel_types"), ("PartyRoleView", "roleCode", "party.ref_party_role_types"),
                     ("PartyRelationshipView", "relationshipCode", "party.ref_relationship_types"), ("PartyConsentView", "purposeCode", "party.cfg_consent_purposes"),
                     ("PartyIndividualData", "nationality", "refdata.ref_countries"), ("PartyIndividualData", "preferredLanguage", "refdata.ref_languages"),
                     ("PartyIndividualData", "maritalStatusCode", "party.ref_personal_attribute_code_lists"),
                     ("PartyOrganisationData", "legalFormCode", "party.ref_party_types_and_legal_forms"),
                 })
        {
            Property(schema, property, "x-code-list").ShouldBe(table, $"{schema}.{property}");
        }

        Property("PartyIndividualData", "maritalStatusCode", "x-code-list-filter").ShouldBe("list_code=MARITAL_STATUS");
        var parameters = core.RootElement.GetProperty("paths").GetProperty("/api/v1/party/search-log").GetProperty("get").GetProperty("parameters").EnumerateArray().ToList();
        foreach (var name in new[] { "from", "to" })
        {
            var parameter = parameters.Single(p => p.GetProperty("name").GetString() == name);
            parameter.GetProperty("schema").GetProperty("format").GetString().ShouldBe("date-time");
            parameter.GetProperty("description").GetString()!.ShouldContain("An instant");
        }
    }

    /// <summary>
    /// BR-027: delivery channels are the enum the channel settings allow, legal hold reasons name their code list, the
    /// document view says what reading needs and gives the file name, and the purpose parameter lists the classifications
    /// that need one.
    /// </summary>
    [Fact]
    public async Task DocumentsContract_DeclaresChannelsHoldReasonsAndWhatReadingNeeds_BR027()
    {
        using var core = JsonDocument.Parse(await Generated("core"));
        var schemas = core.RootElement.GetProperty("components").GetProperty("schemas");
        JsonElement Property(string schema, string property) => schemas.GetProperty(schema).GetProperty("properties").GetProperty(property);

        Property("DocumentsDeliverDocumentRequest", "channel").GetProperty("enum").EnumerateArray().Select(v => v.GetString()).ShouldBe(["EMAIL", "SMS", "POST", "PORTAL"]);
        Property("DocumentsDeliverDocumentRequest", "channel").GetProperty("x-code-list").GetString().ShouldBe("document.cfg_delivery_channel_settings");
        foreach (var schema in new[] { "DocumentsLegalHoldRequest", "DocumentsRecordHoldRequest", "DocumentsLegalHoldView", "DocumentsRecordHoldView" })
        {
            Property(schema, "reasonCode").GetProperty("x-code-list").GetString().ShouldBe("document.ref_legal_hold_reason_codes", schema);
        }

        foreach (var property in new[] { "purposeRequired", "allowedRoleRequired", "fileName" })
        {
            schemas.GetProperty("DocumentsDocumentView").GetProperty("properties").TryGetProperty(property, out _).ShouldBeTrue(property);
        }

        schemas.GetProperty("DocumentsStoreDocumentRequest").GetProperty("properties").TryGetProperty("fileName", out _).ShouldBeTrue();
        var purpose = core.RootElement.GetProperty("paths").GetProperty("/api/v1/documents/documents/{id}/content").GetProperty("get").GetProperty("parameters")
            .EnumerateArray().Single(p => p.GetProperty("name").GetString() == "purpose").GetProperty("description").GetString()!;
        foreach (var classification in Insys.Documents.Domain.Documents.Classifications.Sensitive)
        {
            purpose.ShouldContain(classification);
        }
    }

    /// <summary>
    /// BR-036 (the front end's GAP-034): every General and claims code field the front end listed declares its values (an
    /// enum, or x-known-values for an open field) or names the static data table its codes come from (x-code-list). A
    /// claim party's role comes from the tenant's claim party roles (OQ-122).
    /// </summary>
    [Fact]
    public async Task GeneralAndClaimsCodeFields_DeclareTheirValuesOrTable_BR036()
    {
        string[] codes =
        [
            "billingFrequency", "collectionMethod", "reasonCode", "decisionCode", "endorsementType", "objectType", "valuationBasis", "riskZone", "refundBasis",
            "coverStartsOn", "causeCode", "perilCode", "catEventCode", "reserveType", "recoveryType", "disposalMethod", "evidenceType", "channel", "domain",
            "movementType",
        ];
        using var core = JsonDocument.Parse(await Generated("core"));
        var undeclared = new List<string>();
        var checkedFields = 0;
        foreach (var schema in core.RootElement.GetProperty("components").GetProperty("schemas").EnumerateObject()
                     .Where(s => s.Name.StartsWith("General", StringComparison.Ordinal) || s.Name.StartsWith("Claims", StringComparison.Ordinal)))
        {
            if (!schema.Value.TryGetProperty("properties", out var properties))
            {
                continue;
            }

            foreach (var property in properties.EnumerateObject().Where(p => codes.Contains(p.Name)))
            {
                checkedFields++;
                var declared = property.Value.TryGetProperty("enum", out _) || property.Value.TryGetProperty("$ref", out _)
                               || property.Value.TryGetProperty("x-known-values", out _) || property.Value.TryGetProperty("x-code-list", out _);
                if (!declared)
                {
                    undeclared.Add($"{schema.Name}.{property.Name}");
                }
            }
        }

        checkedFields.ShouldBeGreaterThan(50);
        undeclared.ShouldBeEmpty();
        var schemas = core.RootElement.GetProperty("components").GetProperty("schemas");
        foreach (var name in new[] { "GeneralPartyRequest", "GeneralClaimPartyView" })
        {
            schemas.GetProperty(name).GetProperty("properties").GetProperty("role").GetProperty("x-code-list").GetString().ShouldBe("general.cfg_claim_party_roles", name);
        }
    }

    /// <summary>BR-056: every Billing code field declares its values (an enum or known values) or names its static data table, and the arrears view its currency.</summary>
    [Fact]
    public async Task BillingCodeFields_DeclareTheirValuesOrTable_BR056()
    {
        string[] codes = ["collectionMethod", "frequency", "sourceDomain", "reasonCode", "bucketCode", "nextAction", "chargeType", "documentType", "status"];
        using var core = JsonDocument.Parse(await Generated("core"));
        var undeclared = new List<string>();
        var checkedFields = 0;
        foreach (var schema in core.RootElement.GetProperty("components").GetProperty("schemas").EnumerateObject().Where(s => s.Name.StartsWith("Billing", StringComparison.Ordinal)))
        {
            if (!schema.Value.TryGetProperty("properties", out var properties))
            {
                continue;
            }

            foreach (var property in properties.EnumerateObject().Where(p => codes.Contains(p.Name)))
            {
                checkedFields++;
                var declared = property.Value.TryGetProperty("enum", out _) || property.Value.TryGetProperty("$ref", out _)
                               || property.Value.TryGetProperty("x-known-values", out _) || property.Value.TryGetProperty("x-code-list", out _);
                if (!declared)
                {
                    undeclared.Add($"{schema.Name}.{property.Name}");
                }
            }
        }

        checkedFields.ShouldBeGreaterThan(10);
        undeclared.ShouldBeEmpty();
        core.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("BillingArrearsView").GetProperty("properties").TryGetProperty("currency", out _)
            .ShouldBeTrue();
    }

    /// <summary>
    /// Riders BR-023 and BR-038: an audit event's actorType is an enum (USER, SERVICE); appointing an assessor, creating a
    /// quote and drafting an endorsement declare what follows them asynchronously.
    /// </summary>
    [Fact]
    public async Task AuditActorsAndAsynchronousEffects_AreDeclared_BR023_BR038()
    {
        using var core = JsonDocument.Parse(await Generated("core"));
        core.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("AuditEventView").GetProperty("properties").GetProperty("actorType")
            .GetProperty("enum").EnumerateArray().Select(v => v.GetString()).ShouldBe(["USER", "SERVICE"]);
        var operations = core.RootElement.GetProperty("paths").EnumerateObject().SelectMany(p => p.Value.EnumerateObject())
            .Where(o => o.Value.TryGetProperty("operationId", out _)).ToDictionary(o => o.Value.GetProperty("operationId").GetString()!, o => o.Value);
        foreach (var operation in new[] { "GeneralAppointAssessor", "GeneralCreateQuote", "GeneralDraftEndorsement", "GeneralIssuePolicy" })
        {
            operations[operation].GetProperty("x-eventual-effects").GetString().ShouldNotBeNullOrWhiteSpace(operation);
        }
    }

    /// <summary>GAP-033: the business reference types INSYS files documents under are declared on the documents list.</summary>
    [Fact]
    public async Task DocumentsList_DeclaresTheKnownBusinessReferenceTypes_GAP033()
    {
        using var core = JsonDocument.Parse(await Generated("core"));
        var parameter = core.RootElement.GetProperty("paths").GetProperty("/api/v1/documents/documents").GetProperty("get").GetProperty("parameters")
            .EnumerateArray().Single(p => p.GetProperty("name").GetString() == "businessRefType");

        parameter.GetProperty("x-known-values").EnumerateArray().Select(v => v.GetString())
            .ShouldBe(["GENERAL_POLICY", "GENERAL_ENDORSEMENT", "LIFE_POLICY", "HEALTH_REMITTANCE"]);
    }

    [Fact]
    public async Task CoreDocument_HasNoStaticDataOperations()
    {
        using var core = JsonDocument.Parse(await Generated("core"));

        // Tenancy is the platform operator's, except a tenant's own staff reading their tenant's readiness (BR-012).
        core.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name)
            .ShouldAllBe(p => !p.Contains("/static/") && !p.Contains("/static-data/") && (!p.StartsWith("/api/v1/tenancy/") || p == "/api/v1/tenancy/readiness"));
    }

    [Fact]
    public async Task EveryEndpoint_IsInExactlyOneDocument()
    {
        var operations = 0;
        foreach (var (name, _, _) in ApiDocuments.All)
        {
            using var json = JsonDocument.Parse(await Generated(name));
            operations += json.RootElement.GetProperty("paths").EnumerateObject().Sum(p => p.Value.EnumerateObject().Count(o => o.Name != "parameters"));
        }

        // The test-only daily job runner (ADR-025) is mapped in Testing only and kept out of every document.
        operations.ShouldBe(ApiEndpoints().Count(e => e.RoutePattern.RawText?.Contains("/testing/", StringComparison.Ordinal) != true));
    }

    [Fact]
    public void EveryApiEndpoint_DeclaresAPermissionPolicy_IAM007()
    {
        var endpoints = ApiEndpoints();

        endpoints.ShouldNotBeEmpty();
        endpoints.Where(e => e.Metadata.GetMetadata<RequiredPermissionMetadata>() is null).Select(e => e.DisplayName).ShouldBeEmpty();
    }

    /// <summary>
    /// BR-105: a code field that names its static data table (x-code-list) also names the column holding its value
    /// (x-code-list-column), and the table has that column, and the filter's column, in the database.
    /// </summary>
    [Fact]
    public async Task CodeListFields_NameAValueColumn_ThatExistsOnTheirTable_BR105()
    {
        var problems = new List<string>();
        var declared = 0;
        foreach (var document in ApiDocuments.All.Select(d => d.Name))
        {
            using var json = JsonDocument.Parse(await Generated(document));
            foreach (var (path, field) in CodeListFields(json.RootElement, document))
            {
                declared++;
                var table = field.GetProperty("x-code-list").GetString()!;
                if (!field.TryGetProperty("x-code-list-column", out var column) || string.IsNullOrEmpty(column.GetString()))
                {
                    problems.Add($"{path}: {table} names no column");
                    continue;
                }

                var columns = await Columns(table);
                if (columns.Count == 0)
                {
                    problems.Add($"{path}: {table} is not a table");
                }
                else if (!columns.Contains(column.GetString()!))
                {
                    problems.Add($"{path}: {table} has no column {column.GetString()}");
                }

                if (field.TryGetProperty("x-code-list-filter", out var filter) && !columns.Contains(filter.GetString()!.Split('=')[0]))
                {
                    problems.Add($"{path}: {table} has no filter column {filter.GetString()}");
                }
            }
        }

        declared.ShouldBeGreaterThan(130);
        problems.ShouldBeEmpty();
    }

    /// <summary>
    /// BR-105 (Ade, 29 October 2026): a code-named string field (…Code, …Type and their plurals) in the core and
    /// platform-operator documents declares its values (an enum, a reference, x-known-values) or its code list, or is on
    /// the reviewed list docs/api/code-field-allowances.tsv with its reason ("to declare" until a wave declares it). An
    /// entry that is declared, or gone, comes off the list; the list is empty by the hardening phase.
    /// </summary>
    [Fact]
    public async Task UndeclaredCodeFields_AreOnTheReviewedAllowanceList_BR105()
    {
        var file = Path.Combine(RepositoryPaths.Root, "docs", "api", "code-field-allowances.tsv");
        var entries = (await File.ReadAllLinesAsync(file)).Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split('\t')).ToList();
        entries.Where(e => e.Length != 3 || string.IsNullOrWhiteSpace(e[2])).Select(e => string.Join(" ", e)).ShouldBeEmpty("Each entry is document, Schema.property and its reason.");
        var allowed = entries.Select(e => $"{e[0]} {e[1]}").ToHashSet();

        var undeclared = new HashSet<string>();
        foreach (var document in new[] { "core", "platform-operator" })
        {
            using var json = JsonDocument.Parse(await Generated(document));
            foreach (var schema in json.RootElement.GetProperty("components").GetProperty("schemas").EnumerateObject())
            {
                if (!schema.Value.TryGetProperty("properties", out var properties))
                {
                    continue;
                }

                foreach (var property in properties.EnumerateObject().Where(p => CodeNamed().IsMatch(p.Name) && Undeclared(p.Value)))
                {
                    undeclared.Add($"{document} {schema.Name}.{property.Name}");
                }
            }
        }

        undeclared.Except(allowed).Order().ShouldBeEmpty("Declare the field's code list or values, or add it to docs/api/code-field-allowances.tsv with its reason.");
        allowed.Except(undeclared).Order().ShouldBeEmpty("These are declared now, or gone: take them off docs/api/code-field-allowances.tsv.");
    }

    [System.Text.RegularExpressions.GeneratedRegex("[a-z](Code|Codes|Type|Types)$")]
    private static partial System.Text.RegularExpressions.Regex CodeNamed();

    /// <summary>A string field (or list of strings) that declares neither its values nor its code list.</summary>
    private static bool Undeclared(JsonElement field)
    {
        string[] declaring = ["enum", "$ref", "x-known-values", "x-code-list", "allOf", "oneOf"];
        var items = field.TryGetProperty("items", out var i) ? i : default;
        if (declaring.Any(k => field.TryGetProperty(k, out _)) || (items.ValueKind == JsonValueKind.Object && declaring.Any(k => items.TryGetProperty(k, out _))))
        {
            return false;
        }

        static bool IsString(JsonElement e) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("type", out var t) && t.GetRawText().Contains("\"string\"", StringComparison.Ordinal);
        return IsString(field) || IsString(items);
    }

    /// <summary>Every object in the document carrying x-code-list, with where it is.</summary>
    private static IEnumerable<(string Path, JsonElement Field)> CodeListFields(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("x-code-list", out _))
            {
                yield return (path, element);
            }

            foreach (var child in element.EnumerateObject())
            {
                foreach (var found in CodeListFields(child.Value, $"{path}/{child.Name}"))
                {
                    yield return found;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var child in element.EnumerateArray())
            {
                foreach (var found in CodeListFields(child, $"{path}/{index++}"))
                {
                    yield return found;
                }
            }
        }
    }

    private readonly Dictionary<string, HashSet<string>> _columns = [];

    private async Task<HashSet<string>> Columns(string table)
    {
        if (!_columns.TryGetValue(table, out var columns))
        {
            var (schema, name) = (table.Split('.')[0], table.Split('.')[1]);
            columns = [.. (await fixture.Database.ScalarAsOwner<string>(
                "SELECT string_agg(column_name, ',') FROM information_schema.columns WHERE table_schema = @s AND table_name = @t", ("s", schema), ("t", name)) ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)];
            _columns[table] = columns;
        }

        return columns;
    }

    private List<RouteEndpoint> ApiEndpoints()
    {
        _ = _factory.CreateClient();
        return _factory.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/", StringComparison.Ordinal) == true)
            .ToList();
    }

    private static string Snapshot(string document) => Path.Combine(RepositoryPaths.Root, "docs", "api", $"openapi-{document}.json");

    private static string Normalise(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
}
