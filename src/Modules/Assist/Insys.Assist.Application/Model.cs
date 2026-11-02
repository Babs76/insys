/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Assist
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
using System.Text.RegularExpressions;

namespace Insys.Assist.Application;

/// <param name="Instructions">The prompt template's text: INSYS's own words.</param>
/// <param name="Data">The minimised data, delimited by <see cref="PromptData"/>: never instructions (AIA-061).</param>
public sealed record AiModelRequest(string ModelVersion, string PromptVersion, string Instructions, string Data);

public sealed record AiModelResponse(string Text, long InputTokens, long OutputTokens);

/// <summary>
/// The one interface for model calls (SRS §7.1). It sends text and receives text: it has no tools, so the model can't write, approve, pay or send (AIA-060).
/// Batch 7.1 has only the test stub. A real provider joins the Cloud adapter project in 7.3, behind ADR-027's SDK guard.
/// </summary>
public interface IAiModelProvider
{
    Task<AiModelResponse> Complete(AiModelRequest request, CancellationToken cancellationToken);
}

/// <summary>A provider's failure: the caller gets ASSIST_UNAVAILABLE, and INSYS carries on (P8).</summary>
public sealed class AiModelUnavailableException(string message) : Exception(message);

/// <summary>Wraps data so that nothing inside it can close the delimiter and pass as instructions (AIA-061).</summary>
public static partial class PromptData
{
    public const string Open = "<insys-data>";
    public const string Close = "</insys-data>";

    public static string Delimit(string data) => $"{Open}\n{Tags().Replace(data, "[removed]")}\n{Close}";

    [GeneratedRegex(@"<\s*/?\s*insys-data[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Tags();
}
