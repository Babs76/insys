using Insys.DbMigrator;

// Usage: dotnet run --project tools/Insys.DbMigrator -- [--connection <owner connection string>] [--migrations db/migrations]
//          [--app-role insys_app] [--grants db/roles/app_role_grants.sql]
// The connection may also come from INSYS_MIGRATION_CONNECTION. The application role's password, needed only to
// create the role, comes from INSYS_APP_ROLE_PASSWORD so it never appears on the command line.
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
    if (options.TryGetValue("app-role", out var role))
    {
        await runner.GrantAppRole(options.GetValueOrDefault("grants") ?? "db/roles/app_role_grants.sql", role,
            Environment.GetEnvironmentVariable("INSYS_APP_ROLE_PASSWORD"));
    }

    return 0;
}
catch (Exception ex) when (ex is InvalidOperationException or Npgsql.NpgsqlException)
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
