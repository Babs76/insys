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
using Insys.Rating.Contracts;
using Insys.Testing;

namespace Insys.IntegrationTests;

/// <summary>
/// Performance smoke test of a hot path (Phase 7): premium calculation through Rating, recorded with its breakdown.
/// p95 within the rating target (NFR-003: 3 s), sequentially and 8 at a time. Not a load test (NFR-015).
/// </summary>
public sealed class RatingPerformanceSmokeTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly Guid Tenant = DatabaseFixture.TenantA;
    private InsysApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host(new Dictionary<string, string?> { ["RateLimiting:PermitsPerMinute"] = "100000" });
        await fixture.Database.UserWithPermissions(Tenant, "rating-smoke", RatingPermissions.CalculationCreate, RatingPermissions.CalculationRead);
        _client = _factory.ClientFor(Tenant, "rating-smoke");
        var from = TestClock.Today.AddDays(-30);
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO product.cfg_product_definitions_and_versions (tenant_id, id, code, version, effective_from, record_status, created_by, change_reason,
                                                                     product_code, domain, name, coverages, family_code, class_code, cover_starts_on)
            VALUES (@t, gen_random_uuid(), 'P-MOT', 1, @from, 'PUBLISHED', 'seed', 'published', 'P-MOT', 'GENERAL', 'Motor', ARRAY['OWN_DAMAGE'], 'GEN-MOTOR', 'MOTOR', 'ISSUE');
            INSERT INTO rating.cfg_rating_tables (tenant_id, code, version, effective_from, record_status, created_by, change_reason, table_code, dimensions, values, interpolation)
            VALUES (@t, 'PMOT-BASE', 1, @from, 'EFFECTIVE', 'seed', 'seed', 'PMOT-BASE', ARRAY['VEHICLE_GROUP'],
                    '[{"key":["G1"],"value":0.02},{"key":["G2"],"value":0.025},{"key":["G3"],"value":0.03}]', 'NONE')
            """, ("t", Tenant), ("from", from));
        await fixture.Database.SeedStatic(Tenant, "rating.cfg_rating_factor_definitions", "EFFECTIVE", "VEHICLE_GROUP", from, ("factor_code", "VEHICLE_GROUP"),
            ("data_type", "CODE"), ("source_attribute", "vehicle_group"));
        await fixture.Database.SeedStatic(Tenant, "rating.cfg_premium_parameters", "EFFECTIVE", "PMOT-P1", from, ("parameter_code", "PMOT-BASE"),
            ("product_version", "P-MOT v1"), ("value", 1m), ("unit", "FACTOR"));
        await fixture.Database.SeedStatic(Tenant, "rating.cfg_premium_parameters", "EFFECTIVE", "PMOT-P2", from, ("parameter_code", "POLICY_FEE"),
            ("product_version", "P-MOT v1"), ("value", 50m), ("unit", "AMOUNT"));
        await fixture.Database.SeedStatic(Tenant, "rating.cfg_rounding_and_currency_precision_rules", "EFFECTIVE", "ZAR-2DP", from, ("rule_code", "ZAR-2DP"),
            ("currency", "ZAR"), ("step", 0.01m), ("method", "HALF_UP"), ("stage", "FINAL_PREMIUM"));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task PremiumCalculation_StaysWithinTheRatingLatencyTarget_NFR003()
    {
        var sequential = await Latency.Sequential("Rating calculation, sequential", 5, 40, Calculate);
        var concurrent = await Latency.Concurrent("Rating calculation, 8 at a time", 40, 8, Calculate);

        sequential.P95.ShouldBeLessThan(TimeSpan.FromSeconds(3), sequential.ToString());
        concurrent.P95.ShouldBeLessThan(TimeSpan.FromSeconds(3), concurrent.ToString());
    }

    private async Task Calculate(int i)
    {
        var response = await _client.Send(HttpMethod.Post, "/api/v1/rating/calculations", new
        {
            requestRef = $"S-{Guid.CreateVersion7():N}"[..20], sourceDomain = "GENERAL", subjectType = "GENERAL_QUOTE", subjectId = Guid.CreateVersion7(), productCode = "P-MOT",
            productVersion = 1, currency = "ZAR", frequency = "ANNUAL", values = new { sum_insured = 50_000m + (Math.Abs(i) * 1_000m), vehicle_group = $"G{(Math.Abs(i) % 3) + 1}" },
        }, idempotent: true);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }
}
