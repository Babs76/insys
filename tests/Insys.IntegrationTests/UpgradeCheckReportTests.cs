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
using Insys.Seed;

namespace Insys.IntegrationTests;

/// <summary>
/// The upgrade check's own logic (tools/Insys.Seed --upgrade-check, Ade, 26 November 2026), against a fake API: it reads every page of tenants, which
/// tenants it checks, its report (tenant codes and approval types, never a legal name) and its exit code. No database.
/// </summary>
public sealed class UpgradeCheckReportTests
{
    private static readonly Guid OperatorTenant = Guid.Parse("0199f000-0000-7000-8000-000000000001");
    private static readonly Guid Oftza = Guid.Parse("0199f000-0000-7000-8000-0000000000b1");
    private static readonly Guid Paused = Guid.Parse("0199f000-0000-7000-8000-0000000000b2");
    private static readonly Guid Closed = Guid.Parse("0199f000-0000-7000-8000-0000000000b3");
    private static readonly Guid Loading = Guid.Parse("0199f000-0000-7000-8000-0000000000b4");
    private static readonly Guid Loaded = Guid.Parse("0199f000-0000-7000-8000-0000000000b5");

    [Fact]
    public async Task EveryPageOfTenantsIsRead_TerminatedTheOperatorAndOnboardingBeforeTheStepAreNotChecked_AListExitsOne()
    {
        var api = new FakeApi(new()
        {
            [Oftza] = ("OFTZA", "Active", true, ["PAYOUT_RETRY", "CREDIT_NOTE"]),
            [Paused] = ("PAUSED", "Suspended", true, []),
            [Closed] = ("CLOSED", "Terminated", true, ["CREDIT_NOTE"]),
            [Loading] = ("LOADING", "Onboarding", false, ["CREDIT_NOTE"]),
            [Loaded] = ("LOADED", "Onboarding", true, ["DEAD_LETTER_DISCARD"]),
            [OperatorTenant] = ("OPERATOR", "Active", true, ["PAYOUT_RETRY"]),
        }, pageSize: 2);
        var output = new StringWriter();

        (await UpgradeCheck.RunAndReport(Persona(api), OperatorTenant, output, new StringWriter())).ShouldBe(UpgradeCheck.Listed);

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.ShouldContain("OFTZA Active: CREDIT_NOTE, PAYOUT_RETRY");
        lines.ShouldContain("PAUSED Suspended: none");
        lines.ShouldContain("CLOSED Terminated: not checked (terminated)");
        lines.ShouldContain(l => l.StartsWith("LOADING Onboarding: not checked (onboarding, before SHARED_OPERATIONAL_SETUP", StringComparison.Ordinal));
        lines.ShouldContain("LOADED Onboarding: DEAD_LETTER_DISCARD");
        lines.ShouldContain(l => l.StartsWith("OPERATOR Active: not checked (the operator tenant", StringComparison.Ordinal));
        lines[^1].ShouldBe("upgrade-check: listed: 2 of the 3 tenants checked have approval types no role can decide");
        output.ToString().ShouldNotContain("Ltd", customMessage: "tenant codes only, never a legal name");
        api.Readiness.ShouldBe([Oftza, Paused, Loading, Loaded], ignoreOrder: true, "neither the terminated tenant nor the operator tenant is read");
        api.Pages.ShouldBe(4, "six tenants in pages of two, then the empty page a full last page's cursor leads to, as the API pages");
    }

    [Fact]
    public async Task EveryCheckedTenantWithAnEmptyList_ExitsZero()
    {
        var api = new FakeApi(new() { [Oftza] = ("OFTZA", "Active", true, []), [Closed] = ("CLOSED", "Terminated", true, ["CREDIT_NOTE"]) });
        var output = new StringWriter();

        (await UpgradeCheck.RunAndReport(Persona(api), OperatorTenant, output, new StringWriter())).ShouldBe(UpgradeCheck.Clear);

        output.ToString().ShouldContain("OFTZA Active: none");
        output.ToString().ShouldContain("upgrade-check: clear: in each of the 1 tenants checked");
    }

    [Fact]
    public async Task AnApiRefusal_IsNotAnEmptyList_ItExitsThree_SayingWhy()
    {
        var api = new FakeApi(new() { [Oftza] = ("OFTZA", "Active", true, []) }) { RefuseReadiness = true };
        var output = new StringWriter();
        var error = new StringWriter();

        (await UpgradeCheck.RunAndReport(Persona(api), OperatorTenant, output, error)).ShouldBe(UpgradeCheck.Unread);

        output.ToString().ShouldBeEmpty();
        error.ToString().ShouldContain("upgrade-check: unread: GET /api/v1/tenancy/tenants/");
        error.ToString().ShouldContain("403");
    }

    [Fact]
    public async Task AReadinessWithoutTheApprovalAuthorityCheck_IsUnread_NotClear()
    {
        var api = new FakeApi(new() { [Oftza] = ("OFTZA", "Active", true, []) }) { WithoutTheCheck = true };
        var error = new StringWriter();

        (await UpgradeCheck.RunAndReport(Persona(api), OperatorTenant, new StringWriter(), error)).ShouldBe(UpgradeCheck.Unread);

        error.ToString().ShouldContain("OFTZA's readiness has no APPROVAL_AUTHORITY check");
    }

    private static Persona Persona(HttpMessageHandler api) => new(new HttpClient(api) { BaseAddress = new Uri("http://api.test") });

    /// <summary>The tenant list in pages (in id order, with a cursor), and each tenant's readiness with its SHARED_OPERATIONAL_SETUP step's APPROVAL_AUTHORITY check.</summary>
    private sealed class FakeApi(Dictionary<Guid, (string Code, string Status, bool StepCompleted, string[] Missing)> tenants, int pageSize = 100) : HttpMessageHandler
    {
        public List<Guid> Readiness { get; } = [];
        public int Pages { get; private set; }
        public bool RefuseReadiness { get; init; }
        public bool WithoutTheCheck { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v1/tenancy/tenants")
            {
                Pages++;
                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                var after = query["cursor"] is { } cursor ? Guid.Parse(cursor) : Guid.Empty;
                var page = tenants.Keys.Order().Where(id => id.CompareTo(after) > 0).Take(pageSize).ToList();
                var next = page.Count == pageSize ? page[^1].ToString() : null;
                return Json(new
                {
                    items = page.Select(id => new { id, tenantCode = tenants[id].Code, legalName = tenants[id].Code + " Ltd", isolationProfile = "B", status = tenants[id].Status, rowVersion = 1 }),
                    nextCursor = next,
                });
            }

            var id = Guid.Parse(path.Split('/')[5]);
            Readiness.Add(id);
            if (RefuseReadiness)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"code":"FORBIDDEN","title":"Forbidden"}""") });
            }

            var (_, _, completed, missing) = tenants[id];
            object[] checks = WithoutTheCheck ? [] : [new { code = UpgradeCheck.Check, ready = missing.Length == 0, what = "…", missing, attestation = (object?)null, advisory = false }];
            return Json(new
            {
                tenantId = id,
                day = "2026-11-26",
                steps = new object[]
                {
                    new { step = "ORGANISATION_AND_ACCESS", label = "Organisation and access", completed, ready = true, checks = Array.Empty<object>() },
                    new { step = UpgradeCheck.Step, label = "Shared operational setup", completed, ready = missing.Length == 0, checks },
                },
            });
        }

        private static Task<HttpResponseMessage> Json(object body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body, JsonSerializerOptions.Web)) });
    }
}
