using System.Reflection;
using EtchaSketch.Core;
using EtchaSketch.Data;

namespace EtchaSketch.Tests;

public class ArchitectureTests
{
    private static string[] ReferencedAssemblyNames(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToArray();

    [Fact]
    public void Core_does_not_depend_on_ui()
    {
        Assert.DoesNotContain(ReferencedAssemblyNames(typeof(CoreAssembly).Assembly),
            name => name.StartsWith("Avalonia", StringComparison.Ordinal));
    }

    [Fact]
    public void Core_does_not_depend_on_other_EtchaSketch_projects()
    {
        Assert.DoesNotContain(ReferencedAssemblyNames(typeof(CoreAssembly).Assembly),
            name => name.StartsWith("EtchaSketch.", StringComparison.Ordinal));
    }

    [Fact]
    public void Data_does_not_depend_on_ui()
    {
        Assert.DoesNotContain(ReferencedAssemblyNames(typeof(DataAssembly).Assembly),
            name => name.StartsWith("Avalonia", StringComparison.Ordinal));
    }
}
