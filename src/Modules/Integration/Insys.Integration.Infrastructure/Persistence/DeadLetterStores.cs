/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Integration
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
using System.Text.Json;
using Insys.Integration.Application.DeadLetters;
using Insys.Integration.Domain.DeadLetters;
using Microsoft.EntityFrameworkCore;

namespace Insys.Integration.Infrastructure.Persistence;

internal sealed class DeadLetterRepository(IntegrationDbContext db) : IDeadLetterRepository
{
    public Task<DeadLetter?> Get(Guid id, CancellationToken cancellationToken) =>
        db.DeadLetters.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);

    public void Add(DeadLetter deadLetter) => db.DeadLetters.Add(deadLetter);
}

internal sealed class DeadLetterQueries(IntegrationDbContext db) : IDeadLetterQueries
{
    public async Task<IReadOnlyList<DeadLetterSummary>> List(DeadLetterStatus? status, string? source, Guid? afterId, int limit, CancellationToken cancellationToken)
    {
        var query = db.DeadLetters.AsNoTracking();
        query = status switch
        {
            DeadLetterStatus.DeadLettered => query.Where(d => d.Resolution == null),
            DeadLetterStatus.Replayed => query.Where(d => d.Resolution == DeadLetterResolution.Replayed),
            DeadLetterStatus.Discarded => query.Where(d => d.Resolution == DeadLetterResolution.Discarded),
            _ => query,
        };

        if (!string.IsNullOrEmpty(source))
        {
            var sourceValue = new Source(source);
            query = query.Where(d => d.Source == sourceValue);
        }

        if (afterId is not null)
        {
            query = query.Where(d => d.Id.CompareTo(afterId.Value) > 0);
        }

        var rows = await query.OrderBy(d => d.Id).Take(limit).ToListAsync(cancellationToken);
        return rows.Select(d => new DeadLetterSummary(d.Id, d.Source.Value, d.MessageRef.Value, d.Status.ToString(), d.Attempts, d.FirstFailedAt)).ToList();
    }

    public async Task<DeadLetterDetail?> Get(Guid id, CancellationToken cancellationToken)
    {
        var d = await db.DeadLetters.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (d is null)
        {
            return null;
        }

        using var payload = JsonDocument.Parse(d.Payload);
        return new DeadLetterDetail(d.Id, d.Source.Value, d.MessageRef.Value, d.Status.ToString(), d.Attempts, d.FirstFailedAt, d.Error.Value,
            payload.RootElement.Clone(), d.ResolvedBy, d.RowVersion);
    }
}
