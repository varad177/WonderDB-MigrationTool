# WonderDB Migration Tool

A powerful .NET CLI tool by **WonderBiz Technologies** for managing database migrations in .NET Clean Architecture projects. Discovers `DbContext` classes from Infrastructure assemblies via reflection, resolves configuration automatically, and runs EF Core and MongoDB migrations programmatically.

---

## ✨ Features

- **Auto-discovery** — Scans workspace for Infrastructure projects and discovers `DbContext` classes via reflection
- **Multi-provider** — Supports SQL Server, PostgreSQL, MongoDB, and SQLite
- **Interactive CLI** — Beautiful prompts powered by Spectre.Console when no flags are provided
- **Batch mode** — Migrate all discovered projects in one command
- **Dry run** — Preview pending migrations and generated SQL without applying changes
- **Rollback** — Revert to any previous migration safely
- **Audit logging** — Every operation logged to `wonderdb-audit.log` (and optionally to the target DB)
- **Docker support** — Run as a Docker Compose service with volume-mounted workspace
- **Connection testing** — Fail fast with clear errors if the database is unreachable

---

## 📦 Installation

### Option 1: .NET Global Tool

```bash
dotnet tool install -g wonderdb-migration-tool
```

After installation, the `wonderdb` command is available globally.

### Option 2: Docker Compose

Add the service to your `docker-compose.yml`:

```yaml
services:
  wonderdb-migration:
    build:
      context: ./WonderDB.MigrationTool
      dockerfile: Docker/Dockerfile
    container_name: wonderdb-migration
    stdin_open: true
    tty: true
    volumes:
      - .:/workspace
    working_dir: /workspace
    environment:
      - ASPNETCORE_ENVIRONMENT=Development
    command: ["tail", "-f", "/dev/null"]
```

Then run:

```bash
docker compose up -d wonderdb-migration
docker exec -it wonderdb-migration wonderdb migrate
```

---

## 🚀 Commands

### `wonderdb migrate`

Full interactive migration flow. Prompts for Infrastructure path, database, DbContext, and mode.

```bash
# Interactive mode
wonderdb migrate

# Direct path — skip project discovery prompt
wonderdb migrate --infrastructure ./OrderService/src/OrderService.Infrastructure

# Batch migrate all discovered projects
wonderdb migrate --all

# Preview pending migrations without applying
wonderdb migrate --dry-run

# Specify environment
wonderdb migrate --env Production

# Write audit to target database
wonderdb migrate --infrastructure ./path --audit-db
```

### `wonderdb status`

Show applied vs pending migrations per DbContext.

```bash
wonderdb status
wonderdb status --infrastructure ./OrderService/src/OrderService.Infrastructure
wonderdb status --env Staging
```

### `wonderdb rollback`

Revert to a specific migration.

```bash
wonderdb rollback --to 20240101120000_InitialCreate
wonderdb rollback --to 20240101120000_InitialCreate --infrastructure ./path
```

### `wonderdb generate`

Scaffold a new EF Core or MongoDB migration.

```bash
wonderdb generate --name AddOrderTable
wonderdb generate --name AddOrderTable --infrastructure ./path
```

### `wonderdb history`

Show full audit log of all migration operations.

```bash
wonderdb history
wonderdb history --limit 20
```

### `wonderdb list-projects`

List all discovered .NET projects in the workspace.

```bash
wonderdb list-projects
wonderdb list-projects --path /workspace
```

### `wonderdb list-contexts`

List all DbContext classes found in an Infrastructure assembly.

```bash
wonderdb list-contexts
wonderdb list-contexts --infrastructure ./OrderService/src/OrderService.Infrastructure
```

---

## 🔄 Interactive Flow

When you run `wonderdb migrate` without flags, you'll get a guided interactive experience:

```
? Enter path to Infrastructure folder (or press Enter to auto-scan workspace):
  > /workspace/OrderService/src/OrderService.Infrastructure

? Select database:
  > OrderDb  (SQL Server)
    AuditDb  (PostgreSQL)

? Select DbContext / schema:
  > All
    OrderDbContext   (schema: orders)
    AuditDbContext   (schema: audit)

? Select mode:
  > Migrate & Update
    Dry Run
    Status / Diff
    Rollback
    Generate Migration
```

---

## 🏗️ Project Structure

