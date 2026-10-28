# syntax=docker/dockerfile:1

# The tag names the exact patch global.json pins (§4.4), so a bump there is a
# bump here, in the same change. It is written out rather than floated on
# 10.0-noble because a floated tag drifted past the pin once already and made
# every restore in this repository's images exit 155.
FROM mcr.microsoft.com/dotnet/sdk:10.0.302-noble AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Project files first, so the restore layer really does survive source-only
# changes — a COPY of the whole trees before restore re-keys its layer on
# every .cs edit and the cache claim becomes fiction. global.json first among
# equals: with it copied in, a tag that has drifted off the pin is a restore
# error here rather than a silently different set of analysers in the one
# build whose output ships.
#
# **Every project in the transitive closure gets a line, and a missing one
# fails four steps later rather than here.** dotnet restore walks the graph and
# writes each project's own project.assets.json; a csproj that is not
# present when it runs is simply not restored, and the --no-restore publish
# below then fails with NETSDK1004 naming a project this file never mentions.
# So a new ProjectReference anywhere in the chain is a line here too, and
# §15.1's `images` job is what says so since PR-25: it builds every image a
# changed service ships — api and migrator alike, so two for a service that
# owns a database — and a missing line fails `docker build` on the pull request
# that added the reference. It used to surface only on the next compose pull
# request, that smoke being path-filtered on deploy/compose/** while a
# reference lands under src/.
#
# Three, not five: the gateway references Common.Web alone (§10.1 gives it no
# domain and no database), and Common.Web reaches Common.Application, which
# reaches Common.Domain. Common.Contracts and Common.Infrastructure are absent
# because nothing here names a type in either.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/BuildingBlocks/Common.Domain/Common.Domain.csproj src/BuildingBlocks/Common.Domain/
COPY src/BuildingBlocks/Common.Application/Common.Application.csproj src/BuildingBlocks/Common.Application/
COPY src/BuildingBlocks/Common.Web/Common.Web.csproj src/BuildingBlocks/Common.Web/
COPY src/Gateway/Gateway.Api/Gateway.Api.csproj src/Gateway/Gateway.Api/

# The restores of every image take turns at one NuGet HTTP cache (§15.2), so a
# full-stack build downloads each package once instead of opening every
# restore against the feed at once. The packages still land in this layer, so
# the publish below never reads the mount, and a pruned cache costs a download.
RUN --mount=type=cache,id=nuget-http-cache,target=/root/.nuget/http-cache,sharing=locked \
    NUGET_HTTP_CACHE_PATH=/root/.nuget/http-cache \
    dotnet restore src/Gateway/Gateway.Api/Gateway.Api.csproj

# .editorconfig rides with the source, not the restore inputs: it is a build
# input under ADR-019 (EnforceCodeStyleInBuild reads it), and without it this
# publish would enforce a weaker style policy than every other build.
COPY .editorconfig ./
COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Gateway/ src/Gateway/
RUN dotnet publish src/Gateway/Gateway.Api/Gateway.Api.csproj \
    -c $BUILD_CONFIGURATION -o /app/publish --no-restore /p:UseAppHost=false

# -extra, like every other image here. §15.2 argued the suffix from
# Microsoft.Data.SqlClient, which refuses to open a connection under
# globalization-invariant mode — and the gateway opens no connection at all
# (§10.1). It takes the variant anyway, and the chapter now gives the second
# reason: one base image across the platform, so what a host does with a
# culture-sensitive comparison never depends on which suffix somebody picked
# for its image. ICU and tzdata are the whole difference; no shell, no root.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS final
WORKDIR /app
COPY --from=build /app/publish .

# Chiselled images ship no shell and run as a non-root user by default.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Gateway.Api.dll"]
