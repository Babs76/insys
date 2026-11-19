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
using System.Text.Json;
using Insys.Assist.Application;
using Insys.Assist.Domain;
using Shouldly;
using Xunit;

namespace Insys.Assist.Tests;

/// <summary>Batch 7.6: a pack file read with its marks' sources (D-20), and the content hash an approval binds to (§5.3).</summary>
public sealed class PackFileTests
{
    private static PackFile Read(string json) => PackFile.Read(JsonDocument.Parse(json).RootElement).Value;

    [Fact]
    public void TheHash_IsTheSame_ForTheSameValuesInAnyKeyOrder_AndChangesWithAMarkOrAValue()
    {
        var a = Read("""{"pack":"P","kind":"LINE","rows":[{"table":"t","code":"A","class":"FINANCE","values":{"rate":0.15,"cap":100}}]}""");
        var b = Read("""{"pack":"P","kind":"LINE","rows":[{"table":"t","code":"A","class":"FINANCE","values":{"cap":100,"rate":0.15}}]}""");
        var marked = Read("""{"pack":"P","kind":"LINE","rows":[{"table":"t","code":"A","class":"FINANCE","regulated":true,"values":{"rate":0.15,"cap":100}}]}""");
        var changed = Read("""{"pack":"P","kind":"LINE","rows":[{"table":"t","code":"A","class":"FINANCE","values":{"rate":0.16,"cap":100}}]}""");
        a.Hash(1, PackScopes.AllTenants).ShouldBe(b.Hash(1, PackScopes.AllTenants));
        marked.Hash(1, PackScopes.AllTenants).ShouldNotBe(a.Hash(1, PackScopes.AllTenants), "the same mark, but now explicit: the reviewer confirmed something else");
        changed.Hash(1, PackScopes.AllTenants).ShouldNotBe(a.Hash(1, PackScopes.AllTenants));
        a.Hash(2, PackScopes.AllTenants).ShouldNotBe(a.Hash(1, PackScopes.AllTenants));
        a.Hash(1, PackScopes.TenantOnly).ShouldNotBe(a.Hash(1, PackScopes.AllTenants));
    }

    [Fact]
    public void ATailorMark_NamesTheColumnsAndAnswers_IsPartOfTheHash_AndARowWithoutOneHashesAsBefore_OQ187()
    {
        const string plain = """{"pack":"P","kind":"BASE","rows":[{"table":"t","code":"A","class":"OPERATIONAL","regulated":false,"tailorable":true,"values":{"zone":null}}]}""";
        const string tailor = """{"pack":"P","kind":"BASE","rows":[{"table":"t","code":"A","class":"OPERATIONAL","regulated":false,"tailorable":true,"tailor":{"columns":["zone"],"from":["timeZone"]},"values":{"zone":null}}]}""";
        var without = Read(plain);
        var with = Read(tailor);
        with.Rows[0].TailorColumns.ShouldBe(["zone"]);
        with.Rows[0].TailorFrom.ShouldBe(["timeZone"]);
        without.Rows[0].TailorColumns.ShouldBeNull();
        with.Hash(1, PackScopes.AllTenants).ShouldNotBe(without.Hash(1, PackScopes.AllTenants), "what tailoring may change is reviewed, so it is hashed");

        // A row without a tailor mark hashes exactly as before 7.8: the line the hash reads is unchanged, so approvals already given still bind.
        var row = without.Rows[0];
        without.Hash(1, PackScopes.AllTenants).ShouldBe(PackHash.Of("P", "BASE", 1, PackScopes.AllTenants, null, null, null,
            [new PackHash.Row(row.LineNo, row.Table, row.Code, row.Class, row.Regulated, row.RegulatedSource, row.Tailorable, row.TailorableSource, row.Values, null, null, null)]));
        without.Hash(1, PackScopes.AllTenants).ShouldBe(Pre78Hash(row));
    }

