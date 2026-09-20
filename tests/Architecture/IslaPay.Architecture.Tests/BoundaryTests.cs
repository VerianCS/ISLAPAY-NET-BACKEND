namespace IslaPay.Architecture.Tests;

/// <summary>
/// The module boundaries, enforced rather than described.
/// </summary>
/// <remarks>
/// <para>
/// A modular monolith is one bad afternoon away from being an ordinary
/// monolith. The seams cost nothing to cross — one line in a csproj and the
/// compiler is happy — so a rule that lives only in a README is a rule that
/// has already been broken somewhere nobody has looked.
/// </para>
/// <para>
/// These tests are what make the shape real, and they are the thing that
/// decides whether a context can ever be lifted into its own process. Each
/// failure names the project and the reference to remove.
/// </para>
/// </remarks>
public class BoundaryTests
{
    private static readonly IReadOnlyList<ProjectNode> Projects = ProjectGraph.Load();

    private const string Host = "IslaPay.Host";

    [Fact]
    public void The_platform_never_references_a_module()
    {
        // The direction of the whole graph. Modules stand on the platform; the
        // moment the platform knows a module's name, the platform stops being
        // reusable and the module stops being removable.
        var offenders = Projects
            .Where(p => p.IsUnder("src/Platform/"))
            .SelectMany(p => Referenced(p).Select(r => (From: p, To: r)))
            .Where(edge => edge.To.Module is not null)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            Describe(offenders, "the platform must not reference a module"));
    }

    [Fact]
    public void A_module_reaches_another_module_only_through_its_contracts()
    {
        // The rule that keeps extraction possible. Internals are internal
        // across a module boundary exactly as they would be across a network
        // one — and going through .Contracts today is what makes going through
        // HTTP or a queue tomorrow a change of transport, not of design.
        var offenders = Projects
            .Where(p => p.Module is not null && !p.IsTest)
            .SelectMany(p => Referenced(p).Select(r => (From: p, To: r)))
            .Where(edge =>
                edge.To.Module is not null
                && edge.To.Module != edge.From.Module
                && !edge.To.IsContracts)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            Describe(offenders, "cross-module references must go through .Contracts"));
    }

    [Fact]
    public void Only_the_host_composes_modules()
    {
        // Exactly one project is allowed to know every module: the composition
        // root. If a second one appears, "the host" has quietly become two
        // places and adding a module means editing both.
        var composers = Projects
            .Where(p => !p.IsTest)
            .Where(p => Referenced(p).Any(r => r.Module is not null && !r.IsContracts))
            .Where(p => p.Module is null)
            .Select(p => p.Name)
            .ToList();

        Assert.Equal([Host], composers);
    }

    [Fact]
    public void The_transport_knows_no_contracts_at_all()
    {
        // Messaging must be able to carry any module's event without knowing
        // what any module's events are. It used to reference the shared
        // contracts package for one line of JSON configuration and inherited
        // four bounded contexts with it.
        var messaging = Single("IslaPay.Platform.Messaging");

        Assert.DoesNotContain(Referenced(messaging), r => r.IsContracts);
    }

    [Fact]
    public void The_shared_api_vocabulary_depends_on_nothing()
    {
        // Everything may reference it, so it may reference nothing: that is
        // what stops it from becoming the shared kernel through which two
        // modules accidentally meet.
        Assert.Empty(Single("IslaPay.Platform.Api").References);
    }

    [Fact]
    public void Nothing_builds_on_top_of_the_host()
    {
        // The host is the end of the graph. A library referencing it would be
        // a module depending on its own composition root.
        var offenders = Projects
            .Where(p => !p.IsTest && p.Name != Host)
            .SelectMany(p => Referenced(p).Select(r => (From: p, To: r)))
            .Where(edge => edge.To.Name == Host)
            .ToList();

        Assert.True(offenders.Count == 0, Describe(offenders, "nothing may reference the host"));
    }

    [Fact]
    public void A_modules_tests_stay_inside_that_module()
    {
        // A test project may reach its own module's internals, the platform,
        // and the host it drives. It may not reach a sibling module's
        // internals: a test that does is a test that will keep passing after
        // the boundary is gone.
        var offenders = Projects
            .Where(p => p.IsTest && p.Module is not null)
            .SelectMany(p => Referenced(p).Select(r => (From: p, To: r)))
            .Where(edge =>
                edge.To.Module is not null
                && edge.To.Module != edge.From.Module
                && !edge.To.IsContracts)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            Describe(offenders, "a module's tests must not reach another module's internals"));
    }

    [Fact]
    public void Every_module_directory_publishes_contracts()
    {
        // A module with no .Contracts assembly has no way to be integrated
        // with except through its internals, which is the failure this whole
        // suite exists to prevent. Ledger is the deliberate exception, and it
        // is named here so that the exception is a decision rather than an
        // oversight: it is pure domain rules with no API surface yet, and the
        // day it grows one it gets a .Contracts like everything else.
        string[] knownExceptions = ["Ledger"];

        var modules = Projects
            .Where(p => p.Module is not null && !p.IsTest)
            .Select(p => p.Module!)
            .Distinct()
            .Order(StringComparer.Ordinal);

        foreach (var module in modules)
        {
            if (knownExceptions.Contains(module, StringComparer.Ordinal)) continue;

            Assert.True(
                Projects.Any(p => p.Module == module && p.IsContracts),
                $"Module '{module}' has no .Contracts project, so nothing can integrate "
                + "with it without reaching into its internals.");
        }
    }

    // ------------------------------------------------------------------ helpers

    private static IEnumerable<ProjectNode> Referenced(ProjectNode project) =>
        project.References.Select(Single);

    private static ProjectNode Single(string name) =>
        Projects.SingleOrDefault(p => p.Name == name)
        ?? throw new InvalidOperationException($"No project named '{name}' in the graph.");

    private static string Describe(
        List<(ProjectNode From, ProjectNode To)> offenders, string rule) =>
        $"{offenders.Count} reference(s) break the rule that {rule}:"
        + string.Concat(offenders.Select(e =>
            $"{Environment.NewLine}  {e.From.Name} -> {e.To.Name}  ({e.From.RelativePath})"));
}
