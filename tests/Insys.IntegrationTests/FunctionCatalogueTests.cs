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
using Insys.BuildingBlocks.Api.Functions;
using Insys.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// The function catalogue has one source: the functions the modules declare, with the permissions their operations
/// require as the router builds them (ADR-026). The repeatable seed is generated from them, and the database catalogue
/// matches. To accept a change, run with INSYS_UPDATE_SNAPSHOTS=1.
/// </summary>
public sealed class FunctionCatalogueTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task SeedScript_IsGeneratedFromTheDeclarations_ADR026()
    {
        await fixture.Host();
        var expected = FunctionCatalogueSeed.Generate();
        if (Environment.GetEnvironmentVariable("INSYS_UPDATE_SNAPSHOTS") == "1")
        {
            await File.WriteAllTextAsync(FunctionCatalogueSeed.FilePath, expected);
        }

        File.Exists(FunctionCatalogueSeed.FilePath).ShouldBeTrue("Run the tests once with INSYS_UPDATE_SNAPSHOTS=1 to generate the seed.");
        (await File.ReadAllTextAsync(FunctionCatalogueSeed.FilePath)).ShouldBe(expected,
            "Functions in code changed. Run with INSYS_UPDATE_SNAPSHOTS=1 and commit db/migrations/R__function_catalogue.sql.");
    }

    [Fact]
    public async Task DatabaseCatalogue_HasEveryFunction_WithTheSamePermissions_ADR026()
    {
        await fixture.Host();
        var mismatches = new List<string>();
        foreach (var f in FunctionRegistry.All())
        {
            var row = await fixture.Database.ScalarAsOwner<string>("""
                SELECT array_to_string(required_permissions, ',') || '|' || allowed_while_onboarding FROM iam.ref_function_catalogue
                WHERE tenant_id IS NULL AND function_code = @code AND record_status = 'ACTIVE' AND effective_to IS NULL
                """, ("code", f.Function.Code));
            var expected = $"{string.Join(',', f.Permissions)}|{(f.AllowedWhileOnboarding ? "true" : "false")}";
            if (row != expected)
            {
                mismatches.Add($"{f.Function.Code}: database '{row}', code '{expected}'");
            }
        }

        mismatches.ShouldBeEmpty();
    }

    [Fact]
    public async Task EveryScreenAndAction_RequiresAPermission_AndEveryActionHasAScreen_ADR026()
    {
        await fixture.Host();
        var functions = FunctionRegistry.All();
        var codes = functions.Select(f => f.Function.Code).ToHashSet(StringComparer.Ordinal);
        functions.Where(f => f.Function.Kind != FunctionKind.Job && f.Permissions.Count == 0).Select(f => f.Function.Code).ToList().ShouldBeEmpty();
        functions.Where(f => f.Function.Kind == FunctionKind.Action && (f.Function.Parent is null || !codes.Contains(f.Function.Parent)))
            .Select(f => f.Function.Code).ToList().ShouldBeEmpty();
    }

    /// <summary>
    /// The catalogue holds only functions that exist in the release: every operation a function names is an endpoint of
    /// this build, and every JOB function is one a consumer or daily job runs under. Planned functions are not catalogued.
    /// </summary>
    [Fact]
    public async Task Catalogue_HoldsOnlyFunctionsThatExistInTheRelease_ADR026()
    {
        var factory = await fixture.Host();
        var names = factory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .Select(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IEndpointNameMetadata>()?.EndpointName).OfType<string>();
        FunctionRegistry.Unmatched(names).ShouldBeEmpty("a function names an operation this build does not have");

        string[] jobs =
        [
            .. Insys.BuildingBlocks.Application.Context.JobFunctions.All.Values,
            Insys.Life.Infrastructure.LifeJobs.Function, Insys.General.Infrastructure.GeneralJobs.Function, Insys.Health.Infrastructure.HealthJobs.Function,
            Insys.Billing.Infrastructure.BillingJobs.Function, Insys.Payments.Infrastructure.PaymentJobs.Function,
            Insys.Integration.Infrastructure.Webhooks.WebhookJobs.Function, Insys.Communications.Infrastructure.NotificationSender.Function,
        ];
        FunctionRegistry.All().Where(f => f.Function.Kind == FunctionKind.Job).Select(f => f.Function.Code).Order(StringComparer.Ordinal)
            .ShouldBe(jobs.Distinct().Order(StringComparer.Ordinal), "every JOB function is one a consumer or daily job runs under, and every one is catalogued");
    }
}
