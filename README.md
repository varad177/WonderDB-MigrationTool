# WonderDB Migration Tool

A powerful .NET CLI tool by **WonderBiz Technologies** for managing database migrations in .NET Clean Architecture projects. Discovers `DbContext` classes from Infrastructure assemblies via reflection, resolves configuration automatically, and executes EF Core and MongoDB migrations programmatically.

Designed with a **first-class multi-tenant architecture**, this tool dynamically injects schemas at runtime, completely eliminating the painful constraints of Entity Framework Core's hardcoded schema design.

---

## ✨ Features

- **True Multi-Tenancy (Dynamic Schemas)** — Run `Migrate & Update` and simply type the client's schema name. The tool dynamically overrides the schema and `__EFMigrationsHistory` table at runtime without hardcoding it in your C# files!
- **Auto-discovery** — Scans workspace for Infrastructure projects and discovers `DbContext` classes via reflection.
- **Smart Filtering** — Automatically hides incompatible `DbContexts` (e.g., hides Postgres contexts if connected to SQL Server) to prevent accidental cross-database executions.
- **Smart Defaults (Schema Memory)** — Intelligently defaults to the correct schema name. For the first run, it dynamically computes a default (e.g., `InventorySqlDbContext` → `inventorysql`). After that, it remembers the exact schema you used last time.
- **Interactive CLI** — Beautiful prompts powered by `Spectre.Console` when no flags are provided.
- **Dry run & Rollback** — Safely preview raw SQL or revert to any previous migration—fully supporting client-specific schemas.
- **Docker First** — Run as a Docker Compose service with a volume-mounted workspace. Guaranteed to work seamlessly across Windows and Linux environments.
- **Robust Error Handling** — Global exception catching prevents the CLI from crashing to the shell when EF Core commands fail.

---
## ✨ Architectural Diagram
<img width="1228" height="819" alt="WonderDb-Picsart" src="https://github.com/user-attachments/assets/44862471-051e-49cd-be77-84b84331e58c" />



---

## 📦 Installation & Usage

### Option 1: Docker Compose (Recommended)

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

Then run the interactive tool from inside the container:

```bash
docker compose up -d wonderdb-migration
docker exec -it wonderdb-migration wonderdb migrate
```

### Option 2: .NET Global Tool

```bash
dotnet tool install -g wonderdb-migration-tool
```

After installation, the `wonderdb` command is available globally.

---

## 🚀 The Multi-Tenant Workflow

The tool splits EF Core migrations into two distinct phases to support multi-tenancy seamlessly:

### 1. Generate Migration
Use this **only when you change your C# Entities** (e.g., adding a table or column). 
The tool will silently generate schema-agnostic C# migration files. It will **not** ask you for a schema name, guaranteeing that your C# files never get hardcoded to a specific client.
```bash
# From the interactive menu, select 'Generate Migration'
```

### 2. Runtime Operations (Migrate, Dry Run, Rollback, Status)
Use these when you want to deploy, preview, or inspect your migrations for a specific client.
The tool **will** ask you for a schema name (e.g., `clientA_prod`). 
- **Migrate & Update**: Connects to the DB, sets up the `__EFMigrationsHistory` table inside the provided schema, and safely creates/alters the tables specifically for that client.
- **Dry Run**: Generates a raw SQL script where every `CREATE/ALTER TABLE` command perfectly targets the exact client schema you provided.
- **Rollback**: Reads the history table inside your specified schema, and cleanly removes the migrations from that client's database without touching any other client's data.
- **Status / Diff**: Scans the `__EFMigrationsHistory` table inside the requested schema to show exactly what is pending for that specific client.

*Note on Schema Defaults*: If you press `Enter` without typing anything, the tool intelligently falls back to the default schema shown in green brackets (e.g., `(default: inventorysql)`). It derives this by stripping "DbContext" from your class name, and will remember your inputs for future runs!

---

## 🛠️ Command Reference

### `wonderdb migrate`
Full interactive migration flow. Prompts for Infrastructure path, database, DbContext, and mode.
```bash
wonderdb migrate
```

### `wonderdb status`
Show applied vs pending migrations per DbContext and target schema.
```bash
wonderdb status
```

### `wonderdb rollback`
Revert to a specific migration safely within a specific schema.
```bash
wonderdb rollback --to 20240101120000_InitialCreate
```

### `wonderdb generate`
Scaffold a new EF Core migration.
```bash
wonderdb generate --name AddOrderTable
```

### `wonderdb list-projects`
List all discovered .NET projects in the workspace.
```bash
wonderdb list-projects
```

### `wonderdb list-contexts`
List all DbContext classes found in an Infrastructure assembly.
```bash
wonderdb list-contexts
```

---

## 🔄 Interactive Flow Example

```
? Enter path to Infrastructure folder (or press Enter to auto-scan workspace):
  > /workspace/OrderService/src/OrderService.Infrastructure

? Select database:
  > OrderDb  (SQL Server)

? Select mode:
  > Migrate & Update
    Dry Run
    Status / Diff
    Rollback
    Generate Migration

? Schema name (default: inventorysql):
  > varad_client
```

---

## 🏗️ Architecture & Tech Stack

| Concern | Package / Implementation |
|---------|---------|
| CLI argument parsing | `System.CommandLine` |
| Interactive UI | `Spectre.Console` |
| EF Core Schema Injection | Custom Regex Rewriting + IDesignTimeDbContextFactory |
| SQL Server | `Microsoft.EntityFrameworkCore.SqlServer` |
| PostgreSQL | `Npgsql.EntityFrameworkCore.PostgreSQL` |

### How Dynamic Schemas Work Under the Hood
When you generate a migration, the tool forces EF Core to emit `schema: "__WONDERDB_DYNAMIC_SCHEMA__"`. A post-generation script instantly rewrites the C# files to fallback to `System.Environment.GetEnvironmentVariable("WONDERDB_SCHEMA")`, and automatically restores the original DbContext name so PostgreSQL and SQL Server migrations never collide.
During runtime operations, the tool prompts for your schema and securely injects it into the process environment variables and DbContextOptions, natively tricking EF Core into perfectly isolating your multi-tenant data!

---


