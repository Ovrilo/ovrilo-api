namespace Ovrilo.ArchitectureTests;

/// <summary>
/// The image restores from project files copied one by one (layer caching). A project missing from that list
/// only fails at deployment time, so check it here.
/// </summary>
public sealed class DockerfileTests
{
    [Fact]
    public void EveryProjectUnderSrcIsCopiedBeforeRestore()
    {
        var root = RepositoryRoot();
        var dockerfile = File.ReadAllText(Path.Combine(root, "Dockerfile"));
        var projects = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToList();

        Assert.NotEmpty(projects);
        Assert.All(projects, project => Assert.Contains($"COPY {project} ", dockerfile, StringComparison.Ordinal));
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Ovrilo.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (Ovrilo.slnx) not found.");
    }
}
