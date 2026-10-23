#!/usr/bin/env python3
# ============================================================================
# OPENFACTOR TECHNOLOGY GROUP
# CONFIDENTIAL AND PROPRIETARY SOFTWARE
# ============================================================================
# Copyright (c) 2026 OpenFactor Technology Group.
# All rights reserved.
# PRODUCT       : INSYS
# COMPONENT     : Tools
# CONFIDENTIALITY AND INTELLECTUAL PROPERTY NOTICE
# This source code and its associated documentation contain confidential and
# proprietary information belonging to OpenFactor Technology Group or its
# applicable intellectual property rights holder.
# This material is provided solely for authorised development, maintenance,
# operation or evaluation purposes, as applicable. No person may reproduce,
# modify, distribute, disclose, publish or otherwise use this material except
# as expressly permitted by an applicable written agreement or licence.
# No ownership rights or licence to any intellectual property are granted
# by the inclusion of this notice, except as expressly provided in an
# applicable written agreement or licence.
# Third-party software, open-source components and customer-owned materials
# remain subject to their respective ownership rights and applicable licences
# or agreements.
# UNAUTHORISED ACCESS, USE, COPYING OR DISCLOSURE IS PROHIBITED.
# ============================================================================
"""Creates the Infrastructure and Api projects of a module that owns static data but is not built yet.

Usage: python3 tools/scaffold-module.py <Module> <primary schema> [<other owned schemas>...]
Existing files are never overwritten. Domain, Application and Contracts projects are added when the module is built.
"""
import os
import pathlib
import subprocess
import sys

module, schema, *others = sys.argv[1:]
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
base = os.path.join(root, "src", "Modules", module)


def write(path, content):
    full = os.path.join(base, path)
    if os.path.exists(full):
        return
    os.makedirs(os.path.dirname(full), exist_ok=True)
    with open(full, "w", encoding="utf-8", newline="\n") as f:
        f.write(content)


owned = ""
if others:
    schemas = ", ".join(f'"{s}"' for s in [schema, *others])
    owned = f"\n    public override IReadOnlyCollection<string> OwnedSchemas => [{schemas}];\n"

write(f"Insys.{module}.Infrastructure/Insys.{module}.Infrastructure.csproj", """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\\..\\..\\BuildingBlocks\\Insys.BuildingBlocks.Infrastructure\\Insys.BuildingBlocks.Infrastructure.csproj" />
  </ItemGroup>
</Project>
""")
write(f"Insys.{module}.Infrastructure/Persistence/{module}DbContext.cs", f"""using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.{module}.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.{module}.Infrastructure.Persistence;

/// <summary>The {module} module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class {module}DbContext(DbContextOptions<{module}DbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{{
    public const string SchemaName = "{schema}";

    public override string Schema => SchemaName;
{owned}
    protected override void ConfigureModel(ModelBuilder modelBuilder) => {module}StaticData.Configure(modelBuilder);
}}
""")
write(f"Insys.{module}.Infrastructure/{module}Infrastructure.cs", f"""using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.{module}.Infrastructure.Persistence;
using Insys.{module}.Infrastructure.StaticData;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.{module}.Infrastructure;

public static class {module}Infrastructure
{{
    public static IServiceCollection Add{module}Infrastructure(this IServiceCollection services)
    {{
        services.AddModuleDbContext<{module}DbContext>({module}DbContext.SchemaName, "{module}");
        services.AddStaticData<{module}DbContext>({module}DbContext.SchemaName, {module}StaticData.Descriptors);
        return services;
    }}
}}
""")
write(f"Insys.{module}.Api/Insys.{module}.Api.csproj", f"""<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\\..\\..\\BuildingBlocks\\Insys.BuildingBlocks.Api\\Insys.BuildingBlocks.Api.csproj" />
    <ProjectReference Include="..\\Insys.{module}.Infrastructure\\Insys.{module}.Infrastructure.csproj" />
  </ItemGroup>
</Project>
""")
write(f"Insys.{module}.Api/{module}Module.cs", f"""using Insys.BuildingBlocks.Api;
using Insys.{module}.Api.StaticData;
using Insys.{module}.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.{module}.Api;

/// <summary>The {module} module. So far it serves its static data only; its aggregates are built in a later phase.</summary>
public sealed class {module}Module : IModule
{{
    public string Name => "{module}";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.Add{module}Infrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api) => {module}StaticDataEndpoints.MapStaticData(api);
}}
""")
# The new files carry the source file header (tools/source-headers.py).
subprocess.run([sys.executable, str(pathlib.Path(__file__).with_name("source-headers.py")), "--apply"], check=True)
print(f"scaffolded {module}")
