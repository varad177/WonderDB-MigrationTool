# ── Stage 1: Build ──────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /app/publish --no-restore \
    && dotnet restore

# ── Stage 2: Runtime ────────────────────────────────────────────
# Using SDK image (not runtime) because the tool needs:
#   - dotnet ef CLI for migration generation
#   - dotnet build for compiling target projects
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS runtime
WORKDIR /app

# Install EF Core CLI tool globally
RUN dotnet tool install --global dotnet-ef --version 8.0.10
ENV PATH="$PATH:/root/.dotnet/tools"

COPY --from=build /app/publish .

# Label metadata
LABEL maintainer="WonderBiz Technologies" \
      description="WonderDB Migration Tool — Database migration CLI for .NET Clean Architecture" \
      version="1.0.0"

ENTRYPOINT ["dotnet", "/app/WonderDB.MigrationTool.dll"]
