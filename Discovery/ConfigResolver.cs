using Microsoft.Extensions.Configuration;

namespace WonderDB.MigrationTool.Discovery;

/// <summary>
/// Resolves application configuration by traversing up from the Infrastructure path
/// to locate appsettings.json files. Loads configuration in priority order:
///   1. Environment variables (highest)
///   2. appsettings.{ASPNETCORE_ENVIRONMENT}.json
///   3. appsettings.json (base fallback)
/// </summary>
public class ConfigResolver
{
    /// <summary>
    /// Build an IConfiguration rooted at the project directory above the Infrastructure folder.
    /// </summary>
    /// <param name="infrastructurePath">Absolute path to the Infrastructure project folder.</param>
    /// <param name="environment">Optional environment name (Development, Staging, Production).</param>
    public IConfiguration Resolve(string infrastructurePath, string? environment = null)
    {
        var projectRoot = FindProjectRoot(infrastructurePath);

        var builder = new ConfigurationBuilder()
            .SetBasePath(projectRoot);

        // Base configuration (lowest priority)
        var baseConfig = Path.Combine(projectRoot, "appsettings.json");
        if (File.Exists(baseConfig))
        {
            builder.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
        }

        // Environment-specific configuration
        var env = environment
                  ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                  ?? "Development";

        var envConfig = Path.Combine(projectRoot, $"appsettings.{env}.json");
        if (File.Exists(envConfig))
        {
            builder.AddJsonFile($"appsettings.{env}.json", optional: true, reloadOnChange: false);
        }

        // Environment variables (highest priority)
        builder.AddEnvironmentVariables();

        return builder.Build();
    }

    /// <summary>
    /// Extract all connection strings from the "ConnectionStrings" section of the configuration.
    /// </summary>
    public Dictionary<string, string> GetConnectionStrings(IConfiguration configuration)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = configuration.GetSection("ConnectionStrings");

        foreach (var child in section.GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(child.Value))
            {
                result[child.Key] = child.Value;
            }
        }

        return result;
    }

    /// <summary>
    /// Walk upward from the Infrastructure path to find the project root.
    /// The project root is the first directory containing appsettings.json or a .sln file.
    /// Falls back to the parent of the Infrastructure directory, then the Infrastructure directory itself.
    /// </summary>
      private static string FindProjectRoot(string infrastructurePath)
    {
        var dir = new DirectoryInfo(infrastructurePath);

        // First check the Infrastructure directory itself
        if (File.Exists(Path.Combine(dir.FullName, "appsettings.json")))
            return dir.FullName;

        // Check for sibling API/Web projects first (Clean Architecture sibling pattern)
        if (dir.Parent != null)
        {
            var siblings = dir.Parent.GetDirectories();
            foreach (var sibling in siblings)
            {
                if (sibling.Name.EndsWith(".API", StringComparison.OrdinalIgnoreCase) ||
                    sibling.Name.EndsWith(".Web", StringComparison.OrdinalIgnoreCase) ||
                    sibling.Name.EndsWith(".WebAPI", StringComparison.OrdinalIgnoreCase))
                {
                    var siblingConfig = Path.Combine(sibling.FullName, "appsettings.json");
                    if (File.Exists(siblingConfig))
                        return sibling.FullName;
                }
            }
        }

        // Walk upward
        var parent = dir.Parent;
        while (parent != null)
        {
            if (File.Exists(Path.Combine(parent.FullName, "appsettings.json")))
                return parent.FullName;

            if (parent.GetFiles("*.sln").Length > 0)
                return parent.FullName;

            parent = parent.Parent;
        }

        return infrastructurePath;
    }

}
