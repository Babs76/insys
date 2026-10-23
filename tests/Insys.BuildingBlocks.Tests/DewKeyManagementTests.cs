/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Tests
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
using HuaweiCloud.SDK.Core;
using Insys.BuildingBlocks.Cloud;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Insys.BuildingBlocks.Tests;

/// <summary>
/// DEW KMS calls (ADR-018): throttling (429), server errors, timeouts and lost connections are retried with exponential
/// back-off as Huawei's DEW best practice recommends (200 ms doubled per attempt, at most 6 attempts); client errors are not.
/// The DEW API is faked here; DewKmsCloudTests runs against the real service when credentials are present.
/// </summary>
public sealed class DewKeyManagementTests
{
    [Fact]
    public async Task Throttling_AndServerErrors_AreRetriedWithExponentialBackOff()
    {
        var api = new FakeApi(new ClientRequestException(429, new SdkError("APIGW.0308", "The throttling threshold has been reached")),
            new ServerResponseException(503, new SdkError("KMS.0001", "Service unavailable")));
        var (kms, waits) = Kms(api);

        var key = await kms.CreateDataKey(CancellationToken.None);

        key.Plaintext.Length.ShouldBe(32);
        api.Calls.ShouldBe(3);
        waits.Count.ShouldBe(2);
        waits[0].TotalMilliseconds.ShouldBeInRange(160, 240);
        waits[1].TotalMilliseconds.ShouldBeInRange(320, 480);
    }

    [Fact]
    public async Task ClientErrors_AreNotRetried()
    {
        var api = new FakeApi(new ClientRequestException(400, new SdkError("KMS.0205", "Invalid key ID")));
        var (kms, waits) = Kms(api);

        await Should.ThrowAsync<ClientRequestException>(() => kms.CreateDataKey(CancellationToken.None));
        (api.Calls, waits.Count).ShouldBe((1, 0));
    }

    [Fact]
    public async Task PersistentThrottling_GivesUpAfterSixAttempts_WithWaitsCappedAtFiveSeconds()
    {
        var api = new FakeApi([.. Enumerable.Range(0, 10).Select(_ => (Exception)new ClientRequestException(429, new SdkError("APIGW.0308", "throttled")))]);
        var (kms, waits) = Kms(api);

        await Should.ThrowAsync<ClientRequestException>(() => kms.DecryptDataKey(new byte[48], CancellationToken.None));
        api.Calls.ShouldBe(6);
        waits.ShouldAllBe(w => w <= TimeSpan.FromSeconds(5));
    }

    private static (DewKeyManagementService Kms, List<TimeSpan> Waits) Kms(FakeApi api)
    {
        var waits = new List<TimeSpan>();
        var kms = new DewKeyManagementService(api, Options.Create(new DewKmsOptions { MasterKeyId = "cmk-test" }), NullLogger<DewKeyManagementService>.Instance)
        {
            Delay = (wait, _) =>
            {
                waits.Add(wait);
                return Task.CompletedTask;
            },
        };
        return (kms, waits);
    }

    /// <summary>Fails with the given errors in turn, then succeeds.</summary>
    private sealed class FakeApi(params Exception[] failures) : IDewKmsApi
    {
        private readonly Queue<Exception> _failures = new(failures);

        public int Calls { get; private set; }

        public Task<(string PlainTextHex, string CipherTextHex)> CreateDatakey(string masterKeyId, CancellationToken cancellationToken)
        {
            Calls++;
            return _failures.TryDequeue(out var failure) ? Task.FromException<(string, string)>(failure)
                : Task.FromResult((new string('A', 64), new string('B', 96)));
        }

        public Task<string> DecryptDatakey(string masterKeyId, string cipherTextHex, int plaintextBytes, CancellationToken cancellationToken)
        {
            Calls++;
            return _failures.TryDequeue(out var failure) ? Task.FromException<string>(failure) : Task.FromResult(new string('A', 64));
        }
    }
}
