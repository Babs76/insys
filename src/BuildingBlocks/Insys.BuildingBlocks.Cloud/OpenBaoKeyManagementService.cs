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
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Insys.BuildingBlocks.Application.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Insys.BuildingBlocks.Cloud;

/// <summary>
/// OpenBao Transit settings (Kms; ADR-027). "vault" names the same adapter, as Vault's Transit API is the same. The token comes
/// from the secret store, never from the repository; the Transit key is created on the server and named here.
/// </summary>
public sealed class OpenBaoKmsOptions
{
    /// <summary>The server's address, such as https://openbao.insys.svc:8200.</summary>
    public string Address { get; set; } = "";

    public string Token { get; set; } = "";

    /// <summary>The Transit key that protects the data keys. It must never be deleted: the data would become unreadable.</summary>
    public string KeyName { get; set; } = "";

    /// <summary>Where the Transit secrets engine is mounted.</summary>
    public string Mount { get; set; } = "transit";

    /// <summary>Retries after throttling (429), a 5xx (including a sealed server), a timeout or a lost connection, with exponential back-off.</summary>
    public int MaxAttempts { get; set; } = 6;

    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// OpenBao Transit for envelope encryption (Kms:Provider "openbao" or "vault"; ADR-018, ADR-027). A data key's stored ciphertext is
/// Transit's own text, with its key version, so a rotated Transit key still decrypts the data keys it protected. The token and the
/// keys are never logged.
/// </summary>
public sealed class OpenBaoKeyManagementService(HttpClient http, IOptions<OpenBaoKmsOptions> options, ILogger<OpenBaoKeyManagementService> logger) : IKeyManagementService
{
    /// <summary>The wait before a retry; tests replace it to run without real delays.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public string MasterKeyRef => $"openbao:{options.Value.Mount}:{options.Value.KeyName}";

    public async Task<DataKey> CreateDataKey(CancellationToken cancellationToken)
    {
        var data = await Post("datakey/plaintext", new { bits = 256 }, cancellationToken);
        return new DataKey(Convert.FromBase64String(data.GetProperty("plaintext").GetString()!), Encoding.UTF8.GetBytes(data.GetProperty("ciphertext").GetString()!));
    }

    public async Task<byte[]> DecryptDataKey(byte[] ciphertext, CancellationToken cancellationToken)
    {
        var data = await Post("decrypt", new { ciphertext = Encoding.UTF8.GetString(ciphertext) }, cancellationToken);
        return Convert.FromBase64String(data.GetProperty("plaintext").GetString()!);
    }

    private async Task<JsonElement> Post(string operation, object body, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var url = $"{settings.Address.TrimEnd('/')}/v1/{settings.Mount}/{operation}/{Uri.EscapeDataString(settings.KeyName)}";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(settings.RequestTimeout);
                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
                request.Headers.Add("X-Vault-Token", settings.Token);
                using var response = await http.SendAsync(request, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    throw new OpenBaoException(operation, response.StatusCode);
                }

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                return document.RootElement.GetProperty("data").Clone();
            }
            catch (Exception ex) when (attempt < settings.MaxAttempts && !cancellationToken.IsCancellationRequested && IsTransient(ex))
            {
                var wait = TimeSpan.FromMilliseconds(Math.Min(settings.MaxDelay.TotalMilliseconds,
                    settings.InitialDelay.TotalMilliseconds * Math.Pow(2, attempt - 1) * (0.8 + (Random.Shared.NextDouble() * 0.4))));
                logger.LogWarning("OpenBao Transit {Operation} attempt {Attempt} failed ({ExceptionType}); retrying in {WaitMilliseconds} ms", operation, attempt,
                    ex.GetType().Name, Math.Round(wait.TotalMilliseconds));
                await Delay(wait, cancellationToken);
            }
        }
    }

    /// <summary>Throttling (429), a server error (503 when sealed), a timeout or a lost connection. A refusal (403, 400) is not retried.</summary>
    public static bool IsTransient(Exception exception) => exception switch
    {
        OpenBaoException { StatusCode: HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError } => true,
        HttpRequestException or TaskCanceledException => true,
        _ => false,
    };
}

/// <summary>OpenBao refused a call. It says only the operation and the status: never the request, which holds key material.</summary>
public sealed class OpenBaoException(string operation, HttpStatusCode statusCode) : Exception($"OpenBao Transit {operation} answered {(int)statusCode}.")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
