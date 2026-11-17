/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Documents
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
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Insys.BuildingBlocks.Application.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Insys.Documents.Infrastructure.Scanning;

/// <summary>Documents:Scanner. With no host, there is no scanner, and every upload waits in quarantine (AIA-010).</summary>
public sealed class ScannerOptions
{
    /// <summary>The ClamAV daemon (clamd), run as its own service: for example "clamav" in the deployment, "localhost" with docker compose.</summary>
    public string? Host { get; set; }

    public int Port { get; set; } = 3310;

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>clamd's StreamMaxLength; a larger upload can't be scanned, so it waits as if the scanner were down.</summary>
    public long MaxBytes { get; set; } = 25 * 1024 * 1024;

    public bool JobEnabled { get; set; } = true;

    public TimeSpan JobInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Uploads scanned per tenant each pass.</summary>
    public int BatchSize { get; set; } = 20;
}

/// <summary>
/// ClamAV over clamd's INSTREAM protocol on TCP (AIA-010, D-14): a separate service, never linked, so its GPL-2.0 licence doesn't reach INSYS. The reply is
/// "stream: OK" (clean), "stream: &lt;name&gt; FOUND" (infected) or an error. Anything else, a refused connection or a timeout, is Down: the upload waits.
/// </summary>
public sealed class ClamAvScanner(IOptions<ScannerOptions> options, ILogger<ClamAvScanner> logger) : IMalwareScanner
{
    private const int Chunk = 64 * 1024;

    public async Task<ScanVerdict> Scan(byte[] content, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.Host))
        {
            return ScanVerdict.Down;
        }

        if (content.LongLength > settings.MaxBytes)
        {
            logger.LogWarning("An upload of {Bytes} bytes is over the scanner's limit; it waits in quarantine", content.LongLength);
            return ScanVerdict.Down;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(settings.Host, settings.Port, timeout.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync("zINSTREAM\0"u8.ToArray(), timeout.Token);
            var size = new byte[4];
            for (var offset = 0; offset < content.Length; offset += Chunk)
            {
                var length = Math.Min(Chunk, content.Length - offset);
                BinaryPrimitives.WriteUInt32BigEndian(size, (uint)length);
                await stream.WriteAsync(size, timeout.Token);
                await stream.WriteAsync(content.AsMemory(offset, length), timeout.Token);
            }

            BinaryPrimitives.WriteUInt32BigEndian(size, 0);
            await stream.WriteAsync(size, timeout.Token);
            await stream.FlushAsync(timeout.Token);
            return Read(await ReplyAsync(stream, timeout.Token));
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("The malware scanner is unavailable: {ExceptionType}; uploads wait in quarantine", ex.GetType().Name);
            return ScanVerdict.Down;
        }
    }

    /// <summary>clamd's reply to INSTREAM, as a verdict. Only "OK" is clean; only "&lt;name&gt; FOUND" is infected; anything else is Down.</summary>
    public static ScanVerdict Read(string reply)
    {
        var text = reply.TrimEnd('\0', '\n', '\r').Trim();
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        var result = colon < 0 ? text : text[(colon + 1)..].Trim();
        if (result == "OK")
        {
            return ScanVerdict.Passed;
        }

        return result.EndsWith(" FOUND", StringComparison.Ordinal) && result.Length > " FOUND".Length
            ? ScanVerdict.Found(result[..^" FOUND".Length].Trim())
            : ScanVerdict.Down;
    }

    private static async Task<string> ReplyAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        var reply = new StringBuilder();
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            reply.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (buffer[read - 1] == 0 || reply.Length > 4096)
            {
                break;
            }
        }

        return reply.ToString();
    }
}
