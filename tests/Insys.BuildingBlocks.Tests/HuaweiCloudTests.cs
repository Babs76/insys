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
using Insys.BuildingBlocks.Cloud;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Insys.BuildingBlocks.Tests;

/// <summary>
/// Against the real Huawei Cloud services, only when credentials are present (Phase 8 Workstream D); skipped otherwise.
/// Set INSYS_HUAWEI_AK, INSYS_HUAWEI_SK and INSYS_HUAWEI_PROJECT_ID, plus INSYS_HUAWEI_KMS_KEY_ID for DEW KMS and
/// INSYS_HUAWEI_OBS_BUCKET for OBS (af-south-1 unless INSYS_HUAWEI_REGION says otherwise). Never commit them.
/// </summary>
public sealed class HuaweiCloudTests
{
    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    private static string Region => Env("INSYS_HUAWEI_REGION") ?? "af-south-1";

    [Fact]
    public async Task DewKms_CreatesADataKey_AndDecryptsItsCiphertextToTheSameKey()
    {
        Assert.SkipUnless(Env("INSYS_HUAWEI_AK") is not null && Env("INSYS_HUAWEI_KMS_KEY_ID") is not null, "No Huawei Cloud KMS credentials in the environment.");
        var options = Options.Create(new DewKmsOptions
        {
            Region = Region, ProjectId = Env("INSYS_HUAWEI_PROJECT_ID")!, MasterKeyId = Env("INSYS_HUAWEI_KMS_KEY_ID")!,
            AccessKey = Env("INSYS_HUAWEI_AK")!, SecretKey = Env("INSYS_HUAWEI_SK")!,
        });
        var kms = new DewKeyManagementService(new DewKmsApi(options), options, NullLogger<DewKeyManagementService>.Instance);

        var created = await kms.CreateDataKey(TestContext.Current.CancellationToken);

        created.Plaintext.Length.ShouldBe(32);
        (await kms.DecryptDataKey(created.Ciphertext, TestContext.Current.CancellationToken)).ShouldBe(created.Plaintext);
    }

    [Fact]
    public async Task Obs_PutsGetsAndDeletesAnObject()
    {
        Assert.SkipUnless(Env("INSYS_HUAWEI_AK") is not null && Env("INSYS_HUAWEI_OBS_BUCKET") is not null, "No Huawei Cloud OBS credentials in the environment.");
        using var store = new S3ObjectStore(Options.Create(new ObsOptions
        {
            ServiceUrl = $"https://obs.{Region}.myhuaweicloud.com", Region = Region, Bucket = Env("INSYS_HUAWEI_OBS_BUCKET")!,
            AccessKey = Env("INSYS_HUAWEI_AK")!, SecretKey = Env("INSYS_HUAWEI_SK")!,
        }));
        var key = $"insys-contract-test/{Guid.CreateVersion7():N}";

        await store.Put(key, "OBS contract test"u8.ToArray(), "text/plain", TestContext.Current.CancellationToken);
        (await store.Get(key, TestContext.Current.CancellationToken)).ShouldBe("OBS contract test"u8.ToArray());
        await store.Delete(key, TestContext.Current.CancellationToken);
        (await store.Get(key, TestContext.Current.CancellationToken)).ShouldBeNull();
    }
}
