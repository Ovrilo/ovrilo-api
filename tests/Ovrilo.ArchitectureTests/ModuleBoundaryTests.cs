using System.Reflection;

using Microsoft.EntityFrameworkCore;

namespace Ovrilo.ArchitectureTests;

/// <summary>
/// Enforces modular-monolith ownership (ADR-0004): a module may depend on another module only through
/// its <c>.Contracts</c> assembly, contracts stay free of infrastructure, and persistence stays internal.
/// </summary>
public sealed class ModuleBoundaryTests
{
    private const string ModulePrefix = "Ovrilo.Modules.";
    private const string ContractsSuffix = ".Contracts";

    public static TheoryData<string> ModuleAssemblies() => [.. LoadModuleAssemblies().Where(a => !IsContracts(a)).Select(a => a.GetName().Name!)];

    public static TheoryData<string> ContractAssemblies() => [.. LoadModuleAssemblies().Where(IsContracts).Select(a => a.GetName().Name!)];

    [Fact]
    public void ModulesAreDiscovered()
    {
        Assert.Contains(LoadModuleAssemblies(), a => a.GetName().Name == "Ovrilo.Modules.Identity");
        Assert.Contains(LoadModuleAssemblies(), a => a.GetName().Name == "Ovrilo.Modules.Identity.Contracts");
    }

    [Theory]
    [MemberData(nameof(ModuleAssemblies))]
    public void ModulesReferenceOtherModulesOnlyThroughContracts(string moduleName)
    {
        var module = Assembly.Load(moduleName);
        var ownContracts = moduleName + ContractsSuffix;

        var violations = module.GetReferencedAssemblies()
            .Select(r => r.Name!)
            .Where(name => name.StartsWith(ModulePrefix, StringComparison.Ordinal))
            .Where(name => name != ownContracts && !name.EndsWith(ContractsSuffix, StringComparison.Ordinal))
            .ToList();

        Assert.Empty(violations);
    }

    [Theory]
    [MemberData(nameof(ContractAssemblies))]
    public void ContractsDoNotDependOnModulesOrInfrastructure(string contractsName)
    {
        var references = Assembly.Load(contractsName).GetReferencedAssemblies().Select(r => r.Name!).ToList();

        Assert.DoesNotContain(references, name => name.StartsWith(ModulePrefix, StringComparison.Ordinal) && !name.EndsWith(ContractsSuffix, StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Npgsql", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(ModuleAssemblies))]
    public void DbContextsAndPersistenceTypesAreInternal(string moduleName)
    {
        var publicPersistence = Assembly.Load(moduleName).GetExportedTypes()
            // EF Core generates migrations as public classes; they expose no entities or DbContext.
            .Where(t => !typeof(Microsoft.EntityFrameworkCore.Migrations.Migration).IsAssignableFrom(t))
            .Where(t => typeof(DbContext).IsAssignableFrom(t)
                || t.Namespace?.Contains(".Persistence", StringComparison.Ordinal) == true
                || t.Namespace?.Contains(".Domain", StringComparison.Ordinal) == true)
            .Select(t => t.FullName)
            .ToList();

        Assert.Empty(publicPersistence);
    }

    private static List<Assembly> LoadModuleAssemblies()
    {
        var directory = AppContext.BaseDirectory;
        return Directory.EnumerateFiles(directory, ModulePrefix + "*.dll")
            .Select(path => Assembly.Load(Path.GetFileNameWithoutExtension(path)))
            .ToList();
    }

    private static bool IsContracts(Assembly assembly) =>
        assembly.GetName().Name!.EndsWith(ContractsSuffix, StringComparison.Ordinal);
}
