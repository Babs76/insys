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
using System.Text.Json;
using System.Text.Json.Nodes;
using Insys.BuildingBlocks.Api;
using Insys.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// The three OpenAPI documents (core, static-data, platform-operator) are contracts. Each must match its snapshot in
/// docs/api; to accept a change on purpose, run the tests with INSYS_UPDATE_OPENAPI=1 and commit the updated files.
/// </summary>
public sealed class OpenApiContractTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    public static TheoryData<string> Documents => new(ApiDocuments.All.Select(d => d.Name));

    private InsysApiFactory _factory = null!;

    public async ValueTask InitializeAsync() => _factory = await fixture.Host();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The static data document has over 1 400 paths; building it takes longer than HttpClient's default 100 seconds on CI runners.</summary>
    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(10);
        return client;
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public async Task OpenApiDocument_MatchesItsCommittedSnapshot(string document)
    {
        var current = Normalise(await Client().GetStringAsync($"/openapi/{document}.json"));
        if (Environment.GetEnvironmentVariable("INSYS_UPDATE_OPENAPI") == "1")
        {
            await File.WriteAllTextAsync(Snapshot(document), current);
        }

        File.Exists(Snapshot(document)).ShouldBeTrue($"{Snapshot(document)} is missing; run the tests once with INSYS_UPDATE_OPENAPI=1.");
        Normalise(await File.ReadAllTextAsync(Snapshot(document))).ShouldBe(current,
            $"The {document} API contract changed. If intended, run with INSYS_UPDATE_OPENAPI=1 and commit {Snapshot(document)}.");
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public async Task BuildTimeOpenApiDocument_MatchesItsCommittedSnapshot(string document)
    {
        var generated = Path.Combine(RepositoryPaths.Root, "src", "Host", "Insys.Api", "obj", "openapi", $"openapi_{document}.json");
        File.Exists(generated).ShouldBeTrue("Build the host first; it writes obj/openapi/openapi_{document}.json.");

        Normalise(await File.ReadAllTextAsync(generated)).ShouldBe(Normalise(await File.ReadAllTextAsync(Snapshot(document))),
            $"The build-time {document} document differs from its snapshot.");
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public async Task OpenApiDocument_ListsVersionedPaths_WithBearerSecurity(string document)
    {
        using var json = JsonDocument.Parse(await Client().GetStringAsync($"/openapi/{document}.json"));

        json.RootElement.GetProperty("paths").EnumerateObject().ShouldNotBeEmpty();
        json.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).ShouldAllBe(p => p.StartsWith("/api/v1/"));
        json.RootElement.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("bearer", out _).ShouldBeTrue();
    }

    /// <summary>GAP-005: the ProblemDetails extensions, and each error status as one shared response with its headers.</summary>
    [Theory]
    [MemberData(nameof(Documents))]
    public async Task OpenApiDocument_DeclaresTheErrorContract_GAP005(string document)
    {
        using var json = JsonDocument.Parse(await Client().GetStringAsync($"/openapi/{document}.json"));
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
        using var core = JsonDocument.Parse(await Client().GetStringAsync("/openapi/core.json"));
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
        using var core = JsonDocument.Parse(await Client().GetStringAsync("/openapi/core.json"));
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
        using var core = JsonDocument.Parse(await Client().GetStringAsync("/openapi/core.json"));
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
        using var core = JsonDocument.Parse(await Client().GetStringAsync("/openapi/core.json"));
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

    /// <summary>
    /// Riders BR-023 and BR-038: an audit event's actorType is an enum (USER, SERVICE); appointing an assessor, creating a
    /// quote and drafting an endorsement declare what follows them asynchronously.
    /// </summary>
    [Fact]
    public async Task AuditActorsAndAsynchronousEffects_AreDeclared_BR023_BR038()
    {
        using var core = JsonDocument.Parse(await Client().GetStringAsync("/openapi/core.json"));
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
        using var core = JsonDocument.Parse(await Client().GetStringAsync("/openapi/core.json"));
        var parameter = core.RootElement.GetProperty("paths").GetProperty("/api/v1/documents/documents").GetProperty("get").GetProperty("parameters")
            .EnumerateArray().Single(p => p.GetProperty("name").GetString() == "businessRefType");

        parameter.GetProperty("x-known-values").EnumerateArray().Select(v => v.GetString())
            .ShouldBe(["GENERAL_POLICY", "GENERAL_ENDORSEMENT", "LIFE_POLICY", "HEALTH_REMITTANCE"]);
    }

    [Fact]
    public async Task CoreDocument_HasNoStaticDataOperations()
    {
        using var core = JsonDocument.Parse(await Client().GetStringAsync("/openapi/core.json"));

        // Tenancy is the platform operator's, except a tenant's own staff reading their tenant's readiness (BR-012).
        core.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name)
            .ShouldAllBe(p => !p.Contains("/static/") && !p.Contains("/static-data/") && (!p.StartsWith("/api/v1/tenancy/") || p == "/api/v1/tenancy/readiness"));
    }

    [Fact]
    public async Task EveryEndpoint_IsInExactlyOneDocument()
    {
        var client = Client();
        var operations = 0;
        foreach (var (name, _, _) in ApiDocuments.All)
        {
            using var json = JsonDocument.Parse(await client.GetStringAsync($"/openapi/{name}.json"));
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
