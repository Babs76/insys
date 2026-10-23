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
using System.Diagnostics;
using System.Net;
using Insys.Testing;

namespace Insys.IntegrationTests;

/// <summary>
/// Requests that arrive while a new instance is still building its endpoints, with the CPU saturated (as on a two-core CI
/// runner): each must be answered within a few seconds, either normally or with 503 and Retry-After, never left hanging.
/// </summary>
public sealed class WarmupTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task RequestsDuringWarmup_UnderSaturatedCpu_AreAnsweredQuickly_OkOr503()
    {
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "warmup-user", "static_data.read");
        using var burn = new CancellationTokenSource();
        var burners = Enumerable.Range(0, Environment.ProcessorCount).Select(_ => Task.Factory.StartNew(() =>
        {
            while (!burn.IsCancellationRequested)
            {
                Thread.SpinWait(10_000);
            }
        }, TaskCreationOptions.LongRunning)).ToList();
        try
        {
            await using var factory = new InsysApiFactory(fixture.Database, waitForWarmup: false);
            var user = factory.ClientFor(DatabaseFixture.TenantA, "warmup-user");
            var anonymous = factory.CreateClient();
            foreach (var client in new[] { user, anonymous })
            {
                client.Timeout = TimeSpan.FromSeconds(60);
            }

            var calls = new List<(string Name, Func<Task<HttpResponseMessage>> Send)>
            {
                ("live", () => anonymous.GetAsync("/health/live")),
                ("ready", () => anonymous.GetAsync("/health/ready")),
            };
            calls.AddRange(Enumerable.Range(0, 6).Select(i => ($"api{i}", (Func<Task<HttpResponseMessage>>)(() => user.GetAsync("/api/v1/static-data/static/currencies")))));
            var results = await Task.WhenAll(calls.Select(async call =>
            {
                var stopwatch = Stopwatch.StartNew();
                using var response = await call.Send();
                return (call.Name, response.StatusCode, RetryAfter: response.Headers.RetryAfter is not null, stopwatch.Elapsed);
            }));

            foreach (var result in results)
            {
                Latency.Note($"Warm-up {result.Name}: {(int)result.StatusCode} in {result.Elapsed.TotalMilliseconds:F0} ms");
            }

            results.ShouldAllBe(r => r.Elapsed < Limit, string.Join("; ", results.Select(r => $"{r.Name} {(int)r.StatusCode} {r.Elapsed.TotalMilliseconds:F0} ms")));
            results.Single(r => r.Name == "live").StatusCode.ShouldBe(HttpStatusCode.OK);
            results.Where(r => r.Name != "live").ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK || (r.StatusCode == HttpStatusCode.ServiceUnavailable && r.RetryAfter));
        }
        finally
        {
            await burn.CancelAsync();
            await Task.WhenAll(burners);
        }
    }

    /// <summary>Warm-up builds the router itself, so the first request after readiness pays nothing for it.</summary>
    [Fact]
    public async Task FirstRequestAfterWarmup_FindsTheRouterBuilt()
    {
        await using var factory = new InsysApiFactory(fixture.Database);
        var client = factory.CreateClient();
        var stopwatch = Stopwatch.StartNew();
        (await client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
        Latency.Note($"First request after warm-up: {stopwatch.Elapsed.TotalMilliseconds:F0} ms");
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
    }
}
