# syntax=docker/dockerfile:1

# The tag names the exact patch global.json pins (§4.4), so a bump there is a
# bump here, in the same change.
FROM mcr.microsoft.com/dotnet/sdk:10.0.302-noble AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Project files first, so the restore layer really does survive source-only
# changes.
#
# **Every project in the transitive closure gets a line, and a missing one
# fails four steps later rather than here.** dotnet restore writes each
# project's own project.assets.json; a csproj absent when it runs is not
# restored, and the --no-restore publish then fails with NETSDK1004 naming a
# project this file never mentions. **CI catches it since PR-25**: ci.yml's
# `images` job builds every image a changed service ships, so a missing line
# fails `docker build` on the pull request that added the reference. It used to
# surface only on the next compose pull request, that smoke being path-filtered
# on deploy/compose/** while a reference lands under src/.
#
# What that job does NOT close is a filter that never fires. The `bff` filter
# has to name every path this image copies — including Catalog's proto, which
# is why it reaches into another service's tree (§15.1).
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/BuildingBlocks/Common.Domain/Common.Domain.csproj src/BuildingBlocks/Common.Domain/
COPY src/BuildingBlocks/Common.Application/Common.Application.csproj src/BuildingBlocks/Common.Application/
COPY src/BuildingBlocks/Common.Contracts/Common.Contracts.csproj src/BuildingBlocks/Common.Contracts/
COPY src/BuildingBlocks/Common.Infrastructure/Common.Infrastructure.csproj src/BuildingBlocks/Common.Infrastructure/
COPY src/BuildingBlocks/Common.Web/Common.Web.csproj src/BuildingBlocks/Common.Web/
COPY src/BFF/Web.Bff/Web.Bff.csproj src/BFF/Web.Bff/
RUN dotnet restore src/BFF/Web.Bff/Web.Bff.csproj

# .editorconfig rides with the source, not the restore inputs: it is a build
# input under ADR-019 (EnforceCodeStyleInBuild reads it), and without it this
# publish would enforce a weaker style policy than every other build.
COPY .editorconfig ./
COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/BFF/ src/BFF/

# The price of the linked .proto, which Web.Bff's csproj argues. Web.Bff
# compiles Catalog's contract by relative path, so the file has to be in the
# build context even though no Catalog project is — and it is a source input
# rather than a restore one, which is why it sits here and not above.
#
# The failure mode is loud and its message is misleading: without this,
# Grpc.Tools reports the .proto missing and names a path under
# src/Services/Catalog that the reader will go looking for in a Dockerfile that
# builds no Catalog project at all. A Catalog *rename* breaks it the same way.
COPY src/Services/Catalog/Catalog.Api/Protos/ src/Services/Catalog/Catalog.Api/Protos/

RUN dotnet publish src/BFF/Web.Bff/Web.Bff.csproj \
    -c $BUILD_CONFIGURATION -o /app/publish --no-restore /p:UseAppHost=false

# -extra, like every other image here. §15.2 argued the suffix from
# Microsoft.Data.SqlClient, which refuses to open a connection under
# globalization-invariant mode — and this host, like the gateway, opens no
# database connection at all; its hop is gRPC (§9.7). It takes the
# variant anyway, for the second reason that chapter gives: one base image
# across the platform, so what a host does with a culture-sensitive comparison
# never depends on which suffix somebody picked for its image. ICU and tzdata
# are the whole difference; no shell, no root.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS final
WORKDIR /app
COPY --from=build /app/publish .

# Chiselled images ship no shell and run as a non-root user by default.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Web.Bff.dll"]
