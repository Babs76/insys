/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Cloud
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
using HuaweiCloud.SDK.Core.Auth;
using HuaweiCloud.SDK.Kms.V2;
using HuaweiCloud.SDK.Kms.V2.Model;
using Insys.BuildingBlocks.Application.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Insys.BuildingBlocks.Cloud;

/// <summary>
/// Huawei Cloud DEW KMS settings (Huawei:Kms; ADR-016, ADR-018). The access key pair comes from the secret store, never
/// from the repository; the master key (CMK) is created in the account and named here by its ID.
/// </summary>
public sealed class DewKmsOptions
{
    /// <summary>The region, af-south-1 (AF-Johannesburg) in production.</summary>
    public string Region { get; set; } = "af-south-1";

    public string ProjectId { get; set; } = "";

    /// <summary>The customer master key (CMK) ID that protects the data keys.</summary>
    public string MasterKeyId { get; set; } = "";

    public string AccessKey { get; set; } = "";

    public string SecretKey { get; set; } = "";

    /// <summary>Retries after throttling (429), a 5xx, a timeout or a lost connection, with exponential back-off.</summary>
    public int MaxAttempts { get; set; } = 6;

    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>The two DEW KMS operations envelope encryption needs, behind a seam so the retry policy is testable.</summary>
public interface IDewKmsApi
{
    /// <summary>A new 256-bit data key: (plaintext, ciphertext), both hexadecimal as DEW returns them.</summary>
    Task<(string PlainTextHex, string CipherTextHex)> CreateDatakey(string masterKeyId, CancellationToken cancellationToken);

    Task<string> DecryptDatakey(string masterKeyId, string cipherTextHex, int plaintextBytes, CancellationToken cancellationToken);
}

/// <summary>The DEW KMS API through Huawei's .NET SDK (HuaweiCloud.SDK.Kms, Apache-2.0).</summary>
public sealed class DewKmsApi : IDewKmsApi
{
    private readonly Lazy<KmsAsyncClient> _client;

    public DewKmsApi(IOptions<DewKmsOptions> options) =>
        _client = new(() =>
        {
            var settings = options.Value;
            if (string.IsNullOrWhiteSpace(settings.AccessKey) || string.IsNullOrWhiteSpace(settings.SecretKey) || string.IsNullOrWhiteSpace(settings.ProjectId))
            {
                throw new InvalidOperationException("Huawei:Kms:AccessKey, SecretKey and ProjectId must come from the secret store.");
            }

            return KmsAsyncClient.NewBuilder()
                .WithCredential(new BasicCredentials(settings.AccessKey, settings.SecretKey, settings.ProjectId))
                .WithRegion(KmsRegion.ValueOf(settings.Region))
                .WithHttpConfig(HttpConfig.GetDefaultConfig())
                .Build();
        });

    public async Task<(string PlainTextHex, string CipherTextHex)> CreateDatakey(string masterKeyId, CancellationToken cancellationToken)
    {
        var response = await _client.Value.CreateDatakeyAsync(new CreateDatakeyRequest
        {
            Body = new CreateDatakeyRequestBody { KeyId = masterKeyId, DatakeyLength = "256" },
        });
        return (response.PlainText, response.CipherText);
    }

    public async Task<string> DecryptDatakey(string masterKeyId, string cipherTextHex, int plaintextBytes, CancellationToken cancellationToken)
    {
        var response = await _client.Value.DecryptDatakeyAsync(new DecryptDatakeyRequest
        {
            Body = new DecryptDatakeyRequestBody
            {
                KeyId = masterKeyId, CipherText = cipherTextHex,
                DatakeyCipherLength = plaintextBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
        });
        return response.DataKey;
    }
}

/// <summary>
/// DEW KMS for envelope encryption (FieldProtection:Envelope:Kms "dew"; ADR-018). DEW is rate-limited per account, so
/// the data key ring calls this rarely. A throttled (429) or failed (5xx, timeout, lost connection) call is retried with
/// exponential back-off, as Huawei's DEW best practice recommends: 200 ms doubled per attempt, at most six attempts,
/// each wait capped at 5 seconds and jittered so instances do not retry together.
/// </summary>
public sealed class DewKeyManagementService(IDewKmsApi api, IOptions<DewKmsOptions> options, ILogger<DewKeyManagementService> logger)
    : IKeyManagementService
{
    private const int DataKeyBytes = 32;

    /// <summary>The wait before a retry; tests replace it to run without real delays.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public string MasterKeyRef => $"dew:{options.Value.Region}:{options.Value.MasterKeyId}";

    public async Task<DataKey> CreateDataKey(CancellationToken cancellationToken)
    {
        var (plain, cipher) = await WithRetry(() => api.CreateDatakey(options.Value.MasterKeyId, cancellationToken), "CreateDatakey", cancellationToken);
        return new DataKey(Convert.FromHexString(plain), Convert.FromHexString(cipher));
    }

    public async Task<byte[]> DecryptDataKey(byte[] ciphertext, CancellationToken cancellationToken) =>
        Convert.FromHexString(await WithRetry(() => api.DecryptDatakey(options.Value.MasterKeyId, Convert.ToHexString(ciphertext), DataKeyBytes, cancellationToken),
            "DecryptDatakey", cancellationToken));

    private async Task<T> WithRetry<T>(Func<Task<T>> call, string operation, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (Exception ex) when (attempt < settings.MaxAttempts && IsTransient(ex))
            {
                var wait = TimeSpan.FromMilliseconds(Math.Min(settings.MaxDelay.TotalMilliseconds,
                    settings.InitialDelay.TotalMilliseconds * Math.Pow(2, attempt - 1) * (0.8 + (Random.Shared.NextDouble() * 0.4))));
                logger.LogWarning("DEW KMS {Operation} attempt {Attempt} failed ({ExceptionType}); retrying in {WaitMilliseconds} ms", operation, attempt,
                    ex.GetType().Name, Math.Round(wait.TotalMilliseconds));
                await Delay(wait, cancellationToken);
            }
        }
    }

    /// <summary>Throttling (429, or DEW's "throttling threshold" message), a server error, a timeout or a lost connection.</summary>
    public static bool IsTransient(Exception exception) => exception switch
    {
        ServiceResponseException { HttpStatusCode: 429 or >= 500 } => true,
        ServiceResponseException response when response.ErrorMsg?.Contains("throttling threshold", StringComparison.OrdinalIgnoreCase) == true => true,
        RequestTimeoutException or ConnectionException or HttpRequestException or TaskCanceledException { CancellationToken.IsCancellationRequested: false } => true,
        _ => false,
    };
}
