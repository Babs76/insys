/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Application
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
using Insys.BuildingBlocks.Domain;

namespace Insys.BuildingBlocks.Application;

// AIA-086 (D-11, decided 1 November 2026): a tenant's setup is applied as one SETUP change set, one import batch per table through the existing import path
// (BR-016). Every module's static data tables answer through this port, each on its own module's connection and database role (ADR-001); the caller never
// sees another module's tables or context.

/// <summary>A static data table a SETUP change set can load.</summary>
/// <param name="Table">"schema.table", as a pack names it.</param>
/// <param name="TakesTenantRows">False for a platform-global table (scope G): its rows load once, by the platform, when an overlay is approved (§4.6).</param>
/// <param name="Order">The table's place in the dependency order: reference data, then master data, then configuration (see <see cref="SetupOrder"/>).</param>
public sealed record SetupTable(string Table, string EntityCode, int Tier, bool TakesTenantRows, int Order);

/// <summary>One line to load: the row's code and its values by column name, as a pack gives them.</summary>
/// <param name="Line">The line's number in its batch, from 1, which errors refer to.</param>
public sealed record SetupLine(int Line, string Code, JsonElement Values, string Reason);

/// <summary>A line the table refuses, and why: a field and the table's own message. Never the value.</summary>
public sealed record SetupLineError(int Line, string Code, string Field, string Message);

/// <summary>A batch loaded and submitted to its set: its lines are pending the set's approval, bound to this hash (BR-016).</summary>
public sealed record SetupBatch(string Table, string EntityCode, Guid BatchId, Guid ChangeRequestId, int Lines, string Hash);

public interface ISetupImports
{
    /// <summary>The table, or null when no module owns it.</summary>
    SetupTable? Table(string table);

    /// <summary>
    /// The lines as the import would take them, without saving anything: each mapped onto the table's fields (an unknown field is an error), checked as the
    /// import checks a line, and inserted in a transaction that is rolled back, so the database's own checks are run too.
    /// </summary>
    Task<IReadOnlyList<SetupLineError>> Check(string table, IReadOnlyList<SetupLine> lines, CancellationToken cancellationToken);

    /// <summary>
    /// Imports the lines as drafts in batch <paramref name="batchId"/>, all or nothing, and submits the batch to SETUP change set <paramref name="setId"/>
    /// (BR-016's submission, with no approval of its own). Resumable: a batch already imported is submitted, and one already submitted to that set is returned
    /// as it is, so a retry after a failure part-way through a setup does nothing twice.
    /// </summary>
    Task<Result<SetupBatch>> ImportToSet(string table, IReadOnlyList<SetupLine> lines, Guid batchId, Guid setId, CancellationToken cancellationToken);
}

/// <summary>
/// The dependency order a SETUP change set is applied in (AIA-086, OQ-178): every table after the tables of the set it refers to, from the catalogue's Ref
/// fields (<see cref="StaticDataReferences"/>). Where nothing orders two tables, reference data (ref_) goes before master data (mst_), then configuration
/// (cfg_), then by name. Tables that refer to each other round a cycle are put in that same fallback order, as nothing can put each after the others.
/// </summary>
public static class SetupOrder
{
    /// <summary>The fallback rank: reference data 0, master data 1, configuration 2.</summary>
    public static int Of(string table)
    {
        var name = table[(table.IndexOf('.', StringComparison.Ordinal) + 1)..];
        return name.StartsWith("ref_", StringComparison.Ordinal) ? 0 : name.StartsWith("mst_", StringComparison.Ordinal) ? 1 : 2;
    }

    /// <summary>The tables in the order they are applied: a topological order of their references within the set, ties broken by <see cref="Of"/> and name.</summary>
    public static IReadOnlyList<string> Sort(IEnumerable<string> tables, IReadOnlyDictionary<string, string[]>? references = null)
    {
        references ??= StaticDataReferences.Refers;
        var set = tables.Distinct(StringComparer.Ordinal).ToList();
        var waitingOn = set.ToDictionary(t => t, t => (references.TryGetValue(t, out var r) ? r : []).Where(x => x != t && set.Contains(x)).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
        var order = new List<string>();
        while (waitingOn.Count > 0)
        {
            var ready = waitingOn.Where(w => w.Value.Count == 0).Select(w => w.Key).ToList();
            // A cycle: the rest refer round to each other, so the fallback order decides the next.
            var next = (ready.Count > 0 ? ready : [.. waitingOn.Keys]).OrderBy(t => Of(t)).ThenBy(t => t, StringComparer.Ordinal).First();
            order.Add(next);
            waitingOn.Remove(next);
            foreach (var rest in waitingOn.Values)
            {
                rest.Remove(next);
            }
        }

        return order;
    }

    /// <summary>The batch's stable id in a setup: the same run, attempt and table always give the same batch, so a retry finds what it imported.</summary>
    public static Guid BatchId(Guid runId, int attempt, string table)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"SETUP:{runId:N}:{attempt}:{table}"));
        var id = bytes[..16];
        id[7] = (byte)((id[7] & 0x0F) | 0x80); // version 8 (custom), in .NET's little-endian layout of the time-high field
        id[8] = (byte)((id[8] & 0x3F) | 0x80); // RFC 9562 variant
        return new Guid(id);
    }
}
