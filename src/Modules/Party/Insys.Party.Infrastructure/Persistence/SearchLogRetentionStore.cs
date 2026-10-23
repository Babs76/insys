/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Party
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
using System.Data.Common;
using Insys.Party.Application;
using Insys.Party.Domain.Parties;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Insys.Party.Infrastructure.Persistence;

/// <summary>The search log's retention through V0030's functions (the log is insert-only for the module's role), and its legal holds.</summary>
internal sealed class SearchLogRetentionStore(PartyDbContext db) : ISearchLogRetention
{
    public Task<int> Minimise(DateTimeOffset before, CancellationToken cancellationToken) => Call("party.minimise_search_log", before, cancellationToken);

    public Task<int> Purge(DateTimeOffset before, CancellationToken cancellationToken) => Call("party.purge_search_log", before, cancellationToken);

    public Task<SearchLogHold?> Hold(Guid id, CancellationToken cancellationToken) => db.Set<SearchLogHold>().SingleOrDefaultAsync(h => h.Id == id, cancellationToken);

    public async Task<IReadOnlyList<SearchLogHold>> Holds(CancellationToken cancellationToken) =>
        await db.Set<SearchLogHold>().AsNoTracking().OrderBy(h => h.Id).ToListAsync(cancellationToken);

    public void Add(SearchLogHold hold) => db.Set<SearchLogHold>().Add(hold);

    private async Task<int> Call(string function, DateTimeOffset before, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("Retention runs inside the command's transaction.");
        command.CommandText = $"SELECT {function}(@before)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "before";
        parameter.Value = before;
        command.Parameters.Add(parameter);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }
}
