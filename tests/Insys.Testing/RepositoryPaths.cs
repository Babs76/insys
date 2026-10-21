namespace Insys.Testing;

public static class RepositoryPaths
{
    public static string Root { get; } = FindRoot();

    public static string Migrations => Path.Combine(Root, "db", "migrations");

    public static string Roles => Path.Combine(Root, "db", "roles");

    public static string Specs => Path.Combine(Root, "docs", "specs");

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Insys.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root (Insys.slnx).");
    }
}
