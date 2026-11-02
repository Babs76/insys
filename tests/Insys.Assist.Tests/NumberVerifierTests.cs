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

    [Theory]
    [InlineData("r999")] [InlineData("N999")] [InlineData("Rs999")] [InlineData("Ksh999")] [InlineData("Premium999")] [InlineData("999k")] [InlineData("R5m")] [InlineData("2bn")]
    [InlineData("15pc")] [InlineData("15percent")] [InlineData("FRW999")] [InlineData("AUD999")] [InlineData("US999")] [InlineData("100,5000")] [InlineData("100,99")]
    [InlineData("1240,50")] [InlineData("total.999")] [InlineData(".15")] [InlineData("R1.5m")] [InlineData("fifteen percent")] [InlineData("twelve hundred")] [InlineData("a million")]
    [InlineData("R100 million")] [InlineData("100 billion")] [InlineData("\uFF19\uFF19\uFF19")] [InlineData("\u0669\u0669")] [InlineData("2nd November 2026")] [InlineData("the 3rd")] [InlineData("December 2026")]
    public void WhatIsntReadAsANumber_FailsClosed(string output) =>
        NumberVerifier.Verify($"It is {output} here.", "R100 and 100 and 1240 from 1 November 2026").Passes.ShouldBeFalse(output);

    [Theory]
    [InlineData("1240 dollars")] [InlineData("1240 rand")] [InlineData("US$1240")] [InlineData("A$1240")] [InlineData("KSh 1240")] [InlineData("1240 FCFA")] [InlineData("Frw 1240")]
    [InlineData("usd 1240")] [InlineData("¥1240")] [InlineData("₵1240")]
    public void ACurrencyWrittenAnyWay_MustMatchTheOneStated(string output) => NumberVerifier.Verify(output, "NGN 1240").Passes.ShouldBeFalse(output);

    [Theory]
    [InlineData("100%", "Step 1")] [InlineData("1500%", "15")] [InlineData("200%", "2")] [InlineData("1", "100%")]
    public void APercentage_NeverMatchesAWholeNumber(string output, string source) => NumberVerifier.Verify(output, source).Passes.ShouldBeFalse();

    [Theory]
    [InlineData("\u221250")] [InlineData("R-50")] [InlineData("- 50")] [InlineData("minus 50")]
    public void EveryWayOfWritingAMinus_IsAMinus(string output)
    {
        NumberVerifier.Verify(output, "50").Passes.ShouldBeFalse(output);
        NumberVerifier.Verify("50", output).Passes.ShouldBeFalse(output);
        NumberVerifier.Verify(output, "-50").Passes.ShouldBeTrue(output);
    }

    [Fact]
    public void ANumberTooLongToRead_IsRejected_NotAnException() =>
        NumberVerifier.Verify("99999999999999999999999999999999", "100").Passes.ShouldBeFalse();

    [Fact]
    public void ACodeWithDigits_PassesOnlyIfItIsInTheSource()
    {
        NumberVerifier.Verify("Rule AIA033 says so.", "See rule AIA033 here.").Passes.ShouldBeTrue();
        NumberVerifier.Verify("Rule AIA033 says so.", "").Passes.ShouldBeFalse();
    }
}
