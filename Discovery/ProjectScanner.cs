namespace WonderDB.MigrationTool.Discovery;

/// <summary>
/// Metadata about a discovered .NET project in the workspace.
/// </summary>
public class DiscoveredProject
{
    /// <summary>
    /// Project name without the ".Infrastructure" suffix (e.g., "OrderService").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Absolute path to the Infrastructure project folder.
    /// </summary>
    public string InfrastructurePath { get; set; } = string.Empty;

    /// <summary>
    /// Absolute path to the solution root (containing .sln), if found.
    /// </summary>
    public string SolutionPath { get; set; } = string.Empty;
}

/// <summary>
/// Scans the workspace for .NET projects following Clean Architecture conventions.
/// Looks for folders matching *.Infrastructure that contain a .csproj file.
/// </summary>
public class ProjectScanner
{
    /// <summary>
    /// Scan the given base path (or the default workspace) for Infrastructure projects.
    /// </summary>
    public List<DiscoveredProject> ScanWorkspace(string? basePath = null)
    {
        var scanPath = basePath ?? GetDefaultScanPath();
        var projects = new List<DiscoveredProject>();

        if (!Directory.Exists(scanPath))
            return projects;

        // Find directories whose name ends with ".Infrastructure"
        List<string> infraDirs;
        try
        {
            infraDirs = Directory.GetDirectories(scanPath, "*.Infrastructure", SearchOption.AllDirectories)
                .Where(d => Directory.GetFiles(d, "*.csproj", SearchOption.TopDirectoryOnly).Length > 0)
                .ToList();
        }
        catch (UnauthorizedAccessException)
        {
            // Silently skip inaccessible directories
            infraDirs = new List<string>();
        }

        foreach (var infraDir in infraDirs)
        {
            var dirInfo = new DirectoryInfo(infraDir);
            var projectName = dirInfo.Name.Replace(".Infrastructure", string.Empty);
            var solutionPath = FindSolutionRoot(infraDir);

            projects.Add(new DiscoveredProject
            {
                Name = projectName,
                InfrastructurePath = infraDir,
                SolutionPath = solutionPath
            });
        }

        return projects.OrderBy(p => p.Name).ToList();
    }

    /// <summary>
    /// Determines the default workspace path.
    /// In Docker mode, /workspace is used. Otherwise, the current directory.
    /// </summary>
    private static string GetDefaultScanPath()
    {
        if (Directory.Exists("/workspace"))
            return "/workspace";

        return Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// Walk upward from the Infrastructure path to find the nearest .sln file.
    /// </summary>
    private static string FindSolutionRoot(string infraPath)
    {
        var dir = new DirectoryInfo(infraPath);

        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return dir.FullName;
            dir = dir.Parent;
        }

        return Path.GetDirectoryName(infraPath) ?? infraPath;
    }
}


//its only job is to discover Clean Architecture projects in a folder structure.