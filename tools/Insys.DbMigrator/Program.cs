/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : DbMigrator
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
using Insys.DbMigrator;

// Usage: dotnet run --project tools/Insys.DbMigrator -- [--connection <owner connection string>] [--migrations db/migrations]
//          [--roles db/roles] [--bootstrap-operator <operator tenant id>
//           --admin1-subject <sub> --admin1-username <name> --admin1-name <display name> --admin2-subject ... --admin2-name ...]
// The connection may also come from INSYS_MIGRATION_CONNECTION. With --roles, the module grants are applied; if
// INSYS_ROLE_PASSWORD is set, the local login roles are created first with that password (never on the command line).
// The bootstrap needs two distinct platform administrators (BR-061); each value may also come from
// INSYS_BOOTSTRAP_ADMIN1_SUBJECT, INSYS_BOOTSTRAP_ADMIN1_USERNAME, INSYS_BOOTSTRAP_ADMIN1_NAME and the ADMIN2 equivalents.
var options = ParseArguments(args);
var connection = options.GetValueOrDefault("connection") ?? Environment.GetEnvironmentVariable("INSYS_MIGRATION_CONNECTION");
if (string.IsNullOrWhiteSpace(connection))
{
    Console.Error.WriteLine("Set --connection or INSYS_MIGRATION_CONNECTION to the migration (owner) role's connection string.");
    return 2;
}

var runner = new MigrationRunner(connection, Console.WriteLine);
try
{
    var result = await runner.Migrate(options.GetValueOrDefault("migrations") ?? "db/migrations");
    Console.WriteLine($"{result.Applied.Count} migration(s) applied, {result.AlreadyApplied.Count} already applied.");
    if (options.TryGetValue("roles", out var rolesDirectory))
    {
        await runner.ApplyRoles(rolesDirectory, Environment.GetEnvironmentVariable("INSYS_ROLE_PASSWORD"));
    }

    if (options.TryGetValue("bootstrap-operator", out var operatorTenant))
    {
        if (options.ContainsKey("admin-subject"))
        {
            throw new ArgumentException($"--admin-subject (one administrator) is replaced. {PlatformAdministrator.Required}");
        }

        PlatformAdministrator Admin(int n)
        {
            string Value(string field) => options.GetValueOrDefault($"admin{n}-{field}")
                ?? Environment.GetEnvironmentVariable($"INSYS_BOOTSTRAP_ADMIN{n}_{(field == "name" ? "NAME" : field.ToUpperInvariant())}") ?? "";
            return new PlatformAdministrator(Value("subject"), Value("username"), Value("name"));
        }

        await runner.BootstrapOperator(options.GetValueOrDefault("bootstrap-script") ?? "db/bootstrap/operator.sql", Guid.Parse(operatorTenant), [Admin(1), Admin(2)]);
    }

    return 0;
}
catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or Npgsql.NpgsqlException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static Dictionary<string, string> ParseArguments(string[] args)
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < args.Length - 1; i += 2)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unexpected argument '{args[i]}'.");
        }

        options[args[i][2..]] = args[i + 1];
    }

    return options;
}
