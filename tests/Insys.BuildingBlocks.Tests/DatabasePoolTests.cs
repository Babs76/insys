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
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Insys.BuildingBlocks.Tests;

/// <summary>GAP-039: pool sizes per module role, the per-instance budget, and a busy database answered with a retryable 503.</summary>
public sealed class DatabasePoolTests
{
    [Fact]
    public void TheDefaultPools_AddUpTo116PerInstance_AsOq069OptionA()
    {
        DatabasePools.Defaults.Count.ShouldBe(23);
        DatabasePools.Defaults.Values.Sum().ShouldBe(116);
    }

    [Theory]
    [InlineData(768, 20, 3, 249)] // RDS 16 GB, three instances: 116 fits.
    [InlineData(768, 20, 4, 187)] // A fourth during a rolling deployment: 116 still fits.
    [InlineData(150, 20, 1, 130)] // Local compose: max_connections 150.
    [InlineData(100, 20, 1, 80)]  // A default PostgreSQL: 116 does not fit, so the guard refuses to start.
    public void TheBudget_IsMaxConnectionsLessTheReserve_DividedByTheInstances(int max, int reserve, int instances, int budget) =>
        DatabasePools.Budget(max, reserve, instances).ShouldBe(budget);

    [Fact]
    public void ARolesSetting_WinsOverItsConnectionString_WhichWinsOverTheDefault()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Pools:insys_sdm"] = "1" }).Build();

        DatabasePools.MaxPoolSize(configuration, "insys_sdm", "Host=x;Maximum Pool Size=10").ShouldBe(1);
        DatabasePools.MaxPoolSize(configuration, "insys_party", "Host=x;Maximum Pool Size=10").ShouldBe(10);
        DatabasePools.MaxPoolSize(configuration, "insys_party", "Host=x").ShouldBe(4);
    }

    [Fact]
    public void TooManyConnections_AndAnExhaustedPool_Are503DatabaseBusy_NotA500()
    {
        var tooMany = new PostgresException("sorry, too many clients already", "FATAL", "FATAL", PostgresErrorCodes.TooManyConnections);
        var reserved = new PostgresException("remaining connection slots are reserved", "FATAL", "FATAL", PostgresErrorCodes.TooManyConnections);
        var exhausted = new NpgsqlException("The connection pool has been exhausted, either raise 'Max Pool Size' or 'Timeout' value in the connection string.",
            new TimeoutException());
        var wrapped = new InvalidOperationException("An exception has been raised that is likely due to a transient failure.", exhausted);

        foreach (var exception in new Exception[] { tooMany, reserved, exhausted, wrapped })
        {
            InsysExceptionHandler.Classify(exception).ShouldBe((503, "DATABASE_BUSY", "The database is busy. Retry shortly."));
        }

        InsysExceptionHandler.Classify(new PostgresException("boom", "ERROR", "ERROR", PostgresErrorCodes.SyntaxError)).Status.ShouldBe(500);
    }
}
