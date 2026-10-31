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
using Insys.BuildingBlocks.Application.Storage;
using Insys.BuildingBlocks.Cloud;
using Insys.Documents.Contracts;
using Insys.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Insys.IntegrationTests;

/// <summary>
/// The OBS adapter's S3 contract against an S3-compatible store (Adobe S3Mock, Apache-2.0), and documents stored in the object store end to
/// end (ADR-016, Phase 8 Workstream D). Real OBS runs in ObsCloudTests when credentials are present.
/// </summary>
public sealed class ObjectStoreTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const string Bucket = "insys-documents";
    private const string AccessKey = "test-access-key";
    private const string SecretKey = "test-secret-key";

    private readonly IContainer _s3 = new ContainerBuilder("adobe/s3mock:5.2.3")
        .WithEnvironment("COM_ADOBE_TESTING_S3MOCK_STORE_INITIAL_BUCKETS", Bucket)
        .WithPortBinding(9090, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9090).ForPath("/favicon.ico").ForStatusCodeMatching(_ => true)))
        .Build();

    private string ServiceUrl => $"http://{_s3.Hostname}:{_s3.GetMappedPublicPort(9090)}";

    public async ValueTask InitializeAsync() => await _s3.StartAsync();

    public async ValueTask DisposeAsync() => await _s3.DisposeAsync();

    [Fact]
    public async Task S3ObjectStore_PutsGetsAndDeletes_AndAMissingObjectIsNull()
    {
        using var store = new S3ObjectStore(Options.Create(Obs()));
        var content = "policy schedule"u8.ToArray();

        await store.Put("tenant/a/documents/1", content, "text/plain", CancellationToken.None);
        (await store.Get("tenant/a/documents/1", CancellationToken.None)).ShouldBe(content);
        (await store.Get("tenant/a/documents/missing", CancellationToken.None)).ShouldBeNull();

        await store.Delete("tenant/a/documents/1", CancellationToken.None);
        (await store.Get("tenant/a/documents/1", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Document_IsStoredInTheObjectStore_UnderItsTenantsPrefix_AndReadBackThroughTheApi()
    {
        var settings = new Dictionary<string, string?>
        {
            ["ObjectStore:Provider"] = "s3-compatible", ["ObjectStore:ServiceUrl"] = ServiceUrl, ["ObjectStore:Bucket"] = Bucket,
            ["ObjectStore:AccessKey"] = AccessKey, ["ObjectStore:SecretKey"] = SecretKey, ["ObjectStore:ForcePathStyle"] = "true",
        };
        var factory = await fixture.Host(settings);
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "obs-user", DocumentPermissions.DocumentCreate, DocumentPermissions.DocumentRead);
        await fixture.Database.SeedStatic(DatabaseFixture.TenantA, "document.ref_document_categories", "ACTIVE", "NOTE", TestClock.Today.AddDays(-1),
            ("category_code", "NOTE"), ("label", "Note"), ("classification", "PERSONAL"), ("domain", "ALL"), ("allowed_roles", Array.Empty<string>()));
        var client = factory.ClientFor(DatabaseFixture.TenantA, "obs-user");

        var stored = await client.Send(HttpMethod.Post, "/api/v1/documents/documents", new
        {
            categoryCode = "NOTE", businessRefType = "PARTY", businessRefId = Guid.CreateVersion7(), mimeType = "text/plain",
            contentBase64 = Convert.ToBase64String("stored in OBS"u8.ToArray()),
        }, idempotent: true);
        stored.StatusCode.ShouldBe(HttpStatusCode.Created, await stored.Content.ReadAsStringAsync());
        var document = (await stored.Json()).GetProperty("id").GetGuid();

        (await (await client.GetAsync($"/api/v1/documents/documents/{document}/content?purpose=TEST")).Content.ReadAsStringAsync()).ShouldBe("stored in OBS");
        var key = $"tenant/{DatabaseFixture.TenantA:N}/documents/{document:N}";
        (await factory.Services.GetRequiredService<IObjectStore>().Get(key, CancellationToken.None)).ShouldNotBeNull();
    }

    private ObjectStoreOptions Obs() => new()
    {
        ServiceUrl = ServiceUrl, Bucket = Bucket, AccessKey = AccessKey, SecretKey = SecretKey, ForcePathStyle = true,
    };
}
