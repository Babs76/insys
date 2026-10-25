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
using Insys.BuildingBlocks.Application.Context;
using Insys.Life.Application;
using Insys.Life.Domain.Groups;
using Microsoft.Extensions.DependencyInjection;
using NpgsqlTypes;

namespace Insys.IntegrationTests;

/// <summary>
/// GAP-043: free cover limit bands are int4range, validated and stored as intervals ("[50,200)"), and the store now reads
/// that one format. Before, it read "50-199", which no stored band could be, so no member ever had a free cover limit
/// and every member was accepted without underwriting evidence whatever their cover.
/// </summary>
public sealed class FreeCoverBandTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly Guid Tenant = DatabaseFixture.TenantA;
    private static readonly DateOnly Today = TestClock.Today;
    private InsysApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        foreach (var (code, band, limit) in new[]
                 {
                     ("SMALL", new NpgsqlRange<int>(1, true, 50, false), 500_000m),
                     ("MEDIUM", new NpgsqlRange<int>(50, true, 200, false), 1_000_000m),
                     ("LARGE", new NpgsqlRange<int>(200, true, false, 0, false, true), 2_000_000m),
                 })
        {
            await fixture.Database.SeedStatic(Tenant, "life.cfg_free_cover_limits", "EFFECTIVE", code, Today.AddDays(-30),
                ("size_band", band), ("free_cover_limit", limit), ("evidence_above", "MEDICAL"));
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Fail safe: a scheme whose size matches no band, or whose band is not yet in force, has no free cover limit, and then
    /// any cover needs evidence. Before, "no limit" meant no member ever needed evidence.
    /// </summary>
    [Theory]
    [InlineData(5_000, "no band covers 5 000 members")]
    [InlineData(10, "the band for 10 members is not in force yet")]
    public async Task NoBandInForce_MeansEvidenceForAnyCover_NeverNoLimit_GAP043(int members, string because)
    {
        var tenant = DatabaseFixture.TenantB;
        await fixture.Database.SeedStatic(tenant, "life.cfg_free_cover_limits", "EFFECTIVE", "B-SMALL", Today.AddDays(-30),
            ("size_band", new NpgsqlRange<int>(100, true, 1_000, false)), ("free_cover_limit", 750_000m), ("evidence_above", "MEDICAL"));
        await fixture.Database.SeedStatic(tenant, "life.cfg_free_cover_limits", "EFFECTIVE", "B-FUTURE", Today.AddDays(60),
            ("size_band", new NpgsqlRange<int>(1, true, 100, false)), ("free_cover_limit", 750_000m), ("evidence_above", "MEDICAL"));
        await using var scope = _factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(tenant, "test");
        await using var transaction = await scope.ServiceProvider.GetRequiredService<Insys.Life.Infrastructure.Persistence.LifeDbContext>().Database.BeginTransactionAsync();
        var limit = await scope.ServiceProvider.GetRequiredService<ILifeCodes>().FreeCoverLimit(members, Today, CancellationToken.None);
        var scheme = GroupScheme.SetUpScheme(Guid.CreateVersion7(), "GS-NONE", Guid.CreateVersion7(), "LIF-GRP", 1, "EMPLOYER", Today, Today.AddYears(1), "ZAR", Today).Value;

        var member = scheme.EnrolMember(Guid.CreateVersion7(), new MemberCategory("STAFF", 1m), 50_000m, Today, limit, DateTimeOffset.UtcNow).Value;

        limit.ShouldBeNull(because);
        member.EvidenceStatus.ShouldBe(EvidenceStatus.Required, "with no limit, any cover needs evidence");
    }

    /// <summary>The bands are checked when a draft is submitted: they start at 1 member, and do not overlap or leave gaps.</summary>
    [Fact]
    public async Task BandsThatOverlapOrLeaveAGap_AreRefusedOnSubmit_ARangeThatStillTilesIsAccepted_GAP043()
    {
        string[] permissions =
        [
            Insys.BuildingBlocks.Api.StaticData.StaticDataPermissions.Read, Insys.BuildingBlocks.Api.StaticData.StaticDataPermissions.Create,
            Insys.BuildingBlocks.Api.StaticData.StaticDataPermissions.Update,
        ];
        await fixture.Database.UserWithPermissions(Tenant, "band-maker", permissions);
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantB, "band-maker-b", permissions);
        var maker = _factory.ClientFor(Tenant, "band-maker");

        // Against [1,50), [50,200) and [200,): a new version of the top band with the same range is accepted; a band that
        // overlaps them is refused.
        (await Submit(maker, "LARGE", "[200,)")).ShouldBe("SUBMITTED");
        (await Submit(maker, "OVERLAP", "[150,300)")).ShouldBe("LIFE_FREE_COVER_BANDS_INVALID");

        // A band that leaves members below it uncovered is refused: bands are built up from 1 member.
        (await Submit(_factory.ClientFor(DatabaseFixture.TenantB, "band-maker-b"), "B-HIGH", "[2000,)")).ShouldBe("LIFE_FREE_COVER_BANDS_INVALID");
    }

    private static async Task<string?> Submit(HttpClient maker, string code, string band)
    {
        const string url = "/api/v1/life/static/free-cover-limits";
        var created = await maker.Send(HttpMethod.Post, url,
            new { code, sizeBand = band, freeCoverLimit = 2_000_000m, evidenceAbove = "MEDICAL", effectiveFrom = Today.AddDays(1), changeReason = "Bands" }, idempotent: true);
        created.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var version = (await created.Json()).GetProperty("version").GetInt32();
        var submitted = await maker.Send(HttpMethod.Post, $"{url}/{code}/versions/{version}/submit", ifMatch: created.ETag());
        var body = await submitted.Json();
        return submitted.IsSuccessStatusCode ? body.GetProperty("recordStatus").GetString() : body.GetProperty("code").GetString();
    }

    [Theory]
    [InlineData(49, 500_000)]
    [InlineData(50, 1_000_000)]
    [InlineData(199, 1_000_000)]
    [InlineData(200, 2_000_000)]
    [InlineData(5_000, 2_000_000)]
    public async Task SchemeSize_BelowAtAndAboveABandEdge_GetsThatBandsLimit_GAP043(int members, decimal expected)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(Tenant, "test");
        await using var transaction = await scope.ServiceProvider.GetRequiredService<Insys.Life.Infrastructure.Persistence.LifeDbContext>().Database.BeginTransactionAsync();

        (await scope.ServiceProvider.GetRequiredService<ILifeCodes>().FreeCoverLimit(members, Today, CancellationToken.None)).ShouldBe(expected);
    }

    [Theory]
    [InlineData(49, EvidenceStatus.Required)]
    [InlineData(50, EvidenceStatus.NotRequired)]
    [InlineData(199, EvidenceStatus.NotRequired)]
    [InlineData(200, EvidenceStatus.NotRequired)]
    public async Task MemberEnrolled_WithTheBandsLimit_NeedsEvidenceOnlyAboveIt_GAP043(int members, string evidence)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(Tenant, "test");
        await using var transaction = await scope.ServiceProvider.GetRequiredService<Insys.Life.Infrastructure.Persistence.LifeDbContext>().Database.BeginTransactionAsync();
        var limit = await scope.ServiceProvider.GetRequiredService<ILifeCodes>().FreeCoverLimit(members, Today, CancellationToken.None);
        var scheme = GroupScheme.SetUpScheme(Guid.CreateVersion7(), "GS-BAND", Guid.CreateVersion7(), "LIF-GRP", 1, "EMPLOYER", Today, Today.AddYears(1), "ZAR", Today).Value;

        // Cover of 800 000 (salary 200 000 × 4): above the small band's limit, within the others.
        var member = scheme.EnrolMember(Guid.CreateVersion7(), new MemberCategory("MGMT", 4m), 200_000m, Today, limit, DateTimeOffset.UtcNow).Value;

        (member.FreeCoverLimit, member.EvidenceStatus).ShouldBe((limit, evidence));
        limit.ShouldNotBeNull("before GAP-043, no band ever matched and this was null");
    }
}
