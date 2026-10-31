/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Regen
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
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

// Regenerates the function catalogue seed and the role templates without a database: the host starts as for build-time document generation.
public static class Regen
{
    public static async Task Main()
    {
        await using var host = new WebApplicationFactory<global::Program>().WithWebHostBuilder(b => b.UseEnvironment("Testing"));
        _ = host.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).ToList();
        await File.WriteAllTextAsync(FunctionCatalogueSeed.FilePath, FunctionCatalogueSeed.Generate());
        await File.WriteAllTextAsync(RoleTemplateAddendum.FilePath, RoleTemplateAddendum.Document(FunctionRegistry.All()));
        await File.WriteAllTextAsync(ErrorCodeSeed.FilePath, ErrorCodeSeed.Generate());
        Console.WriteLine($"functions: {FunctionRegistry.All().Count}");
    }
}
