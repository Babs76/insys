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
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Insys.BuildingBlocks.Application.Localisation;

namespace Insys.BuildingBlocks.Infrastructure.StaticData;

/// <summary>
/// Chooses a static data record's display text for a request (docs/specs/addenda/localisation.md §3, OQ-068): the first
/// language of the fallback chain present in labels, else the table's own label column (treated as English).
/// </summary>
public static class StaticLabels
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> LabelColumns = new();

    public static T Localise<T>(T record, IReadOnlyList<string> chain) where T : StaticRecord
    {
        var picked = LanguageChain.Pick(chain, language => Text(record.Labels, language));
        if (picked is { } chosen)
        {
            (record.DisplayLabel, record.DisplayLabelLanguage) = (chosen.Text, chosen.Language);
        }
        else if (LabelColumn(record.GetType())?.GetValue(record) is string { Length: > 0 } column)
        {
            (record.DisplayLabel, record.DisplayLabelLanguage) = (column, TenantLanguages.English);
        }
        else
        {
            (record.DisplayLabel, record.DisplayLabelLanguage) = (null, null);
        }

        return record;
    }

    public static IReadOnlyList<T> Localise<T>(IReadOnlyList<T> records, IReadOnlyList<string> chain) where T : StaticRecord
    {
        foreach (var record in records)
        {
            Localise(record, chain);
        }

        return records;
    }

    private static string? Text(JsonElement? labels, string language) =>
        labels is { ValueKind: JsonValueKind.Object } l && l.TryGetProperty(language, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;

    private static PropertyInfo? LabelColumn(Type type) =>
        LabelColumns.GetOrAdd(type, t => t.GetProperty("Label", BindingFlags.Public | BindingFlags.Instance) is { PropertyType: var p } property && p == typeof(string) ? property : null);
}
