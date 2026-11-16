/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Infrastructure
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
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Insys.BuildingBlocks.Application;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.Persistence;

namespace Insys.BuildingBlocks.Infrastructure.StaticData;

/// <summary>One module's static data tables, as a SETUP change set loads them (AIA-086): on that module's context and role.</summary>
public interface IModuleSetupImports
{
    IReadOnlyList<StaticDataDescriptor> Descriptors { get; }

    Task<IReadOnlyList<SetupLineError>> Check(StaticDataDescriptor descriptor, IReadOnlyList<SetupLine> lines, CancellationToken cancellationToken);

    Task<Result<SetupBatch>> ImportToSet(StaticDataDescriptor descriptor, IReadOnlyList<SetupLine> lines, Guid batchId, Guid setId, CancellationToken cancellationToken);
}

/// <summary>Every module's tables behind one port: a table is found by its "schema.table" name and handed to the module that owns it.</summary>
public sealed class SetupImports(IEnumerable<IModuleSetupImports> modules) : ISetupImports
{
    private readonly IReadOnlyList<IModuleSetupImports> owners = [.. modules];

    public SetupTable? Table(string table) =>
        Find(table) is ({ } descriptor, _) ? new SetupTable(table, descriptor.EntityCode, descriptor.Tier, descriptor.AllowsTenantRows, SetupOrder.Of(table)) : null;

    public async Task<IReadOnlyList<SetupLineError>> Check(string table, IReadOnlyList<SetupLine> lines, CancellationToken cancellationToken) =>
        Find(table) is ({ } descriptor, { } owner)
            ? await owner.Check(descriptor, lines, cancellationToken)
            : [new SetupLineError(0, "", "table", $"No module owns {table}.")];

    public async Task<Result<SetupBatch>> ImportToSet(string table, IReadOnlyList<SetupLine> lines, Guid batchId, Guid setId, CancellationToken cancellationToken) =>
        Find(table) is ({ } descriptor, { } owner)
            ? await owner.ImportToSet(descriptor, lines, batchId, setId, cancellationToken)
            : Error.Validation(ErrorCodes.ValidationFailed, $"No module owns {table}.");

    private (StaticDataDescriptor?, IModuleSetupImports?) Find(string table)
    {
        foreach (var owner in owners)
        {
            if (owner.Descriptors.FirstOrDefault(d => $"{d.Schema}.{d.Table}" == table) is { } descriptor)
            {
                return (descriptor, owner);
            }
        }

        return (null, null);
    }
}

