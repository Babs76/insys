/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Assist.Tests
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
using Insys.Assist.Application;
using Shouldly;
using Xunit;

namespace Insys.Assist.Tests;

/// <summary>AIA-033a: every rule and rejection of addendum §5.1, in both directions, plus RWF with no decimals, zero, negative amounts and an empty source.</summary>
public sealed class NumberVerifierTests
{
    private static void Same(string a, string b)
    {
        NumberVerifier.Verify($"It is {a} here.", $"The figure is {b}.").Rejected.ShouldBeEmpty($"{a} from {b}");
        NumberVerifier.Verify($"It is {b} here.", $"The figure is {a}.").Rejected.ShouldBeEmpty($"{b} from {a}");
    }

    private static void Rejected(string output, string source) =>
        NumberVerifier.Verify($"It is {output} here.", $"The figure is {source}.").Rejected.ShouldNotBeEmpty($"{output} from {source}");

    [Theory]
    [InlineData("R1,240", "1240")]
    [InlineData("1240", "NGN 1240")]
    [InlineData("R1,240", "1240.00")]
    [InlineData("1,240", "1 240")]
    [InlineData("1 240", "1240")]
    [InlineData("1,240", "1240")]
    [InlineData("1240.00", "1240")]
    [InlineData("1,234,567.50", "1234567.5")]
    [InlineData("15%", "0.15")]
    [InlineData("15 percent", "0.15")]
    [InlineData("15%", "15 percent")]
    [InlineData("12.5%", "0.125")]
    [InlineData("1 November 2026", "2026-11-01")]
    [InlineData("1 Nov 2026", "2026-11-01")]
    [InlineData("RWF 1,240", "1240")]
    [InlineData("1240 RWF", "RWF 1240")]
    [InlineData("0", "0.00")]
    [InlineData("R0", "0")]
    [InlineData("-1240", "-1,240.00")]
    [InlineData("-R50", "-50")]
    public void TheseAreTheSameNumber(string a, string b) => Same(a, b);

    [Theory]
    [InlineData("1241", "1240")]
    [InlineData("about R1,240", "R1,240.50")]
    [InlineData("1240 USD", "1240 NGN")]
    [InlineData("R1240", "NGN 1240")]
    [InlineData("15%", "15")]
    [InlineData("15", "15%")]
    [InlineData("15%", "R15")]
    [InlineData("-50", "50")]
    [InlineData("50", "-50")]
    [InlineData("2 November 2026", "2026-11-01")]
    [InlineData("1,23", "123")]
    [InlineData("1 234,50", "1234.50")]
    public void TheseAreRejected(string output, string source) => Rejected(output, source);

    [Fact]
    public void ACorrectSum_IsRejected_AsItIsNotInTheSource() =>
        NumberVerifier.Verify("The total is R300.", "Premium R100 and fee R200.").Rejected.ShouldBe(["R300"]);

    [Fact]
    public void ListNumbersStepNumbersAndYears_InTheSource_DontTriggerARejection()
    {
        const string source = "1. Rate the risk. Step 2 adds the fee on 1 November 2026. Tax year 2026.";
        NumberVerifier.Verify("Step 2 adds it; 1. rates it, in 2026.", source).Passes.ShouldBeTrue();
        NumberVerifier.Verify("Step 3 adds it.", source).Passes.ShouldBeFalse("a step number not in the source is a number not in the source");
    }

    [Fact]
    public void AYearInASourceDate_MayBeWrittenAlone_ButNotItsDayOrMonth()
    {
        NumberVerifier.Verify("in 2026", "on 1 November 2026").Passes.ShouldBeTrue();
        NumberVerifier.Verify("the 11", "on 2026-11-01").Passes.ShouldBeFalse();
    }

    [Fact]
    public void AnEmptySource_PassesOnlyAnOutputWithNoNumbers()
    {
        NumberVerifier.Verify("Ask someone else to approve.", "").Passes.ShouldBeTrue();
        NumberVerifier.Verify("R5", "").Passes.ShouldBeFalse();
        NumberVerifier.Verify("0", "").Passes.ShouldBeFalse();
    }

    [Fact]
    public void ACodeWithDigits_IsNotANumber() => NumberVerifier.Verify("Rule AIA033 and version V2 say so.", "").Passes.ShouldBeTrue();
}
