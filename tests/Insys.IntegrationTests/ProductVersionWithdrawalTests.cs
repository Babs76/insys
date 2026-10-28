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
using Insys.ProductFactory.Contracts;
using Insys.ProductFactory.Domain.Products;

namespace Insys.IntegrationTests;

/// <summary>
/// BR-030 (Ade, 28 October 2026): a Published version not yet Effective can be withdrawn, never retired, and the
/// withdrawal is refused while any quote, proposal or policy references the version, naming each.
/// </summary>
public sealed class ProductVersionWithdrawalTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const string Product = "BR030-MOT";
    private const string Version = $"/api/v1/product-factory/products/{Product}/versions/1";
    private static readonly Guid T = DatabaseFixture.TenantA;
    private InsysApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        await fixture.Database.UserWithPermissions(T, "br030-manager", ProductPermissions.ProductRead, ProductPermissions.ProductUpdate, ProductPermissions.ProductPublish);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task APublishedVersionNotYetEffective_IsWithdrawnOnlyWhenNothingReferencesIt_AndNeverRetired_BR030()
    {
        await fixture.Database.SeedStaticFilled(T, "product.cfg_product_definitions_and_versions", "PUBLISHED", Product, TestClock.Today.AddDays(30),
            ("product_code", Product), ("domain", "GENERAL"), ("name", "Motor"), ("coverages", new[] { "OWN_DAMAGE" }), ("cover_starts_on", "ISSUE"),
            ("reinstatement_arrears", "NONE"));
        var quote = await new RowFiller(fixture.Database, DateTimeOffset.UtcNow).Insert(T, "general.quote", ("product_code", Product), ("product_version", 1),
            ("quote_number", "Q-BR030-1"), ("status", "DRAFT"));
        var manager = _factory.ClientFor(T, "br030-manager");

        var retired = await manager.Send(HttpMethod.Post, $"{Version}/retire", ifMatch: (await manager.GetAsync(Version)).ETag());
        retired.StatusCode.ShouldBe(HttpStatusCode.Conflict, "a version that was never in force is not retired");

        var refused = await manager.Send(HttpMethod.Post, $"{Version}/withdraw", new { effectiveTo = TestClock.Today.AddDays(31) },
            ifMatch: (await manager.GetAsync(Version)).ETag());
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict, await refused.Content.ReadAsStringAsync());
        var problem = await refused.Json();
        problem.GetProperty("code").GetString().ShouldBe(ProductErrorCodes.VersionReferenced);
        problem.GetProperty("detail").GetString()!.ShouldContain("GENERAL quote Q-BR030-1");

        await fixture.Database.ExecuteAsOwner("DELETE FROM general.quote WHERE id = @q", ("q", quote));
        var withdrawn = await manager.Send(HttpMethod.Post, $"{Version}/withdraw", new { effectiveTo = TestClock.Today.AddDays(31) },
            ifMatch: (await manager.GetAsync(Version)).ETag());
        withdrawn.StatusCode.ShouldBe(HttpStatusCode.NoContent, await withdrawn.Content.ReadAsStringAsync());
        (await (await manager.GetAsync(Version)).Json()).GetProperty("status").GetString().ShouldBe(nameof(ProductVersionStatus.Withdrawn));
    }
}