/// <summary>
/// A module's side of the port, on its own <see cref="StaticDataService{TDbContext}"/>. A pack gives a row's values by column name (snake_case); each is mapped
/// onto the table's input field of that name, and a value for a field the table doesn't have is an error, never dropped.
/// </summary>
public sealed class ModuleSetupImports<TDbContext>(StaticDataCatalogue<TDbContext> catalogue, StaticDataService<TDbContext> service) : IModuleSetupImports
    where TDbContext : ModuleDbContext
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly MethodInfo CheckTyped = typeof(ModuleSetupImports<TDbContext>).GetMethod(nameof(CheckLines), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo ImportTyped = typeof(ModuleSetupImports<TDbContext>).GetMethod(nameof(ImportLines), BindingFlags.NonPublic | BindingFlags.Instance)!;

    public IReadOnlyList<StaticDataDescriptor> Descriptors => catalogue.Descriptors;

    public Task<IReadOnlyList<SetupLineError>> Check(StaticDataDescriptor descriptor, IReadOnlyList<SetupLine> lines, CancellationToken cancellationToken) =>
        (Task<IReadOnlyList<SetupLineError>>)CheckTyped.MakeGenericMethod(descriptor.EntityType, descriptor.InputType).Invoke(this, [descriptor, lines, cancellationToken])!;

    public Task<Result<SetupBatch>> ImportToSet(StaticDataDescriptor descriptor, IReadOnlyList<SetupLine> lines, Guid batchId, Guid setId, CancellationToken cancellationToken) =>
        (Task<Result<SetupBatch>>)ImportTyped.MakeGenericMethod(descriptor.EntityType, descriptor.InputType)
            .Invoke(this, [descriptor, lines, batchId, setId, cancellationToken])!;

    private async Task<IReadOnlyList<SetupLineError>> CheckLines<TEntity, TInput>(StaticDataDescriptor descriptor, IReadOnlyList<SetupLine> lines,
        CancellationToken cancellationToken)
        where TEntity : StaticRecord<TInput>, new()
        where TInput : StaticRecordInput
    {
        var (inputs, errors) = Map<TInput>(lines);
        if (errors.Count > 0)
        {
            return errors;
        }

        return [.. (await service.CheckImport<TEntity, TInput>(descriptor, inputs, cancellationToken))
            .Select(e => new SetupLineError(e.Line, e.Line >= 1 && e.Line <= lines.Count ? lines[e.Line - 1].Code : "", e.Field, e.Message))];
    }

    private async Task<Result<SetupBatch>> ImportLines<TEntity, TInput>(StaticDataDescriptor descriptor, IReadOnlyList<SetupLine> lines, Guid batchId, Guid setId,
        CancellationToken cancellationToken)
        where TEntity : StaticRecord<TInput>, new()
        where TInput : StaticRecordInput
    {
        var (inputs, errors) = Map<TInput>(lines);
        if (errors.Count > 0)
        {
            return Error.Validation(StaticDataErrorCodes.ImportRejected, $"The import was rejected: {errors.Count} line error(s). Nothing was saved.",
                errors.GroupBy(e => $"line {e.Line}").ToDictionary(g => g.Key, g => g.Select(e => $"{e.Field}: {e.Message}").ToArray()));
        }

        var submitted = await service.ImportToSet<TEntity, TInput>(descriptor, inputs, batchId, setId, cancellationToken);
        return submitted.IsFailure
            ? submitted.Error
            : new SetupBatch($"{descriptor.Schema}.{descriptor.Table}", descriptor.EntityCode, batchId, submitted.Value.ChangeRequestId, submitted.Value.Lines, submitted.Value.Hash);
    }

    /// <summary>Each line's values as the table's input: the row's code, its reason, and every value under the field its column name gives.</summary>
    internal static (List<TInput> Inputs, List<SetupLineError> Errors) Map<TInput>(IReadOnlyList<SetupLine> lines)
        where TInput : StaticRecordInput
    {
        var inputs = new List<TInput>();
        var errors = new List<SetupLineError>();
        var fields = typeof(TInput).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite)
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).ToHashSet(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (line.Values.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new SetupLineError(line.Line, line.Code, "values", "The values are not a set of fields."));
                continue;
            }

            var node = new JsonObject();
            var unknown = false;
            foreach (var value in line.Values.EnumerateObject())
            {
                var field = Camel(value.Name);
                if (!fields.Contains(field))
                {
                    errors.Add(new SetupLineError(line.Line, line.Code, value.Name, "The table has no such field."));
                    unknown = true;
                    continue;
                }

                node[field] = JsonNode.Parse(value.Value.GetRawText());
            }

            if (unknown)
            {
                continue;
            }

            node["code"] = line.Code;
            node["changeReason"] = line.Reason;
            try
            {
                inputs.Add(node.Deserialize<TInput>(Json) ?? throw new JsonException("No value."));
            }
            catch (JsonException ex)
            {
                // The path names the field; the message is the serializer's, which carries no value.
                errors.Add(new SetupLineError(line.Line, line.Code, Field(ex.Path), ex.Path is null ? "The values can't be read." : "The value isn't of the field's type."));
            }
        }

        return (inputs, errors);
    }

    private static string Field(string? path) => path is { Length: > 2 } ? Snake(path.TrimStart('$', '.').Split('.', '[')[0]) : "values";

    private static string Camel(string snake)
    {
        var parts = snake.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? snake : parts[0] + string.Concat(parts.Skip(1).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private static string Snake(string camel) => JsonNamingPolicy.SnakeCaseLower.ConvertName(camel);
}
