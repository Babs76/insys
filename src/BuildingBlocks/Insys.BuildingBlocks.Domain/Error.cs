/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Domain
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
namespace Insys.BuildingBlocks.Domain;

/// <summary>How an error maps to HTTP (PROMPT §4.13).</summary>
public enum ErrorKind
{
    Validation,
    Conflict,
    NotFound,
    Forbidden,
    PreconditionFailed,
    PreconditionRequired,
    Unauthorized,
}

/// <summary>A failure with a stable code from the error code catalogue (integration.ref_error_code_catalogue).</summary>
public sealed record Error(string Code, string Message, ErrorKind Kind)
{
    public IReadOnlyDictionary<string, string[]>? Details { get; init; }

    public static Error Validation(string code, string message, IReadOnlyDictionary<string, string[]>? details = null) =>
        new(code, message, ErrorKind.Validation) { Details = details };

    public static Error Conflict(string code, string message) => new(code, message, ErrorKind.Conflict);

    public static Error NotFound(string code, string message) => new(code, message, ErrorKind.NotFound);

    public static Error Forbidden(string code, string message) => new(code, message, ErrorKind.Forbidden);

    public static Error PreconditionFailed(string code, string message) => new(code, message, ErrorKind.PreconditionFailed);
}
