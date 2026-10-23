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
using System.Buffers.Text;

namespace Insys.BuildingBlocks.Api;

/// <summary>A page of results with an opaque cursor for the next page.</summary>
public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>The body of a response that returns only a new resource's identifier.</summary>
public sealed record CreatedId(Guid Id);

/// <summary>
/// Cursor pagination over UUIDv7 IDs, which sort by creation time. TODO(spec): page size limits are not specified.
/// </summary>
public static class Cursor
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public static string Encode(Guid lastId) => Base64Url.EncodeToString(lastId.ToByteArray(bigEndian: true));

    public static bool TryDecode(string? cursor, out Guid? afterId)
    {
        afterId = null;
        if (string.IsNullOrEmpty(cursor))
        {
            return true;
        }

        try
        {
            var bytes = Base64Url.DecodeFromChars(cursor);
            if (bytes.Length != 16)
            {
                return false;
            }

            afterId = new Guid(bytes, bigEndian: true);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static int ClampLimit(int? limit) => limit is null or < 1 ? DefaultLimit : Math.Min(limit.Value, MaxLimit);
}
