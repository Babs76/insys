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
using Insys.Rules.Contracts;

namespace Insys.IntegrationTests;

/// <summary>
/// BR-025: a subject's rating calculations and rule decisions page with a cursor, newest first, like the other lists; a
/// subject with more than a page is listed in full, every record once.
/// </summary>
public sealed class SubjectHistoryPagingTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly Guid T = DatabaseFixture.TenantA;
    private InsysApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        await fixture.Database.UserWithPermissions(T, "br025-reader", RatingPermissions.CalculationRead, RulesPermissions.DecisionRead);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Theory]
    [InlineData("rating.rating_request", "calculated_at", "/api/v1/rating/calculations", "GENERAL_QUOTE")]
    [InlineData("rules.decision_log", "evaluated_at", "/api/v1/rules/decisions", "LIFE_PROPOSAL")]
    public async Task ASubjectsHistory_PagesNewestFirst_EveryRecordOnce_BR025(string table, string at, string list, string subjectType)
    {
        var subject = Guid.CreateVersion7();
        var filler = new RowFiller(fixture.Database, DateTimeOffset.UtcNow);
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            (string, object)[] row = [("subject_type", subjectType), ("subject_id", subject), ("inputs", System.Text.Json.JsonDocument.Parse("{}").RootElement),
                (at, DateTimeOffset.UtcNow)];
            ids.Add(await filler.Insert(T, table, table == "rules.decision_log" ? [.. row, ("fired_rules", Array.Empty<string>())] : row));
        }

        var reader = _factory.ClientFor(T, "br025-reader");
        var seen = new List<Guid>();
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var response = await reader.GetAsync($"{list}?subjectType={subjectType}&subjectId={subject}&limit=2{(cursor is null ? "" : $"&cursor={cursor}")}");
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var body = await response.Json();
            seen.AddRange(body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
            if (body.GetProperty("nextCursor").ValueKind == System.Text.Json.JsonValueKind.Null)
            {
                break;
            }

            cursor = body.GetProperty("nextCursor").GetString();
        }

        seen.ShouldBe(ids.OrderByDescending(id => id.ToString("N"), StringComparer.Ordinal).ToList(), "newest first, every record once");
    }
}
