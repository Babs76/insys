using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Insys.Testing;

/// <summary>
/// The OpenFactor source file header (tools/source-header.txt), with the same rules as tools/source-headers.py: which
/// files are in scope, the comment style per file type, and the component each file belongs to.
/// </summary>
public static partial class SourceHeader
{
    public const string Marker = "OPENFACTOR TECHNOLOGY GROUP";

    private static readonly string[] CStyle = [".cs", ".sql"];
    private static readonly string[] HashStyle = [".yml", ".yaml", ".sh", ".ps1", ".py"];
    private static readonly string[] XmlStyle = [".csproj", ".props", ".targets", ".slnx"];

    private static readonly Lazy<string[]> Template = new(() =>
        File.ReadAllText(Path.Combine(RepositoryPaths.Root, "tools", "source-header.txt")).TrimEnd('\n').Split('\n'));

    public enum Style
    {
        C,
        Hash,
        Xml,
    }

    /// <summary>The header in C-style comment syntax, for generated C# and SQL.</summary>
    public static string Render(string component) => Render(Style.C, component);

    public static string Render(Style style, string component)
    {
        var lines = Template.Value.Select(line => line.Replace("{COMPONENT}", component, StringComparison.Ordinal)).ToList();
        return style switch
        {
            Style.C => "/*\n" + string.Concat(lines.Select(l => $" * {l}\n")) + " */\n",
            Style.Hash => string.Concat(lines.Select(l => $"# {l}\n")),
            _ => "<!--\n" + string.Concat(lines.Select(l => $"  {l}\n")) + "-->\n",
        };
    }

    public static Style? StyleOf(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        var extension = Path.GetExtension(relativePath).ToLowerInvariant();
        return name.StartsWith("Dockerfile", StringComparison.Ordinal) || HashStyle.Contains(extension) ? Style.Hash
            : CStyle.Contains(extension) ? Style.C
            : XmlStyle.Contains(extension) ? Style.Xml
            : null;
    }

    /// <summary>The module or component a file belongs to, from its path relative to the repository root.</summary>
    public static string ComponentOf(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/');
        return parts switch
        {
            ["src", "Modules", var module, ..] => module,
            ["src", "BuildingBlocks", var project, ..] => project.Replace("Insys.", "", StringComparison.Ordinal),
            ["src", "Host", ..] => "Api.Host",
            ["tools", var tool, _, ..] => tool.Replace("Insys.", "", StringComparison.Ordinal),
            ["tools", _] => "Tools",
            ["tests", var project, _, ..] => project.Replace("Insys.", "", StringComparison.Ordinal),
            ["tests", _] => "Tests.Build",
            ["db", "migrations", ..] => "Migrations",
            ["db", "roles", ..] => "Database.Roles",
            ["db", "bootstrap", ..] => "Database.Bootstrap",
            ["db", ..] => "Database",
            [".github", ..] => "CI",
            [var file] when file.StartsWith("docker-compose", StringComparison.Ordinal) => "Local.Environment",
            _ => "Build",
        };
    }

    /// <summary>Every in-scope file git knows (tracked or new, not ignored), outside bin and obj, relative to the root.</summary>
    public static IReadOnlyList<string> InScopeFiles()
    {
        var git = Process.Start(new ProcessStartInfo("git", "ls-files --cached --others --exclude-standard")
        {
            WorkingDirectory = RepositoryPaths.Root, RedirectStandardOutput = true, UseShellExecute = false,
        })!;
        var files = git.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        git.WaitForExit();
        return files.Distinct().Where(f => !$"/{f}".Contains("/bin/", StringComparison.Ordinal) && !$"/{f}".Contains("/obj/", StringComparison.Ordinal))
            .Where(f => StyleOf(f) is not null && File.Exists(Path.Combine(RepositoryPaths.Root, f))).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Whether the file starts (after a byte-order mark, shebang or XML declaration) with the header for its component.</summary>
    public static bool HasCorrectHeader(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(RepositoryPaths.Root, relativePath));
        text = text.TrimStart('﻿');
        text = Preamble().Replace(text, "", 1);
        return text.StartsWith(Render(StyleOf(relativePath)!.Value, ComponentOf(relativePath)), StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^(#![^\n]*\n)?(<\?xml[^>]*\?>\n)?")]
    private static partial Regex Preamble();
}
