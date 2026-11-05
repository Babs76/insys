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
using Insys.Seed;
using Shouldly;
using Xunit;

namespace Insys.IntegrationTests;

/// <summary>
/// The seed's writes made on a record's version (If-Match): refused with 412 because a consumer of an earlier write's event changed the
/// record in between (rotate-passwords run 42057678256's contact point), they're made again on the fresh version, after a settle, under an
/// Idempotency-Key of their own, as a route that requires one keeps the 412 against the first. No database: the API is a fake that refuses as asked.
/// </summary>
public sealed class SeedPersonaTests
{
    [Fact]
    public async Task AWriteRefusedWith412_IsMadeAgainOnTheFreshVersion_AfterASettle_UnderAKeyOfItsOwn()
    {
        var api = new FakeApi(refusals: 1);
        var settles = 0;
        var persona = new Persona(new HttpClient(api) { BaseAddress = new Uri("http://api.test") }) { Settle = () => { settles++; return Task.CompletedTask; } };

        await persona.Send(HttpMethod.Post, "/api/v1/party/parties/p1/contact-points", new { channelType = "EMAIL" }, "\"7\"", key: "OFTZA:contact:1:EMAIL");

        var writes = api.Writes;
        writes.Count.ShouldBe(2);
        writes[0].IfMatch.ShouldBe("\"7\"");
        writes[1].IfMatch.ShouldBe("\"8\"", "the retry is made on the record's fresh version");
        writes[0].Key.ShouldBe("OFTZA:contact:1:EMAIL");
        writes[1].Key.ShouldNotBe(writes[0].Key, "a route that requires a key keeps the 412 against the first, and would replay it");
        settles.ShouldBe(1);
        api.Reads.ShouldBe(["/api/v1/party/parties/p1/contact-points", "/api/v1/party/parties/p1"], "the record is the path less its action");
    }

    [Fact]
    public async Task AWriteRefusedWith412_EveryTime_GivesUpAfterThreeRetries_AndSaysSo()
    {
        var api = new FakeApi(refusals: int.MaxValue);
        var persona = new Persona(new HttpClient(api) { BaseAddress = new Uri("http://api.test") });

        var refused = await Should.ThrowAsync<SeedRefusedException>(() =>
            persona.Send(HttpMethod.Post, "/api/v1/party/parties/p1/contact-points", new { channelType = "EMAIL" }, "\"7\"", key: "OFTZA:contact:1:EMAIL"));

        refused.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        api.Writes.Count.ShouldBe(1 + Persona.PreconditionRetries);
        api.Writes.Select(w => w.Key).Distinct().Count().ShouldBe(api.Writes.Count, "each retry has a key of its own");
    }

    /// <summary>Refuses the first writes with 412 and accepts the rest; the record's version goes up with each refusal, as a consumer's change would.</summary>
    private sealed class FakeApi(int refusals) : HttpMessageHandler
    {
        private int _version = 7;
        private int _refused;
        public List<(string? IfMatch, string? Key)> Writes { get; } = [];
        public List<string> Reads { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
            {
                Reads.Add(path);
                // Only the record answers with its version; the action's path doesn't.
                var found = path == "/api/v1/party/parties/p1";
                var read = new HttpResponseMessage(found ? HttpStatusCode.OK : HttpStatusCode.MethodNotAllowed) { Content = new StringContent("{}") };
                if (found)
                {
                    read.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"{_version}\"");
                }

                return Task.FromResult(read);
            }

            Writes.Add((request.Headers.TryGetValues("If-Match", out var m) ? m.Single() : null,
                request.Headers.TryGetValues("Idempotency-Key", out var k) ? k.Single() : null));
            if (_refused++ < refusals)
            {
                _version++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PreconditionFailed) { Content = new StringContent("{\"code\":\"PRECONDITION_FAILED\"}") });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{\"id\":\"c1\"}") });
        }
    }
}
