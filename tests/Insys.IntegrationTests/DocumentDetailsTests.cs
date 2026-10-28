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
using Insys.Documents.Contracts;

namespace Insys.IntegrationTests;

/// <summary>
/// BR-027: an uploaded document keeps its file name (without the path a browser sends), and a document's view says whether
/// reading its content needs a purpose (DOC-003) or an allowed role (DOC-004), by its classification.
/// </summary>
public sealed class DocumentDetailsTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly Guid T = DatabaseFixture.TenantA;
    private InsysApiFactory _factory = null!;
    private HttpClient _clerk = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        foreach (var (code, classification) in new[] { ("BR27_PUBLIC", "PUBLIC"), ("BR27_NOTE", "PERSONAL"), ("BR27_MEDICAL", "HIGHLY_SENSITIVE") })
        {
            await fixture.Database.SeedStatic(T, "document.ref_document_categories", "ACTIVE", code, TestClock.Today.AddDays(-1), ("category_code", code),
                ("label", code), ("classification", classification), ("domain", "ALL"), ("allowed_roles", Array.Empty<string>()));
        }

        await fixture.Database.UserWithPermissions(T, "br027-clerk", DocumentPermissions.DocumentCreate, DocumentPermissions.DocumentRead);
        _clerk = _factory.ClientFor(T, "br027-clerk");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task AnUpload_KeepsItsFileName_AndTheViewSaysWhatReadingItNeeds_BR027()
    {
        async Task<HttpResponseMessage> Upload(string category, string? fileName) => await _clerk.Send(HttpMethod.Post, "/api/v1/documents/documents", new
        {
            categoryCode = category, businessRefType = "PARTY", businessRefId = Guid.CreateVersion7(), mimeType = "application/pdf",
            contentBase64 = Convert.ToBase64String("%PDF-1.7"u8.ToArray()), fileName,
        }, idempotent: true);

        var stored = await Upload("BR27_NOTE", @"C:\fakepath\claim form.pdf");
        stored.StatusCode.ShouldBe(HttpStatusCode.Created, await stored.Content.ReadAsStringAsync());
        var id = (await stored.Json()).GetProperty("id").GetGuid();
        var view = await (await _clerk.GetAsync($"/api/v1/documents/documents/{id}")).Json();
        view.GetProperty("fileName").GetString().ShouldBe("claim form.pdf", "the path the browser sent is dropped");
        (view.GetProperty("purposeRequired").GetBoolean(), view.GetProperty("allowedRoleRequired").GetBoolean()).ShouldBe((true, false));

        var medical = await (await Upload("BR27_MEDICAL", "scan.pdf")).Json();
        (medical.GetProperty("purposeRequired").GetBoolean(), medical.GetProperty("allowedRoleRequired").GetBoolean()).ShouldBe((true, true));
        var open = await (await Upload("BR27_PUBLIC", null)).Json();
        (open.GetProperty("purposeRequired").GetBoolean(), open.GetProperty("allowedRoleRequired").GetBoolean()).ShouldBe((false, false));
        open.GetProperty("fileName").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null, "a name is kept only when one is sent");

        (await Upload("BR27_NOTE", "bad\u0007name.pdf")).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Upload("BR27_NOTE", new string('n', 252) + ".pdf")).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Upload("BR27_NOTE", "folder/")).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, "a path with no name");
    }
}