    /// <summary>The hash as 7.6 computed it (before the tailor mark existed), written out here, so a change to the line format is caught.</summary>
    private static string Pre78Hash(PackRow r)
    {
        static string F(string? v) => v is null ? "\u0000" : v.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        var text = $"P|BASE|1|{PackScopes.AllTenants}|{F(null)}|{F(null)}|{F(null)}\n"
                   + $"{r.LineNo}|{F(r.Table)}|{F(r.Code)}|{r.Class}|{r.Regulated}|{r.RegulatedSource}|{r.Tailorable}|{r.TailorableSource}|{F(PackHash.Canonical(r.Values))}|{F(null)}|{F(null)}|{F(null)}\n";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    [Theory]
    [InlineData("""{"pack":"P","kind":"BASE","rows":[{"table":"t","code":"A","class":"OPERATIONAL","tailor":{"columns":["zone"],"from":["timeZone"]},"values":{"zone":null}}]}""")]
    [InlineData("""{"pack":"P","kind":"BASE","rows":[{"table":"t","code":"A","class":"OPERATIONAL","tailorable":true,"tailor":{"columns":["other"],"from":["timeZone"]},"values":{"zone":null}}]}""")]
    [InlineData("""{"pack":"P","kind":"BASE","rows":[{"table":"t","code":"A","class":"OPERATIONAL","tailorable":true,"tailor":{"columns":["zone"],"from":["favouriteColour"]},"values":{"zone":null}}]}""")]
    [InlineData("""{"pack":"P","kind":"BASE","rows":[{"table":"t","code":"A","class":"OPERATIONAL","tailorable":true,"tailor":{"columns":[],"from":["timeZone"]},"values":{"zone":null}}]}""")]
    [InlineData("""{"pack":"P","kind":"BASE","rows":[{"table":"t","code":"A","class":"OPERATIONAL","tailorable":true,"tailor":true,"values":{"zone":null}}]}""")]
    public void ATailorMark_OnARowNotMarkedTailorable_OrNamingAColumnOrAnswerThatIsntThere_IsRefused_OQ187(string json) =>
        PackFile.Read(JsonDocument.Parse(json).RootElement).IsFailure.ShouldBeTrue();

    [Fact]
    public void ARowMayBeTailored_OnlyWithBothMarksExplicit_NotRegulated_AndItsColumnsAndAnswersNamed_D20_OQ187()
    {
        var pack = Guid.CreateVersion7();
        StarterPackValue Value(bool regulated, string regulatedSource, bool tailorable, string tailorableSource, string[]? columns) =>
            StarterPackValue.For(pack, PackScopes.AllTenants, 1, "t", "A", "OPERATIONAL", regulated, regulatedSource, tailorable, tailorableSource, "{}", null, null, null,
                columns, columns is null ? null : ["timeZone"]);
        Value(false, MarkSources.Explicit, true, MarkSources.Explicit, ["zone"]).MayBeTailored.ShouldBeTrue();
        Value(false, MarkSources.Explicit, true, MarkSources.Explicit, null).MayBeTailored.ShouldBeFalse("nothing named to tailor (OQ-187 (a))");
        Value(true, MarkSources.Explicit, true, MarkSources.Explicit, ["zone"]).MayBeTailored.ShouldBeFalse("a regulated value is never tailored (AIA-082)");
        Value(false, MarkSources.Interim, true, MarkSources.Explicit, ["zone"]).MayBeTailored.ShouldBeFalse("the interim reading never makes a row tailorable (D-20)");
    }

    [Fact]
    public void ACountryOrLinePack_TakesItsCountryOrLineFromItsCode()
    {
        (Read("""{"pack":"NG","kind":"COUNTRY","rows":[{"table":"t","code":"A","class":"OPERATIONAL","values":{}}]}""").Country).ShouldBe("NG");
        (Read("""{"pack":"GENERAL_MOTOR","kind":"LINE","rows":[{"table":"t","code":"A","class":"OPERATIONAL","values":{}}]}""").Line).ShouldBe("GENERAL");
    }

    [Theory]
    [InlineData("""{"kind":"LINE","rows":[]}""")]
    [InlineData("""{"pack":"P","kind":"OTHER","rows":[{"table":"t","code":"A","class":"FINANCE","values":{}}]}""")]
    [InlineData("""{"pack":"P","kind":"LINE","rows":[{"table":"t","code":"A","class":"SECRET","values":{}}]}""")]
    [InlineData("""{"pack":"P","kind":"LINE","rows":[{"table":"t","code":"A","class":"FINANCE","tailorable":"maybe","values":{}}]}""")]
    public void AMalformedFile_IsRefused(string json) => PackFile.Read(JsonDocument.Parse(json).RootElement).IsFailure.ShouldBeTrue();

    [Fact]
    public void AVersion_ForEveryTenant_IsApprovedOnlyAfterReview_ByNeitherItsPreparerNorItsReviewer_AndOnlyForTheHashItHas()
    {
        var pack = StarterPack.Load("P", PackKinds.Line, 1, null, null, "GENERAL", PackScopes.AllTenants, "h", 1, "loader").Value;
        pack.Approve("lead", "h", PackScopes.AllTenants, DateTimeOffset.UnixEpoch).Error!.Code.ShouldBe("INVALID_STATE_TRANSITION");
        pack.MarkReviewed("reviewer", "Partner", "Name", new DateOnly(2026, 11, 14), Guid.CreateVersion7()).IsSuccess.ShouldBeTrue();
        pack.Approve("loader", "h", PackScopes.AllTenants, DateTimeOffset.UnixEpoch).Error!.Code.ShouldBe("MAKER_CHECKER_SAME_PERSON");
        pack.Approve("reviewer", "h", PackScopes.AllTenants, DateTimeOffset.UnixEpoch).Error!.Code.ShouldBe("MAKER_CHECKER_SAME_PERSON");
        pack.Approve("lead", "other", PackScopes.AllTenants, DateTimeOffset.UnixEpoch).Error!.Code.ShouldBe("ASSIST_PACK_CHANGED");
        pack.Approve("lead", "h", PackScopes.TenantOnly, DateTimeOffset.UnixEpoch).Error!.Code.ShouldBe("FORBIDDEN", "a tenant's officer can't approve a platform version");
        pack.Approve("lead", "h", PackScopes.AllTenants, DateTimeOffset.UnixEpoch).IsSuccess.ShouldBeTrue();
        pack.Status.ShouldBe(PackStatuses.Approved);
    }

    private static StarterPackValue Row(Guid pack, int line, string table, string code, bool regulated, string values) =>
        StarterPackValue.For(pack, PackScopes.AllTenants, line, table, code, regulated ? "FINANCE" : "OPERATIONAL", regulated, MarkSources.Interim, false, MarkSources.Default, values, null, null, null);

    [Fact]
    public void ALaterLayer_MergesIntoTheRowFieldByField_ANullMeansNoChange_AndAnUnapprovedVersionsRegulatedValueIsNeverUsed_OQ181_AIA082()
    {
        var basePack = StarterPack.Load("base", PackKinds.Base, 1, null, null, null, PackScopes.AllTenants, "h", 2, "p").Value;
        var country = StarterPack.Load("NG", PackKinds.Country, 1, null, "NG", null, PackScopes.AllTenants, "h", 2, "p").Value;
        var drafted = SetupAssembly.Assemble(
        [
            new AssemblyLayer(basePack, [Row(basePack.Id, 1, "t", "A", false, """{"x":1,"y":2}"""), Row(basePack.Id, 2, "t", "B", true, """{"rate":0.1,"basis":"GROSS"}""")], true),
            new AssemblyLayer(country, [Row(country.Id, 1, "t", "A", false, """{"x":null,"y":3}"""), Row(country.Id, 2, "t", "B", true, """{"rate":0.2,"basis":null}""")], false),
        ]);
        drafted.Select(d => (d.Code, d.Flag, d.Values, d.MissingReason)).ShouldBe(
        [
            ("A", ItemFlags.Pack, """{"x":1,"y":3}""", (string?)null),
            ("B", ItemFlags.Missing, (string?)null, MissingReasons.NotApproved),
        ], "a null leaves the base's x; the unapproved country's rate is never used, nor the base's it would change");

        var approved = SetupAssembly.Assemble([new AssemblyLayer(country, [Row(country.Id, 1, "t", "B", true, """{"rate":0.2}"""), Row(country.Id, 2, "t", "C", true, """{"rate":""}""")], true)]);
        approved.Select(d => (d.Code, d.Flag, d.MissingReason)).ShouldBe([("B", ItemFlags.Pack, (string?)null), ("C", ItemFlags.Missing, MissingReasons.NoValue)]);
    }

    [Fact]
    public void ALinesNullValue_DoesNotEraseWhatTheCountryOrTheTenantGives_OQ181()
    {
        var line = StarterPack.Load("GENERAL_MOTOR", PackKinds.Line, 1, null, null, "GENERAL", PackScopes.AllTenants, "h", 1, "p").Value;
        var country = StarterPack.Load("XQ", PackKinds.Country, 1, null, "XQ", null, PackScopes.AllTenants, "h", 1, "p").Value;
        var own = StarterPack.Load("GENERAL_MOTOR", PackKinds.Line, 2, null, null, "GENERAL", PackScopes.TenantOnly, "h", 1, "p").Value;
        var am = """{"role_code":null,"max_sum_assured":null,"decision_types":["ACCEPT"]}""";
        var byCountry = SetupAssembly.Assemble([
            new AssemblyLayer(line, [Row(line.Id, 1, "rules.am", "AM", true, am)], true),
            new AssemblyLayer(country, [Row(country.Id, 1, "rules.am", "AM", true, """{"role_code":"UW","max_sum_assured":1000}""")], true)]).Single();
        (byCountry.Flag, byCountry.Values).ShouldBe((ItemFlags.Pack, """{"role_code":"UW","max_sum_assured":1000,"decision_types":["ACCEPT"]}"""));

        var byTenant = SetupAssembly.Assemble([
            new AssemblyLayer(line, [Row(line.Id, 1, "rules.am", "AM", true, am)], true),
            new AssemblyLayer(own, [Row(own.Id, 1, "rules.am", "AM", true, """{"role_code":"UW","max_sum_assured":500}""")], true, Fill: true)]).Single();
        (byTenant.Flag, byTenant.Values, byTenant.SourcePackId).ShouldBe((ItemFlags.Pack, """{"role_code":"UW","max_sum_assured":500,"decision_types":["ACCEPT"]}""", own.Id));
    }

    [Fact]
    public void TheTenantsOwnOverlay_FillsOnlyValuesStillEmpty_AndNeverOverridesAPlatformApprovedRegulatedValue_OQ181()
    {
        var basePack = StarterPack.Load("base", PackKinds.Base, 1, null, null, null, PackScopes.AllTenants, "h", 1, "p").Value;
        var draft = StarterPack.Load("NG", PackKinds.Country, 1, null, "NG", null, PackScopes.AllTenants, "h", 1, "p").Value;
        var own = StarterPack.Load("NG", PackKinds.Country, 2, null, "NG", null, PackScopes.TenantOnly, "h", 1, "p").Value;
        var drafted = SetupAssembly.Assemble([
            new AssemblyLayer(basePack, [Row(basePack.Id, 1, "t", "LIMIT", true, """{"limit":1000,"currency":null}"""), Row(basePack.Id, 2, "t", "OP", false, """{"x":1}""")], true),
            new AssemblyLayer(draft, [Row(draft.Id, 1, "t", "TAX", true, """{"rate":0.075}""")], false),
            new AssemblyLayer(own, [Row(own.Id, 1, "t", "LIMIT", true, """{"limit":9,"currency":"NGN"}"""), Row(own.Id, 2, "t", "OP", false, """{"x":2}"""),
                Row(own.Id, 3, "t", "TAX", true, """{"rate":0.05}"""), Row(own.Id, 4, "t", "NEW", false, """{"z":1}""")], true, Fill: true),
        ]);
        drafted.Select(d => (d.Code, d.Flag, d.Values)).ShouldBe(
        [
            ("LIMIT", ItemFlags.Pack, """{"limit":1000,"currency":"NGN"}"""),
            ("OP", ItemFlags.Pack, """{"x":1}"""),
            ("TAX", ItemFlags.Pack, """{"rate":0.05}"""),
            ("NEW", ItemFlags.Pack, """{"z":1}"""),
        ], "the platform-approved limit stays 1000 and x stays 1; only the empty currency and the unapproved draft's tax are the tenant's; a new row is added");
    }

    [Fact]
    public void TheLayers_AreBaseLinesThenCountry_EachThePlatformsApprovedElseItsDraft_ThenTheTenantsOwnApprovedToFill_OQ181()
    {
        var tenant = Guid.CreateVersion7();
        StarterPack Pack(string code, string kind, string? country, string? line, int version, string scope, bool approved)
        {
            var p = StarterPack.Load(code, kind, version, null, country, line, scope, "h", 1, "loader").Value;
            if (scope == PackScopes.AllTenants && approved)
            {
                p.MarkReviewed("reviewer", "P", "N", new DateOnly(2026, 11, 14), Guid.CreateVersion7());
            }

            if (approved)
            {
                p.Approve("lead", "h", scope, DateTimeOffset.UnixEpoch).IsSuccess.ShouldBeTrue();
            }

            return p;
        }

        var answers = new SetupAnswers("NG", ["GENERAL"]);
        var basePack = Pack("base", PackKinds.Base, null, null, 1, PackScopes.AllTenants, true);
        var draft = Pack("NG", PackKinds.Country, "NG", null, 3, PackScopes.AllTenants, false);
        var platform = Pack("NG", PackKinds.Country, "NG", null, 2, PackScopes.AllTenants, true);
        var motor = Pack("GENERAL_MOTOR", PackKinds.Line, null, "GENERAL", 1, PackScopes.AllTenants, true);
        var own = Pack("NG", PackKinds.Country, "NG", null, 4, PackScopes.TenantOnly, true);
        tenant = own.TenantId; // a version's tenant is set when it is stored
        SetupAssembly.Choose([draft, platform, basePack, motor, own], answers, tenant).Select(c => (c.Pack, c.Fill))
            .ShouldBe([(basePack, false), (motor, false), (platform, false), (own, true)]);
        SetupAssembly.Choose([draft, basePack], answers, tenant).Select(c => c.Pack).ShouldBe([basePack, draft], "a draft when nothing is approved");
    }

    [Fact]
    public void ARowRegulatedInALowerLayer_StaysRegulated_ThoughAnApprovedOverlayMarksItOtherwise_D20()
    {
        var basePack = StarterPack.Load("base", PackKinds.Base, 1, null, null, null, PackScopes.AllTenants, "h", 1, "p").Value;
        var line = StarterPack.Load("L", PackKinds.Line, 1, null, null, "GENERAL", PackScopes.AllTenants, "h", 1, "p").Value;
        var explicitFalse = StarterPackValue.For(line.Id, PackScopes.AllTenants, 1, "t", "B", "FINANCE", false, MarkSources.Explicit, false, MarkSources.Default, """{"rate":""}""", null, null, null);
        var drafted = SetupAssembly.Assemble([
            new AssemblyLayer(basePack, [Row(basePack.Id, 1, "t", "B", true, """{"rate":0.1}""")], true),
            new AssemblyLayer(line, [explicitFalse], true)]);
        drafted.Single().ShouldSatisfyAllConditions(d => d.Regulated.ShouldBeTrue(), d => d.MissingReason.ShouldBe(MissingReasons.NoValue));
    }
}

