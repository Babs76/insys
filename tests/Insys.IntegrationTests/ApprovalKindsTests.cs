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
using Insys.BuildingBlocks.Api.Functions;
using Insys.BuildingBlocks.Contracts.Approvals;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// BR-005: the approval and reference types the modules declare are the contract's enums, and each reference type names
/// the screen that opens its record. Workflow refuses an approval of an undeclared kind, so the approval tables tests
/// (every type, through the real request) prove each module uses a declared value.
/// </summary>
public sealed class ApprovalKindsTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task EveryReferenceType_OpensAScreenInTheCatalogue_BR005()
    {
        await fixture.Host();
        var screens = FunctionRegistry.All().Where(f => f.Function.Kind == FunctionKind.Screen).Select(f => f.Function.Code).ToHashSet(StringComparer.Ordinal);
        ApprovalKinds.BusinessRefTypes.Count.ShouldBe(36); // + the static data import a discarded import's notice names (STATIC_DATA_IMPORT, OQ-203), + the three due diligence references (Wave 3 batch 2), General's renewal (Wave 4 §4.3), the SETUP change set (AIA-086), the Health remittance, the credit note request and the dead letter (DE-007, DE-008, DE-010), the static data version a discarded draft's notice names (DE-016, OQ-204), and the disclosure setting (DISCLOSURE_SETTING, OQ-201)
        ApprovalKinds.ApprovalTypes.Count.ShouldBe(33); // + PAYOUT_RETRY (DE-008, declared by several modules, counted once), COMMISSION_CLAWBACK_INVOICE (OQ-062), CREDIT_NOTE (DE-010), DEAD_LETTER_DISCARD (DE-007; Ade, 24 November 2026) and DISCLOSURE_SETTING_CHANGE (OQ-201; Ade, 26 November 2026)
        ApprovalKinds.BusinessRefTypes.Where(r => !screens.Contains(ApprovalKinds.ScreenFor(r)!)).ShouldBeEmpty();
    }

    /// <summary>
    /// DE-019, DE-020, DE-022: an approval says how to open its record, by the ID it carries: each declared reference type names a
    /// GET operation of this build, by its operationId (the endpoint name), whose route is the declared path with {id} as its only
    /// parameter (none for a record the tenant has one of). Its view gives subjectOperation and subjectPath.
    /// </summary>
    [Fact]
    public async Task EveryReferenceType_OpensItsRecordWithAReadOperationThatExists_DE020()
    {
        var host = await fixture.Host();
        _ = host.CreateClient();
        var gets = host.Services.GetServices<Microsoft.AspNetCore.Routing.EndpointDataSource>().SelectMany(s => s.Endpoints)
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IHttpMethodMetadata>()?.HttpMethods.Contains("GET") == true
                && e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IEndpointNameMetadata>() is not null)
            .ToDictionary(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IEndpointNameMetadata>()!.EndpointName, e => Normalise(e.RoutePattern.RawText!),
                StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var type in ApprovalKinds.BusinessRefTypes)
        {
            if (ApprovalKinds.ReadFor(type) is not { } read)
            {
                if (!ApprovalKinds.TypesOpenedByRecord.TryGetValue(type, out var how) || string.IsNullOrWhiteSpace(how))
                {
                    problems.Add($"{type}: no read operation declared (ApprovalKinds.OpensWith), nor a read by the record (ApprovalKinds.OpensByRecord)");
                }
            }
            else if (ApprovalKinds.TypesOpenedByRecord.ContainsKey(type))
            {
                problems.Add($"{type}: declared both with a read operation and with a read by the record");
            }
            else if (!gets.TryGetValue(read.OperationId, out var route))
            {
                problems.Add($"{type}: {read.OperationId} is not a GET operation of this build");
            }
            else if (route != Normalise(read.PathTemplate) || System.Text.RegularExpressions.Regex.Matches(read.PathTemplate, "{[^}]*}").Any(m => m.Value != SubjectRead.IdPlaceholder))
            {
                problems.Add($"{type}: {read.OperationId} is {route}, not {read.PathTemplate} with {{id}} its only parameter");
            }
        }

        problems.ShouldBeEmpty();
        // OQ-203: only the discarded static data draft and import are opened by their record (their task's subjectPath), tested in StaticDataTests.
        ApprovalKinds.TypesOpenedByRecord.Keys.Order(StringComparer.Ordinal).ShouldBe(["STATIC_DATA_IMPORT", "STATIC_DATA_VERSION"]);
        var id = Guid.CreateVersion7();
        ApprovalKinds.ReadFor("BANK_ACCOUNT")!.PathFor(id).ShouldBe($"/api/v1/party/bank-accounts/{id}");
    }

    /// <summary>A route's path with each parameter's name and constraints dropped: {id:guid} and {redressId:guid} are both {}.</summary>
    private static string Normalise(string route) =>
        System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(route, "^/api/v{[^}]*}/", "/api/v1/"), "{[^}]*}", "{}").TrimEnd('/');

    [Fact]
    public async Task TheContract_ListsTheDeclaredTypes_AndAnApprovalNamesItsScreen_BR005()
    {
        // The committed document: OpenApiContractTests checks it is the one the host serves, so this read needs no host.
        await fixture.Host();
        using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(RepositoryPaths.Root, "docs", "api", "openapi-core.json")));
        var view = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("WorkflowApprovalView").GetProperty("properties");
        view.GetProperty("approvalType").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ShouldBe(ApprovalKinds.ApprovalTypes);
        view.GetProperty("businessRefType").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ShouldBe(ApprovalKinds.BusinessRefTypes);
        view.TryGetProperty("screen", out _).ShouldBeTrue();
    }
}
