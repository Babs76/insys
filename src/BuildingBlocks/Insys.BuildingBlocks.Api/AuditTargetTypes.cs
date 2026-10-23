/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Api
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
using System.Collections.Concurrent;
using Insys.BuildingBlocks.Domain;

namespace Insys.BuildingBlocks.Api;

/// <summary>
/// The target types audit events name (GAP-029): every aggregate type (record history names a changed aggregate by its
/// type), the static data entity codes (static data changes), the few other records a module registers (Party's
/// BankAccount), and Endpoint (denied requests). As stored: at most 30 characters (audit.audit_event.target_type).
/// The documents declare them as the enum of targetType on the audit operations.
/// </summary>
public static class AuditTargetTypes
{
    public const string Endpoint = "Endpoint";

    private const int MaxLength = 30;

    private static readonly ConcurrentDictionary<string, byte> Registered = new(StringComparer.Ordinal);

    /// <summary>A record type a module audits that is not an aggregate (Party's BankAccount).</summary>
    public static void Register(string targetType) => Registered[Stored(targetType)] = 0;

    public static IReadOnlyList<string> All() =>
        InsysAssemblies()
            .SelectMany(a =>
            {
                try
                {
                    return a.GetExportedTypes();
                }
                catch (System.Reflection.ReflectionTypeLoadException e)
                {
                    return e.Types.OfType<Type>();
                }
            })
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IAggregateRoot).IsAssignableFrom(t) && SchemaNames.Module(t) is not null)
            .Select(t => Stored(t.Name))
            .Concat(Registered.Keys)
            .Concat(StateEnums.EntityCodes.Select(Stored))
            .Append(Endpoint)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Every INSYS assembly the loaded ones reference, loaded if need be, so the list is the same wherever a document is
    /// generated (at build time not every module's domain assembly has been loaded yet).
    /// </summary>
    private static IEnumerable<System.Reflection.Assembly> InsysAssemblies()
    {
        static bool IsInsys(System.Reflection.AssemblyName name) =>
            name.Name?.StartsWith("Insys.", StringComparison.Ordinal) == true && !name.Name.EndsWith("Tests", StringComparison.Ordinal) && name.Name != "Insys.Testing";

        var found = new Dictionary<string, System.Reflection.Assembly>(StringComparer.Ordinal);
        var pending = new Queue<System.Reflection.Assembly>(AppDomain.CurrentDomain.GetAssemblies().Where(a => IsInsys(a.GetName())));
        while (pending.TryDequeue(out var assembly))
        {
            if (!found.TryAdd(assembly.GetName().Name!, assembly))
            {
                continue;
            }

            foreach (var reference in assembly.GetReferencedAssemblies().Where(IsInsys).Where(r => !found.ContainsKey(r.Name!)))
            {
                pending.Enqueue(System.Reflection.Assembly.Load(reference));
            }
        }

        return found.Values;
    }

    private static string Stored(string name) => name.Length <= MaxLength ? name : name[..MaxLength];
}
