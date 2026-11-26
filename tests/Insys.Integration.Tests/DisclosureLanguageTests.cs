/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Integration.Tests
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
using Insys.BuildingBlocks.Application;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.Integration.Application;

namespace Insys.Integration.Tests;

/// <summary>DE-039 (Ade, 25 November 2026): the language an outstanding notice is opened in, and what DISCLOSURE_REQUIRED carries (F060).</summary>
public sealed class DisclosureLanguageTests
{
    [Fact]
    public void TheNotice_IsOpenedInThePartysPreferredLanguage_WhenATextIsInForceInIt_DE039() =>
        DisclosureLanguage.Choose(["af", "en"], "af", "en").ShouldBe("af");

    [Fact]
    public void WithoutATextInThePreferredLanguage_TheTenantsDefaultIsUsed_DE039() =>
        DisclosureLanguage.Choose(["en", "zu"], "af", "zu").ShouldBe("zu");

    [Fact]
    public void WithNoPreferredLanguage_TheTenantsDefaultIsUsed_DE039() =>
        DisclosureLanguage.Choose(["af", "en"], null, "en").ShouldBe("en");

    [Fact]
    public void WithNeitherAvailable_TheOnlyLanguageTheNoticeExistsIn_IsUsed_DE039() =>
        DisclosureLanguage.Choose(["xh"], "af", "en").ShouldBe("xh");

    /// <summary>Ade, 25 November 2026: no "first by code"; the person chooses among the languages in force.</summary>
    [Fact]
    public void WithSeveralAndNoneMatching_NoLanguageIsChosen_DE039() =>
        DisclosureLanguage.Choose(["zu", "xh", "st"], "af", "en").ShouldBeNull();

    [Fact]
    public void WithSeveral_NoPreferredLanguage_AndNoTenantDefault_NoLanguageIsChosen_DE039() =>
        DisclosureLanguage.Choose(["af", "en"], null, null).ShouldBeNull();

    [Fact]
    public void ThePreferredLanguage_MatchesWhateverItsCase_AndTheTextsSpellingIsKept_DE039() =>
        DisclosureLanguage.Choose(["af", "en"], "AF ", "en").ShouldBe("af");

    [Fact]
    public void DisclosureRequired_KeepsItsCodeAndWording_AndNamesTheNotices_TheParty_AndTheRecord_DE039_F060()
    {
        var party = Guid.CreateVersion7();
        var quote = Guid.CreateVersion7();
        var error = DisclosureRefusal.For("ISSUE",
            [new DisclosureNotice("POLICY_TERMS", "ZA", "en", 3, ["en"]), new DisclosureNotice("COOLING_OFF", "ZA", "af", 1, ["af", "en"]), new DisclosureNotice("PRIVACY", "ZA", null, null, ["xh", "zu"])],
            party, "GENERAL_QUOTE", quote);

        (error.Code, error.Message, error.Kind).ShouldBe((ErrorCodes.DisclosureRequired, "The ISSUE disclosures aren't acknowledged yet.", ErrorKind.Conflict));
        var json = JsonSerializer.SerializeToElement(error.Extensions, JsonSerializerOptions.Web);
        json.GetProperty("displayPoint").GetString().ShouldBe("ISSUE");
        json.GetProperty("noticeTypes").EnumerateArray().Select(n => n.GetString()).ShouldBe(["COOLING_OFF", "POLICY_TERMS", "PRIVACY"]);
        var notice = json.GetProperty("notices")[1];
        (notice.GetProperty("noticeType").GetString(), notice.GetProperty("jurisdiction").GetString(), notice.GetProperty("language").GetString(), notice.GetProperty("textVersion").GetInt32())
            .ShouldBe(("COOLING_OFF", "ZA", "af", 1));
        notice.GetProperty("availableLanguages").EnumerateArray().Select(l => l.GetString()).ShouldBe(["af", "en"]);
        var unchosen = json.GetProperty("notices")[2];
        (unchosen.GetProperty("language").ValueKind, unchosen.GetProperty("textVersion").ValueKind).ShouldBe((JsonValueKind.Null, JsonValueKind.Null));
        unchosen.GetProperty("availableLanguages").EnumerateArray().Select(l => l.GetString()).ShouldBe(["xh", "zu"]);
        (json.GetProperty("partyId").GetGuid(), json.GetProperty("businessRefType").GetString(), json.GetProperty("businessRefId").GetGuid()).ShouldBe((party, "GENERAL_QUOTE", quote));
    }
}
