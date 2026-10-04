# src/BFF/Web.Bff.Migrator/Dockerfile

# Pinned to the exact patch global.json names, same as the host image (§4.4);
# a bump there is a bump in both files.
FROM mcr.microsoft.com/dotnet/sdk:10.0.302-noble AS build
WORKDIR /src

# Project files first, so restore sits in a layer that survives source-only
# changes. No Common.Web and no Web.Bff — the reference chain stops at
# Web.Bff.Persistence. Every project in it takes a line: a csproj absent at
# restore is not restored, and the --no-restore publish fails with NETSDK1004.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/BuildingBlocks/Common.Domain/Common.Domain.csproj src/BuildingBlocks/Common.Domain/
COPY src/BuildingBlocks/Common.Application/Common.Application.csproj src/BuildingBlocks/Common.Application/
COPY src/BuildingBlocks/Common.Contracts/Common.Contracts.csproj src/BuildingBlocks/Common.Contracts/
COPY src/BuildingBlocks/Common.Infrastructure/Common.Infrastructure.csproj src/BuildingBlocks/Common.Infrastructure/
COPY src/BFF/Web.Bff.Persistence/Web.Bff.Persistence.csproj src/BFF/Web.Bff.Persistence/
COPY src/BFF/Web.Bff.Migrator/Web.Bff.Migrator.csproj src/BFF/Web.Bff.Migrator/
RUN dotnet restore src/BFF/Web.Bff.Migrator/Web.Bff.Migrator.csproj

# .editorconfig is a build input under ADR-019 — without it this publish
# enforces a weaker style policy than every other build.
COPY .editorconfig ./
COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/BFF/Web.Bff.Persistence/ src/BFF/Web.Bff.Persistence/
COPY src/BFF/Web.Bff.Migrator/ src/BFF/Web.Bff.Migrator/
RUN dotnet publish src/BFF/Web.Bff.Migrator/Web.Bff.Migrator.csproj \
    -c Release -o /app/publish --no-restore /p:UseAppHost=false

# Runtime, not aspnet — the migrator has no listener. -extra, because plain
# chiselled runs globalization-invariant and Microsoft.Data.SqlClient refuses
# it; ICU and tzdata are the whole difference.
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled-extra AS final
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Web.Bff.Migrator.dll"]
