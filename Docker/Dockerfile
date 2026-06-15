# ── Stage 1: Build ──────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore
RUN dotnet publish -c Release -o /app/publish --no-restore

# ── Stage 2: Runtime ────────────────────────────────────────────
# Using SDK image (not runtime) because the tool needs:
#   - dotnet ef CLI for migration generation
#   - dotnet build for compiling target projects
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS runtime
WORKDIR /app

# Install EF Core CLI tool globally (matching the target project's EF version)
RUN dotnet tool install --global dotnet-ef --version 8.0.11
ENV PATH="$PATH:/root/.dotnet/tools"

COPY --from=build /app/publish .

# Create a 'wonderdb' shell wrapper so the command is available in $PATH
RUN printf '#!/bin/sh\ndotnet /app/WonderDB.MigrationTool.dll "$@"\n' \
    > /usr/local/bin/wonderdb && chmod +x /usr/local/bin/wonderdb

# Label metadata
LABEL maintainer="WonderBiz Technologies" \
      description="WonderDB Migration Tool — Database migration CLI for .NET Clean Architecture" \
      version="1.0.0"

ENTRYPOINT ["dotnet", "/app/WonderDB.MigrationTool.dll"]
