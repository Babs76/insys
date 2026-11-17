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
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Application.Storage;
using Insys.Documents.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Insys.Documents.Infrastructure.Scanning;

/// <summary>
/// The malware scan of uploads (AIA-010, D-14), for every tenant, onboarding ones included: each pending upload's content is read and scanned with no
/// transaction open (the scan is a network call), then the verdict is recorded in a short command of its own. An infected upload's content is deleted once
/// the verdict is recorded. While the scanner is down, a tenant's uploads stay pending and the next pass tries again: none passes unscanned.
/// </summary>
public sealed class DocumentScanJob(IServiceScopeFactory scopes, ITenantDirectory tenants, IMalwareScanner scanner, IDocumentStorage storage, IOptions<ScannerOptions> options,
    ILogger<DocumentScanJob> logger)
{
    public const string ServiceName = "document-malware-scan";

    /// <returns>How many uploads got a verdict.</returns>
    public async Task<int> Run(CancellationToken cancellationToken)
    {
        var scanned = 0;
        foreach (var tenantId in await tenants.GetActiveTenantIds(cancellationToken))
        {
            IReadOnlyList<PendingScan> pending;
            await using (var scope = Scope(tenantId))
            {
                var listed = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(new ListPendingScans(options.Value.BatchSize), cancellationToken);
                if (listed.IsFailure || listed.Value.Count == 0)
                {
                    continue;
                }

                pending = listed.Value;
            }

            foreach (var upload in pending)
            {
                var content = await storage.Get(tenantId, upload.StorageUri, cancellationToken);
                if (content is null || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)) != upload.Sha256)
                {
                    // Content missing or changed since it was stored: never released, so it is treated as failing the scan.
                    scanned += await Record(tenantId, upload, ScanVerdict.Found("CONTENT_MISMATCH"), cancellationToken);
                    continue;
                }

                var verdict = await scanner.Scan(content, cancellationToken);
                if (verdict.Unavailable)
                {
                    logger.LogWarning("Malware scanner unavailable; {Count} upload(s) of tenant {TenantId} stay in quarantine", pending.Count, tenantId);
                    break;
                }

                scanned += await Record(tenantId, upload, verdict, cancellationToken);
            }
        }

        return scanned;
    }

    private async Task<int> Record(Guid tenantId, PendingScan upload, ScanVerdict verdict, CancellationToken cancellationToken)
    {
        await using var scope = Scope(tenantId);
        var recorded = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new RecordScan(upload.DocumentId, verdict.Clean, verdict.Finding), cancellationToken);
        if (recorded.IsFailure)
        {
            logger.LogError("Recording the scan of a document of tenant {TenantId} failed: {Code}", tenantId, recorded.Error.Code);
            return 0;
        }

        if (!verdict.Clean)
        {
            await storage.Delete(tenantId, upload.StorageUri, cancellationToken);
        }

        return 1;
    }

    private AsyncServiceScope Scope(Guid tenantId)
    {
        var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(tenantId, ServiceName);
        return scope;
    }
}

public sealed class DocumentScanService(DocumentScanJob job, IOptions<ScannerOptions> options, ILogger<DocumentScanService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.JobEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.Value.JobInterval);
        do
        {
            try
            {
                await job.Run(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("The malware scan job failed: {ExceptionType}", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
