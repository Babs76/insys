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
using System.Net.Http.Json;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Insys.BuildingBlocks.Application.Security;
using Insys.BuildingBlocks.Cloud;
using Insys.BuildingBlocks.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Insys.IntegrationTests;

/// <summary>ADR-027 phase 2: the Transit adapter's contract, against a real OpenBao server, not a stub.</summary>
public sealed class OpenBaoTransitTests : IAsyncLifetime
{
    private const string Root = "test-root-token";
    private const string Key = "insys-test";

    private readonly IContainer _server = new ContainerBuilder("quay.io/openbao/openbao:2.6.5")
        .WithEnvironment("BAO_DEV_ROOT_TOKEN_ID", Root)
        .WithEnvironment("BAO_DEV_LISTEN_ADDRESS", "0.0.0.0:8200")
        .WithPortBinding(8200, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8200).ForPath("/v1/sys/health")))
        .Build();

    private readonly HttpClient _http = new();

    private string Address => $"http://{_server.Hostname}:{_server.GetMappedPublicPort(8200)}";

    public async ValueTask InitializeAsync()
    {
        await _server.StartAsync();
        (await Admin("sys/mounts/transit", new { type = "transit" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Admin($"transit/keys/{Key}", new { })).IsSuccessStatusCode.ShouldBeTrue();
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
    }

    private async Task<HttpResponseMessage> Admin(string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Address}/v1/{path}") { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Vault-Token", Root);
        return await _http.SendAsync(request);
    }

    private OpenBaoKeyManagementService Service(string token = Root, int attempts = 2) => new(_http,
        Options.Create(new OpenBaoKmsOptions { Address = Address, Token = token, KeyName = Key, MaxAttempts = attempts }), NullLogger<OpenBaoKeyManagementService>.Instance)
    {
        Delay = (_, _) => Task.CompletedTask,
    };

    [Fact]
    public async Task ADataKey_IsReturnedAs256Bits_WithCiphertextThatDecryptsToIt_OnANewInstance()
    {
        var key = await Service().CreateDataKey(TestContext.Current.CancellationToken);
        key.Plaintext.Length.ShouldBe(32);
        Encoding.UTF8.GetString(key.Ciphertext).ShouldStartWith("vault:v1:");
        key.Ciphertext.ShouldNotBe(key.Plaintext);
        (await Service().DecryptDataKey(key.Ciphertext, TestContext.Current.CancellationToken)).ShouldBe(key.Plaintext);
    }

    [Fact]
    public async Task ARotatedTransitKey_StillDecryptsWhatItProtected_AndNewKeysUseTheNewVersion()
    {
        var before = await Service().CreateDataKey(TestContext.Current.CancellationToken);
        (await Admin($"transit/keys/{Key}/rotate", new { })).IsSuccessStatusCode.ShouldBeTrue();
        (await Service().DecryptDataKey(before.Ciphertext, TestContext.Current.CancellationToken)).ShouldBe(before.Plaintext);
        Encoding.UTF8.GetString((await Service().CreateDataKey(TestContext.Current.CancellationToken)).Ciphertext).ShouldStartWith("vault:v2:");
    }

    [Fact]
    public async Task ABadTokenOrTamperedCiphertext_IsRefused_WithoutRetrying_OrNamingTheKeys()
    {
        var key = await Service().CreateDataKey(TestContext.Current.CancellationToken);
        var denied = await Should.ThrowAsync<OpenBaoException>(() => Service("wrong", attempts: 1).DecryptDataKey(key.Ciphertext, TestContext.Current.CancellationToken));
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        OpenBaoKeyManagementService.IsTransient(denied).ShouldBeFalse();
        denied.Message.ShouldNotContain(Encoding.UTF8.GetString(key.Ciphertext));

        var tampered = Encoding.UTF8.GetBytes("vault:v1:" + Convert.ToBase64String(new byte[60]));
        OpenBaoKeyManagementService.IsTransient(await Should.ThrowAsync<OpenBaoException>(() => Service().DecryptDataKey(tampered, TestContext.Current.CancellationToken))).ShouldBeFalse();
    }

    [Fact]
    public async Task ASealedServer_IsRetried_ThenFails()
    {
        var key = await Service().CreateDataKey(TestContext.Current.CancellationToken);
        (await _http.SendAsync(Sealing())).IsSuccessStatusCode.ShouldBeTrue();
        var waits = 0;
        var service = new OpenBaoKeyManagementService(_http, Options.Create(new OpenBaoKmsOptions { Address = Address, Token = Root, KeyName = Key, MaxAttempts = 3 }),
            NullLogger<OpenBaoKeyManagementService>.Instance)
        {
            Delay = (_, _) =>
            {
                waits++;
                return Task.CompletedTask;
            },
        };
        (await Should.ThrowAsync<OpenBaoException>(() => service.DecryptDataKey(key.Ciphertext, TestContext.Current.CancellationToken))).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        waits.ShouldBe(2);
    }

    private HttpRequestMessage Sealing()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{Address}/v1/sys/seal");
        request.Headers.Add("X-Vault-Token", Root);
        return request;
    }
}
