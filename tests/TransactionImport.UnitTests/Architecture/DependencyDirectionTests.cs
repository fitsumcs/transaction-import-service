using System.Reflection;

namespace TransactionImport.UnitTests.Architecture;

public sealed class DependencyDirectionTests
{
    private static readonly string[] InfrastructureAssemblyPrefixes =
    [
        "TransactionImport.Infrastructure",
        "TransactionImport.Api",
        "TransactionImport.Worker",
        "Npgsql",
        "Microsoft.AspNetCore",
    ];

    [Fact]
    public void Domain_does_not_depend_on_any_other_layer_or_infrastructure()
    {
        var references = ReferencedAssemblyNames("TransactionImport.Domain");

        Assert.DoesNotContain(references, name => name.StartsWith("TransactionImport.", StringComparison.Ordinal));
        AssertNoInfrastructure(references);
    }

    [Fact]
    public void Application_depends_only_on_domain_and_no_infrastructure()
    {
        var references = ReferencedAssemblyNames("TransactionImport.Application");

        Assert.All(
            references.Where(name => name.StartsWith("TransactionImport.", StringComparison.Ordinal)),
            name => Assert.Equal("TransactionImport.Domain", name));
        AssertNoInfrastructure(references);
    }

    private static string[] ReferencedAssemblyNames(string assemblyName) =>
        Assembly.Load(assemblyName)
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

    private static void AssertNoInfrastructure(string[] references) =>
        Assert.DoesNotContain(
            references,
            name => InfrastructureAssemblyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)));
}
