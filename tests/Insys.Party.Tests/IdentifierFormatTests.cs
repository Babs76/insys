/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Party
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
using Insys.Party.Domain.Parties;

namespace Insys.Party.Tests;

/// <summary>A valid number can't become a server error because the machine stalled, and a tenant's pattern can't hang a request.</summary>
public sealed class IdentifierFormatTests
{
    [Theory]
    [InlineData("8001015009087", @"\d{13}", true)]
    [InlineData("80010150090", @"\d{13}", false)]
    [InlineData("A1234", "A|B", false)] // the whole number has to match, not a part of it
    [InlineData("B", "A|B", true)]
    public void TheWholeNumberHasToMatchThePattern(string number, string pattern, bool expected) =>
        Assert.Equal(expected, IdentifierFormat.Matches(number, pattern));

    [Fact]
    public void APatternThatWouldBacktrackForever_IsDecidedAtOnce_WithNoTimeoutToThrow()
    {
        var number = new string('a', 5000) + "!";
        var started = System.Diagnostics.Stopwatch.StartNew();

        Assert.False(IdentifierFormat.Matches(number, "(a+)+"));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(4), $"took {started.Elapsed}");
    }

    [Theory]
    [InlineData("AB12", true)]
    [InlineData("ABCD", false)]
    public void APatternWithALookaround_StillWorks(string number, bool expected) =>
        Assert.Equal(expected, IdentifierFormat.Matches(number, @"(?=.*\d)[A-Z0-9]{4}"));
}
