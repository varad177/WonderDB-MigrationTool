using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.EntityFrameworkCore;
using Spectre.Console;

namespace WonderDB.MigrationTool.Discovery;

/// <summary>
/// Metadata about a DbContext discovered via reflection from the Infrastructure assembly.
/// </summary>
public class DbContextInfo
{
    /// <summary>
    /// The System.Type of the DbContext class.
    /// </summary>
    public Type DbContextType { get; set; } = null!;

    /// <summary>
    /// Inferred schema name (derived from the context class name).
    /// </summary>
    public string SchemaName { get; set; } = string.Empty;

    /// <summary>
    /// Absolute path to the loaded assembly on disk.
    /// </summary>
    public string AssemblyPath { get; set; } = string.Empty;
}

/// <summary>
/// Loads the Infrastructure assembly via reflection and discovers all DbContext subclasses.
/// Will invoke 'dotnet build' if the compiled DLL is not found.
/// </summary>
public class InfrastructureLoader
{
    /// <summary>
    /// Build (if necessary) and load the Infrastructure assembly, returning the loaded Assembly.
    /// </summary>
    public async Task<Assembly> LoadAssemblyAsync(string infrastructurePath)
    {
        var csprojFile = Directory.GetFiles(infrastructurePath, "*.csproj", SearchOption.TopDirectoryOnly)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"No .csproj file found in '{infrastructurePath}'. Ensure the path points to the Infrastructure project folder.");

        var projectName = Path.GetFileNameWithoutExtension(csprojFile);
        var dllPath = FindCompiledAssembly(infrastructurePath, projectName);

        if (dllPath == null || !File.Exists(dllPath))
        {
            AnsiConsole.MarkupLine("[yellow]⚡ Compiled assembly not found. Building project...[/]");
            await BuildProjectAsync(csprojFile);

            dllPath = FindCompiledAssembly(infrastructurePath, projectName)
                ?? throw new InvalidOperationException(
                    "Build succeeded but the compiled assembly could not be located.");
        }

        AnsiConsole.MarkupLine($"[blue]ℹ Loading assembly:[/] {Path.GetFileName(dllPath)}");

        var loadContext = new MigrationAssemblyLoadContext(dllPath);
        return loadContext.LoadFromAssemblyPath(dllPath);
    }

    /// <summary>
    /// Discover all concrete classes that inherit from DbContext in the given assembly.
    /// </summary>
    public List<DbContextInfo> DiscoverDbContexts(Assembly assembly)
    {
        var baseType = typeof(DbContext);
        var contexts = new List<DbContextInfo>();

        Type[] exportedTypes;
        try
        {
            exportedTypes = assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Some types may fail to load due to missing dependencies — use the ones that loaded
            exportedTypes = ex.Types.Where(t => t != null).ToArray()!;
        }

        foreach (var type in exportedTypes)
        {
            if (type.IsAbstract || type.IsInterface || !baseType.IsAssignableFrom(type))
                continue;

            contexts.Add(new DbContextInfo
            {
                DbContextType = type,
                SchemaName = DeriveSchemaName(type),
                AssemblyPath = assembly.Location
            });
        }

        return contexts;
    }

    /// <summary>
    /// Derive a schema name from the context type name by convention.
    /// "OrderDbContext" → "orders", "AuditContext" → "audit"
    /// </summary>
    private static string DeriveSchemaName(Type contextType)
    {
        var name = contextType.Name;

        if (name.EndsWith("DbContext", StringComparison.OrdinalIgnoreCase))
            name = name[..^9];
        else if (name.EndsWith("Context", StringComparison.OrdinalIgnoreCase))
            name = name[..^7];

        return name.ToLowerInvariant();
    }

    /// <summary>
    /// Search bin/Debug and bin/Release for the project's compiled DLL across all TFM folders.
    /// </summary>
    private static string? FindCompiledAssembly(string infrastructurePath, string projectName)
    {
        string[] configurations = ["Debug", "Release"];

        foreach (var config in configurations)
        {
            var basePath = Path.Combine(infrastructurePath, "bin", config);

            if (!Directory.Exists(basePath))
                continue;

            foreach (var tfmDir in Directory.GetDirectories(basePath))
            {
                var dllPath = Path.Combine(tfmDir, $"{projectName}.dll");
                if (File.Exists(dllPath))
                    return dllPath;
            }
        }

        return null;
    }

    /// <summary>
    /// Invoke 'dotnet build' on the given project and throw on failure.
    /// </summary>
    private static async Task BuildProjectAsync(string csprojPath)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build \"{csprojPath}\" -c Debug --nologo -v quiet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            AnsiConsole.MarkupLine("[red]✗ Build failed![/]");

            if (!string.IsNullOrWhiteSpace(error))
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]");
            if (!string.IsNullOrWhiteSpace(output))
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(output)}[/]");

            throw new InvalidOperationException(
                "dotnet build failed. Fix the compilation errors above and retry.");
        }

        AnsiConsole.MarkupLine("[green]✓ Build succeeded.[/]");
    }
}

/// <summary>
/// Custom AssemblyLoadContext that resolves dependencies from the same directory as the loaded assembly.
/// Uses a collectible context so the assembly can be unloaded later.
/// </summary>
internal sealed class MigrationAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public MigrationAssemblyLoadContext(string assemblyPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(assemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return assemblyPath != null ? LoadFromAssemblyPath(assemblyPath) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return libraryPath != null ? LoadUnmanagedDllFromPath(libraryPath) : IntPtr.Zero;
    }
}
