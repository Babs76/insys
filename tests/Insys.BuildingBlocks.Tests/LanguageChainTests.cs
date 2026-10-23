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
using System.Text.Json;
using Insys.BuildingBlocks.Application.Localisation;
using Insys.BuildingBlocks.Infrastructure.StaticData;

namespace Insys.BuildingBlocks.Tests;

/// <summary>The request language and its fallback chain (docs/specs/addenda/localisation.md §1 to §3, OQ-068).</summary>
public sealed class LanguageChainTests
{
    private static readonly TenantLanguages SouthAfrica = new(["en", "af", "zu", "xh"]);
    private static readonly TenantLanguages AfrikaansFirst = new(["af", "en"]);

    [Fact]
    public void AcceptLanguage_IsReadByQuality_PrimarySubtagOnly_IgnoringWildcardsAndRefusals()
    {
        LanguageChain.Parse("en;q=0.4, zu-ZA, af;q=0.8, *;q=0.1, fr;q=0, x-klingon, DE").ShouldBe(["zu", "de", "af", "en"]);
        LanguageChain.Parse(null).ShouldBeEmpty();
        LanguageChain.Parse("  ").ShouldBeEmpty();
    }

    [Fact]
    public void RequestLanguage_IsTheFirstSupported_ElseTheTenantDefault_ElseEnglish()
    {
        LanguageChain.Resolve(["fr", "zu", "af"], SouthAfrica).ShouldBe("zu");
        LanguageChain.Resolve(["fr"], AfrikaansFirst).ShouldBe("af");
        LanguageChain.Resolve([], AfrikaansFirst).ShouldBe("af");
        LanguageChain.Resolve(["af"], TenantLanguages.EnglishOnly).ShouldBe("en");
        new TenantLanguages([]).Default.ShouldBe("en");
    }

    [Fact]
    public void FallbackChain_IsPreferred_ThenTenantDefault_ThenEnglish_WithoutRepeats()
    {
        LanguageChain.Chain("zu", AfrikaansFirst).ShouldBe(["zu", "af", "en"]);
        LanguageChain.Chain("en", SouthAfrica).ShouldBe(["en"]);
        LanguageChain.Chain(null, AfrikaansFirst).ShouldBe(["af", "en"]);
        LanguageChain.Pick(["zu", "af", "en"], l => l == "af" ? "Maandeliks" : null).ShouldBe(("af", "Maandeliks"));
        LanguageChain.Pick(["zu"], _ => "").ShouldBeNull();
    }

    [Fact]
    public void StaticDataDisplayLabel_FollowsTheChain_ThenTheLabelColumn_AsEnglish()
    {
        var record = new Frequency { Label = "Monthly", Labels = JsonSerializer.SerializeToElement(new { en = "Monthly (en)", zu = "Nyanga zonke" }) };
        StaticLabels.Localise(record, ["zu", "en"]);
        (record.DisplayLabel, record.DisplayLabelLanguage).ShouldBe(("Nyanga zonke", "zu"));
        StaticLabels.Localise(record, ["xh", "en"]);
        (record.DisplayLabel, record.DisplayLabelLanguage).ShouldBe(("Monthly (en)", "en"));
        StaticLabels.Localise(record, ["xh"]);
        (record.DisplayLabel, record.DisplayLabelLanguage).ShouldBe(("Monthly", "en"));
        StaticLabels.Localise(new Frequency(), ["en"]).DisplayLabel.ShouldBeNull();
    }

    private sealed class Frequency : StaticRecord
    {
        public string? Label { get; set; }
    }
}
