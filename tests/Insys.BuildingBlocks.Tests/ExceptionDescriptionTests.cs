/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Tests
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
using Insys.BuildingBlocks.Api;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Xunit;

namespace Insys.BuildingBlocks.Tests;

/// <summary>
/// An unhandled exception's log line names what failed, down its inner exceptions, with a database error's SQLSTATE and constraint and a
/// client library's error code, so a 500 can be diagnosed from the log (rotate-passwords run 42080719155); never a message or a detail,
/// which may hold data values.
/// </summary>
public sealed class ExceptionDescriptionTests
{
    private const string Value = "8001015009087";   // a value a message could carry: never in the description

    [Fact]
    public void ADatabaseFailure_IsDescribedByItsTypes_SqlState_AndConstraint_NeverItsMessageOrDetail()
    {
        var database = new PostgresException($"duplicate key {Value}", "ERROR", "ERROR", "23505", detail: $"Key (number)=({Value}) already exists.", constraintName: "uq_party_identifier");
        var failure = new DbUpdateException($"An error occurred saving {Value}.", database);

        var described = InsysExceptionHandler.Describe(failure);

        described.ShouldBe("Microsoft.EntityFrameworkCore.DbUpdateException <- Npgsql.PostgresException (SQLSTATE 23505, constraint uq_party_identifier)");
        described.ShouldNotContain(Value);
    }

    [Fact]
    public void AClientLibrarysFailure_IsDescribedWithItsErrorCode_NeverItsMessage()
    {
        var failure = new InvalidOperationException($"Storing {Value} failed.", new StorageFailure($"Access denied for {Value}", "InvalidAccessKeyId"));

        var described = InsysExceptionHandler.Describe(failure);

        described.ShouldBe($"System.InvalidOperationException <- {typeof(StorageFailure).FullName} (code InvalidAccessKeyId)");
        described.ShouldNotContain(Value);
    }

    /// <summary>As an S3 client's exception: an ErrorCode beside its message.</summary>
    private sealed class StorageFailure(string message, string errorCode) : Exception(message)
    {
        public string ErrorCode { get; } = errorCode;
    }
}
