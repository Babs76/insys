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
using Shouldly;

namespace Insys.BuildingBlocks.Tests;

/// <summary>The commit the build recorded (Ade, 27 November 2026): only a full 40-character commit after '+' is shown.</summary>
public sealed class BuildRevisionTests
{
    [Theory]
    [InlineData("1.0.0+6ebe19958cef5eab7b775ce3ed5385cded90814b", "6ebe19958cef5eab7b775ce3ed5385cded90814b")]
    [InlineData("1.0.0-beta+6ebe19958cef5eab7b775ce3ed5385cded90814b", "6ebe19958cef5eab7b775ce3ed5385cded90814b")]
    [InlineData("1.0.0", null)]
    [InlineData("1.0.0+", null)]
    [InlineData("1.0.0+6ebe199", null)]
    [InlineData("1.0.0+6EBE19958CEF5EAB7B775CE3ED5385CDED90814B", null)]
    [InlineData("1.0.0+6ebe19958cef5eab7b775ce3ed5385cded90814b.dirty", null)]
    [InlineData(null, null)]
    public void OnlyAFullCommitAfterThePlusIsShown(string? informationalVersion, string? commit) =>
        BuildRevision.From(informationalVersion).ShouldBe(commit);

    /// <summary>A build from a git checkout records its commit (the SDK's source revision), so tests and CI see the real one.</summary>
    [Fact]
    public void ThisBuildRecordsAFullCommit() =>
        BuildRevision.Commit.ShouldNotBeNull().ShouldMatch("^[0-9a-f]{40}$");
}