```
WonderDB.MigrationTool/
├── Commands/
│   ├── MigrateCommand.cs         ← Main migration command (interactive/direct/batch)
│   ├── StatusCommand.cs          ← Show applied vs pending
│   ├── RollbackCommand.cs        ← Revert to target migration
│   ├── GenerateCommand.cs        ← Scaffold new migration
│   ├── HistoryCommand.cs         ← Audit log viewer
│   ├── ListProjectsCommand.cs   ← Workspace project scanner
│   └── ListContextsCommand.cs   ← DbContext discovery viewer
├── Interactive/
│   └── PromptService.cs          ← Spectre.Console interactive prompts
├── Discovery/
│   ├── InfrastructureLoader.cs   ← Assembly loading + DbContext discovery
│   ├── ConfigResolver.cs         ← appsettings.json resolution
│   ├── ProviderDetector.cs       ← DB provider detection from connection string
│   └── ProjectScanner.cs         ← Workspace scanning for *.Infrastructure projects
├── Providers/
│   ├── IDbMigrationProvider.cs   ← Provider interface contract
│   ├── IMongoMigration.cs        ← MongoDB migration interface
│   ├── MigrationContext.cs       ← Shared context passed to providers
│   ├── MigrationStatus.cs        ← Migration status model
│   ├── EFCoreMigrationProvider.cs← EF Core implementation
│   └── MongoMigrationProvider.cs ← MongoDB implementation
├── Connection/
│   └── ConnectionTester.cs       ← Pre-flight database connectivity check
├── Audit/
│   └── AuditLogger.cs            ← File and DB audit logging
├── Docker/
│   └── Dockerfile
├── Program.cs                    ← Entry point (DI + command registration only)
└── WonderDB.MigrationTool.csproj
```

---

## 🔌 Adding a New Database Provider

1. **Implement `IDbMigrationProvider`** in the `Providers/` folder:

```csharp
public class MySqlMigrationProvider : IDbMigrationProvider
{
    public async Task MigrateAsync(MigrationContext context) { /* ... */ }
    public async Task<IEnumerable<MigrationStatus>> GetStatusAsync(MigrationContext context) { /* ... */ }
    public async Task DryRunAsync(MigrationContext context) { /* ... */ }
    public async Task RollbackAsync(MigrationContext context, string targetMigration) { /* ... */ }
    public async Task GenerateAsync(MigrationContext context, string migrationName) { /* ... */ }
}
```

2. **Add the provider type** to `DbProviderType` enum in `Discovery/ProviderDetector.cs`:

```csharp
public enum DbProviderType
{
    SqlServer,
    PostgreSQL,
    MongoDB,
    SQLite,
    MySql,     // ← Add here
    Unknown
}
```

3. **Add detection logic** in `ProviderDetector.Detect()`:

```csharp
if (Regex.IsMatch(connectionString, @"Server=.*Port=3306", RegexOptions.IgnoreCase))
    return DbProviderType.MySql;
```

4. **Register in DI** in `Program.cs`:

```csharp
services.AddSingleton<MySqlMigrationProvider>();
```

5. **Add connection testing** in `ConnectionTester.cs` for the new provider.

6. **Wire it up** in `MigrateCommand.ResolveProvider()`:

```csharp
DbProviderType.MySql => services.GetRequiredService<MySqlMigrationProvider>(),
```

---

## 🐳 Docker Compose Integration

For projects that use Docker Compose, add the migration service to your existing `docker-compose.yml`:

```yaml
services:
  # Your application services...
  app:
    build: .
    ports:
      - "5000:5000"

  # Database
  sqlserver:
    image: mcr.microsoft.com/mssql/server:2022-latest
    environment:
      SA_PASSWORD: "YourStrong!Passw0rd"
      ACCEPT_EULA: "Y"

  # WonderDB Migration Tool
  wonderdb-migration:
    build:
      context: ./WonderDB.MigrationTool
      dockerfile: Docker/Dockerfile
    container_name: wonderdb-migration
    stdin_open: true
    tty: true
    volumes:
      - .:/workspace
    working_dir: /workspace
    depends_on:
      - sqlserver
    environment:
      - ASPNETCORE_ENVIRONMENT=Development
      - ConnectionStrings__DefaultConnection=Server=sqlserver;Database=MyDb;User=sa;Password=YourStrong!Passw0rd;TrustServerCertificate=true
    command: ["tail", "-f", "/dev/null"]
```

Then run migrations with:

```bash
docker compose up -d
docker exec -it wonderdb-migration wonderdb migrate --all
```

---

## 🛠️ Tech Stack

| Concern | Package |
|---------|---------|
| CLI argument parsing | `System.CommandLine` |
| Interactive prompts + colored output | `Spectre.Console` |
| Configuration reading | `Microsoft.Extensions.Configuration` |
| Dependency injection | `Microsoft.Extensions.DependencyInjection` |
| EF Core migrations | `Microsoft.EntityFrameworkCore.Design` |
| SQL Server | `Microsoft.EntityFrameworkCore.SqlServer` |
| PostgreSQL | `Npgsql.EntityFrameworkCore.PostgreSQL` |
| MongoDB | `MongoDB.Driver` |

---

## 📝 Audit Log Format

Each entry in `wonderdb-audit.log` is a JSON line:

```json
{
  "timestamp": "2024-01-15T10:30:00Z",
  "project": "OrderService",
  "database": "OrderDb",
  "context": "OrderDbContext",
  "migrationName": "20240115_AddOrderTable",
  "mode": "Migrate",
  "executedBy": "developer",
  "result": "Success",
  "errorMessage": null
}
```

---

## 📄 License

MIT © WonderBiz Technologies
