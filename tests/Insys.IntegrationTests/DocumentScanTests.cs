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
using Insys.Testing;
using Shouldly;
using Xunit;

namespace Insys.IntegrationTests;

/// <summary>Wave 7, batch 7.4: every upload is held in quarantine until the malware scan clears it, and waits while the scanner is down (AIA-010, D-14).</summary>
public sealed class DocumentScanTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly Guid T = DatabaseFixture.TenantA;
    private InsysApiFactory _factory = null!;
    private HttpClient _clerk = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        await fixture.Database.SeedStatic(T, "document.ref_document_categories", "ACTIVE", "SCAN_PUBLIC", TestClock.Today.AddDays(-1), ("category_code", "SCAN_PUBLIC"),
            ("label", "SCAN_PUBLIC"), ("classification", "PUBLIC"), ("domain", "ALL"), ("allowed_roles", Array.Empty<string>()));
        await fixture.Database.UserWithPermissions(T, "scan-clerk", DocumentPermissions.DocumentCreate, DocumentPermissions.DocumentRead);
        _clerk = _factory.ClientFor(T, "scan-clerk");
    }

    public ValueTask DisposeAsync()
    {
        _factory.Scanner.Down = false;
        return ValueTask.CompletedTask;
    }

    private async Task<Guid> Upload(string text)
    {
        var stored = await _clerk.Send(HttpMethod.Post, "/api/v1/documents/documents", new
        {
            categoryCode = "SCAN_PUBLIC", businessRefType = "PARTY", businessRefId = Guid.CreateVersion7(), mimeType = "text/plain",
            contentBase64 = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(text)), fileName = "upload.txt",
        }, idempotent: true);
        stored.StatusCode.ShouldBe(HttpStatusCode.Created, await stored.Content.ReadAsStringAsync());
        var view = await stored.Json();
        view.GetProperty("scanStatus").GetString().ShouldBe("PENDING", "every upload starts in quarantine");
        return view.GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> Read(Guid id) => _clerk.GetAsync($"/api/v1/documents/documents/{id}/content");

    private async Task<string?> Status(Guid id) => (await (await _clerk.GetAsync($"/api/v1/documents/documents/{id}")).Json()).GetProperty("scanStatus").GetString();

    [Fact]
    public async Task AnUpload_IsHeldInQuarantine_UntilTheScanClearsIt_ThenItIsReleased_AIA010()
    {
        var id = await Upload("A clean sample upload.");
        var held = await Read(id);
        (held.StatusCode, await held.Code()).ShouldBe((HttpStatusCode.Conflict, "DOCUMENT_QUARANTINED"));

        (await _factory.ScanUploads()).ShouldBeGreaterThanOrEqualTo(1);
        (await Status(id)).ShouldBe("CLEAN");
        var released = await Read(id);
        released.StatusCode.ShouldBe(HttpStatusCode.OK, await released.Content.ReadAsStringAsync());
        (await released.Content.ReadAsStringAsync()).ShouldBe("A clean sample upload.");
        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM audit.audit_event WHERE tenant_id = @t AND operation = 'RecordScan' AND target_id = @d",
            ("t", T), ("d", id.ToString()))).ShouldBe(1L, "the verdict is audited");
    }

    [Fact]
    public async Task AnInfectedUpload_IsNeverReleased_AndItsContentIsDeleted_AIA010()
    {
        var id = await Upload("sample " + FakeMalwareScanner.Eicar);
        await _factory.ScanUploads();
        (await Status(id)).ShouldBe("INFECTED");
        (await fixture.Database.ScalarAsOwner<string>("SELECT scan_finding FROM document.document WHERE id = @d", ("d", id))).ShouldBe("Eicar-Test-Signature");
        var refused = await Read(id);
        (refused.StatusCode, await refused.Code()).ShouldBe((HttpStatusCode.Conflict, "DOCUMENT_QUARANTINED"));
        var uri = await fixture.Database.ScalarAsOwner<string>("SELECT storage_uri FROM document.document WHERE id = @d", ("d", id));
        var storage = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Insys.Documents.Application.IDocumentStorage>(_factory.Services);
        (await storage.Get(T, uri!, CancellationToken.None)).ShouldBeNull("its content is deleted once the verdict is recorded");
    }

    [Fact]
    public async Task WhileTheScannerIsDown_UploadsWait_NoneIsReleasedUnscanned_ThenTheyClear_AIA010_D14()
    {
        _factory.Scanner.Down = true;
        var id = await Upload("Uploaded while the scanner is down.");
        (await _factory.ScanUploads()).ShouldBe(0, "nothing gets a verdict while the scanner is down");
        (await Status(id)).ShouldBe("PENDING");
        (await (await Read(id)).Code()).ShouldBe("DOCUMENT_QUARANTINED", "an upload waits rather than passes");

        _factory.Scanner.Down = false;
        await _factory.ScanUploads();
        (await Status(id)).ShouldBe("CLEAN");
        (await Read(id)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
