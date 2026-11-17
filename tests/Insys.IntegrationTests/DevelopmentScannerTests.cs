/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : IntegrationTests
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
using Insys.BuildingBlocks.Application.Storage;
using Insys.Documents.Infrastructure.Scanning;
using Insys.Testing;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Insys.IntegrationTests;

/// <summary>
/// The development scanner (Ade, 17 November): how a stack with no ClamAV clears scans, in Development and Testing only. Anywhere else the host doesn't start
/// with it, and it can't even be made.
/// </summary>
public sealed class DevelopmentScannerTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task Host_WithTheDevelopmentScanner_RefusesToStart_OutsideDevelopmentAndTesting_AIA010(string environment)
    {
        await using var factory = new InsysApiFactory(fixture.Database, new Dictionary<string, string?>
        {
            ["Authentication:DevelopmentSigningKey"] = "", ["Testing:DailyJobRunner:Enabled"] = "false", ["Documents:Scanner:Provider"] = "development",
        }, environment);

        var refused = Should.Throw<InvalidOperationException>(() => factory.CreateClient());
        refused.Message.ShouldContain("Documents:Scanner:Provider");
        refused.Message.ShouldContain(environment);
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    [InlineData("development")]
    public void TheDevelopmentScanner_CantBeMade_OutsideDevelopmentAndTesting_AIA010(string environment) =>
        Should.Throw<InvalidOperationException>(() => new DevelopmentMalwareScanner(new Environment(environment)));

    [Fact]
    public async Task InDevelopment_ItFlagsTheEicarTestFile_AndPassesTheRest_SoUploadsStillGoThroughQuarantine_AIA010()
    {
        var scanner = new DevelopmentMalwareScanner(new Environment("Development"));
        (await scanner.Scan("A sample upload."u8.ToArray(), CancellationToken.None)).ShouldBe(ScanVerdict.Passed);
        (await scanner.Scan(System.Text.Encoding.ASCII.GetBytes("sample " + FakeMalwareScanner.Eicar), CancellationToken.None)).Clean.ShouldBeFalse();
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Insys.Api";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
