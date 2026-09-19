# src/Services/Inventory/Inventory.Migrator/Dockerfile

# Pinned to the exact patch global.json names, same as the API image (§4.4);
# a bump there is a bump in both files.
FROM mcr.microsoft.com/dotnet/sdk:10.0.302-noble AS build
WORKDIR /src

# Project files first, same as the API image and for the same reason: restore
# in a layer that survives source-only changes, which is most changes. No
# Common.Web — the migrator's reference chain stops at Infrastructure. Every
# other project in that chain takes a line, for the reason the API image
# states: a csproj absent at restore is not restored, and the --no-restore
# publish below fails with NETSDK1004 four steps later.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/BuildingBlocks/Common.Domain/Common.Domain.csproj src/BuildingBlocks/Common.Domain/
COPY src/BuildingBlocks/Common.Application/Common.Application.csproj src/BuildingBlocks/Common.Application/
COPY src/BuildingBlocks/Common.Contracts/Common.Contracts.csproj src/BuildingBlocks/Common.Contracts/
COPY src/BuildingBlocks/Common.Infrastructure/Common.Infrastructure.csproj src/BuildingBlocks/Common.Infrastructure/
COPY src/Services/Inventory/Inventory.Domain/Inventory.Domain.csproj src/Services/Inventory/Inventory.Domain/
COPY src/Services/Inventory/Inventory.Application/Inventory.Application.csproj src/Services/Inventory/Inventory.Application/
COPY src/Services/Inventory/Inventory.Infrastructure/Inventory.Infrastructure.csproj src/Services/Inventory/Inventory.Infrastructure/
COPY src/Services/Inventory/Inventory.Migrator/Inventory.Migrator.csproj src/Services/Inventory/Inventory.Migrator/
RUN dotnet restore src/Services/Inventory/Inventory.Migrator/Inventory.Migrator.csproj

# .editorconfig is a build input under ADR-019 — without it this publish
# enforces a weaker style policy than every other build.
COPY .editorconfig ./
COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Services/Inventory/ src/Services/Inventory/
RUN dotnet publish src/Services/Inventory/Inventory.Migrator/Inventory.Migrator.csproj \
    -c Release -o /app/publish --no-restore /p:UseAppHost=false

# Runtime, not aspnet — the migrator has no listener. -extra, because plain
# chiseled runs globalization-invariant and Microsoft.Data.SqlClient refuses
# it; ICU and tzdata are the whole difference.
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled-extra AS final
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Inventory.Migrator.dll"]
