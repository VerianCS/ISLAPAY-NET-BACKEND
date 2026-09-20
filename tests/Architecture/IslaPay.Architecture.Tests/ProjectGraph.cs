using System.Xml.Linq;

namespace IslaPay.Architecture.Tests;

/// <summary>One project and what it references, as the build sees it.</summary>
public sealed record ProjectNode(string Name, string RelativePath, IReadOnlyList<string> References)
{
    /// <summary>Repo-relative directory, with forward slashes.</summary>
    public string Directory => RelativePath[..RelativePath.LastIndexOf('/')];

    public bool IsUnder(string path) =>
        Directory.StartsWith(path, StringComparison.Ordinal);

    public bool IsContracts => Name.EndsWith(".Contracts", StringComparison.Ordinal);

    public bool IsTest => Name.EndsWith(".Tests", StringComparison.Ordinal);

    /// <summary>"Identity" for anything under <c>src/Modules/Identity/</c>.</summary>
    public string? Module
    {
        get
        {
            const string Prefix = "src/Modules/";
            if (!Directory.StartsWith(Prefix, StringComparison.Ordinal)) return null;
            var rest = Directory[Prefix.Length..];
            var slash = rest.IndexOf('/', StringComparison.Ordinal);
            return slash < 0 ? rest : rest[..slash];
        }
    }
}

/// <summary>
/// The dependency graph, read from the <c>.csproj</c> files themselves.
/// </summary>
/// <remarks>
/// <para>
/// Read from the build files rather than from compiled assemblies on purpose.
/// A rule about what a project is <em>allowed to reference</em> is a rule
/// about the build graph, and the build graph is where a violation is
/// introduced — by someone adding a line to a csproj, usually with the best of
/// intentions and in a hurry. Catching it in assembly metadata would also
/// work, but the error would name a type rather than the line that has to
/// change.
/// </para>
/// <para>
/// It also means a project with no code in it yet is still covered.
/// </para>
/// </remarks>
public static class ProjectGraph
{
    public static IReadOnlyList<ProjectNode> Load()
    {
        var root = RepositoryRoot();

        var projects = new List<ProjectNode>();
        foreach (var file in System.IO.Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
        {
            var relative = Relative(root, file);
            if (relative.Contains("/obj/", StringComparison.Ordinal)
                || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(file)!;
            var references = XDocument.Load(file)
                .Descendants("ProjectReference")
                .Select(r => (string?)r.Attribute("Include"))
                .Where(include => !string.IsNullOrWhiteSpace(include))
                .Select(include => Path.GetFileNameWithoutExtension(
                    include!.Replace('\\', '/'))!)
                .ToList();

            projects.Add(new ProjectNode(
                Path.GetFileNameWithoutExtension(file), relative, references));
        }

        return projects.Count > 0
            ? projects
            : throw new InvalidOperationException($"No projects found under {root}.");
    }

    /// <summary>
    /// Walks up from the test binaries until the solution file turns up.
    /// </summary>
    /// <remarks>
    /// Not a hardcoded relative path: the depth from <c>bin/Debug/net10.0</c>
    /// to the root changes the moment a project moves, and this suite exists
    /// precisely because projects move.
    /// </remarks>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (directory.GetFiles("IslaPay.slnx").Length > 0) return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not find IslaPay.slnx above " + AppContext.BaseDirectory);
    }

    private static string Relative(string root, string file) =>
        Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
}
