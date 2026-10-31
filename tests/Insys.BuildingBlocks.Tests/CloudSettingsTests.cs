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
using Insys.BuildingBlocks.Application.Security;
using Insys.BuildingBlocks.Application.Storage;
using Insys.BuildingBlocks.Cloud;
using Insys.BuildingBlocks.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Insys.BuildingBlocks.Tests;

/// <summary>ADR-027 phase 1: the neutral names select and configure the adapters, the replaced names still do for one release, and a provider with no adapter fails at start-up.</summary>
public sealed class CloudSettingsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))).Build();

    private static ServiceCollection Registered(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInsysCloud(configuration);
        return services;
    }

    [Fact]
    public void TheNeutralNames_SelectTheKeyServiceAndObjectStore_WithNoWarning()
    {
        var configuration = Config(("FieldProtection:Provider", "envelope"), ("Kms:Provider", "dew"), ("Kms:MasterKeyId", "cmk"), ("ObjectStore:Provider", "s3-compatible"), ("ObjectStore:Bucket", "b"));
        var services = Registered(configuration);
        services.ShouldContain(d => d.ServiceType == typeof(IKeyManagementService));
        services.ShouldContain(d => d.ServiceType == typeof(IObjectStore));
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<DewKmsOptions>>().Value.MasterKeyId.ShouldBe("cmk");
        provider.GetRequiredService<IOptions<ObjectStoreOptions>>().Value.Bucket.ShouldBe("b");
        CloudSettings.OldNamesUsed(configuration).ShouldBeEmpty();
    }

    [Fact]
    public void TheReplacedNames_StillWork_AndAreNamedForTheWarning()
    {
        var configuration = Config(("FieldProtection:Provider", "envelope"), ("FieldProtection:Envelope:Kms", "dew"), ("Huawei:Kms:MasterKeyId", "old"), ("Documents:Storage:Provider", "object-store"),
            ("Huawei:Obs:Bucket", "old-bucket"));
        var services = Registered(configuration);
        services.ShouldContain(d => d.ServiceType == typeof(IKeyManagementService));
        services.ShouldContain(d => d.ServiceType == typeof(IObjectStore));
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<DewKmsOptions>>().Value.MasterKeyId.ShouldBe("old");
        var store = provider.GetRequiredService<IOptions<ObjectStoreOptions>>().Value;
        (store.Bucket, store.ServiceUrl).ShouldBe(("old-bucket", "https://obs.af-south-1.myhuaweicloud.com"), "the old section keeps its defaults");
        CloudSettings.OldNamesUsed(configuration).Count.ShouldBe(4);
    }

    [Fact]
    public void WhereBothAreSet_TheNeutralNameWins()
    {
        var configuration = Config(("Kms:Provider", "local"), ("FieldProtection:Envelope:Kms", "dew"), ("Huawei:Obs:Bucket", "old"), ("ObjectStore:Bucket", "new"));
        CloudSettings.KmsProvider(configuration).ShouldBe(CloudSettings.LocalKms);
        using var provider = Registered(configuration).BuildServiceProvider();
        provider.GetRequiredService<IOptions<ObjectStoreOptions>>().Value.Bucket.ShouldBe("new");
    }

    [Fact]
    public void TheNeutralObjectStore_HasNoCloudsEndpointByDefault()
    {
        using var provider = Registered(Config(("ObjectStore:Bucket", "b"))).BuildServiceProvider();
        provider.GetRequiredService<IOptions<ObjectStoreOptions>>().Value.ServiceUrl.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Kms:Provider", "aws", "no adapter yet")]
    [InlineData("Kms:Provider", "vault", "no adapter yet")]
    [InlineData("ObjectStore:Provider", "azure-blob", "no adapter yet")]
    [InlineData("Kms:Provider", "nonsense", "isn't known")]
    public void AProviderWithNoAdapter_FailsAtStartUp(string setting, string value, string message)
    {
        var configuration = Config(("FieldProtection:Provider", "envelope"), (setting, value));
        Should.Throw<InvalidOperationException>(() =>
        {
            CloudSettings.KmsProvider(configuration);
            CloudSettings.ObjectStoreProvider(configuration);
        }).Message.ShouldContain(message);
    }
}
