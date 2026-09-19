# syntax=docker/dockerfile:1

# The tag names the exact patch global.json pins (§4.4), so a bump there is a
# bump here, in the same change. It floated on 10.0-noble until the copied-in
# global.json proved the point it was there to prove: the tag moved to
# 10.0.400, `rollForward: disable` refused it, and every restore below exited
# 155. Loud is right for a drift nobody chose and wrong for the image itself,
# which has to build — so the tag obeys the pin and global.json stays as the
# belt on those braces.
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
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/BuildingBlocks/Common.Domain/Common.Domain.csproj src/BuildingBlocks/Common.Domain/
COPY src/BuildingBlocks/Common.Application/Common.Application.csproj src/BuildingBlocks/Common.Application/
COPY src/BuildingBlocks/Common.Contracts/Common.Contracts.csproj src/BuildingBlocks/Common.Contracts/
COPY src/BuildingBlocks/Common.Infrastructure/Common.Infrastructure.csproj src/BuildingBlocks/Common.Infrastructure/
COPY src/BuildingBlocks/Common.Web/Common.Web.csproj src/BuildingBlocks/Common.Web/
COPY src/Services/Payments/Payments.Domain/Payments.Domain.csproj src/Services/Payments/Payments.Domain/
COPY src/Services/Payments/Payments.Application/Payments.Application.csproj src/Services/Payments/Payments.Application/
COPY src/Services/Payments/Payments.Infrastructure/Payments.Infrastructure.csproj src/Services/Payments/Payments.Infrastructure/
COPY src/Services/Payments/Payments.Api/Payments.Api.csproj src/Services/Payments/Payments.Api/
RUN dotnet restore src/Services/Payments/Payments.Api/Payments.Api.csproj

# .editorconfig rides with the source, not the restore inputs: it is a build
# input under ADR-019 (EnforceCodeStyleInBuild reads it), and without it this
# publish would enforce a weaker style policy than every other build.
COPY .editorconfig ./
COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Services/Payments/ src/Services/Payments/
RUN dotnet publish src/Services/Payments/Payments.Api/Payments.Api.csproj \
    -c $BUILD_CONFIGURATION -o /app/publish --no-restore /p:UseAppHost=false

# -extra, not plain chiseled: the plain image runs in globalization-invariant
# mode and Microsoft.Data.SqlClient refuses to open a connection under it —
# the migrator found this at first run, and every service here talks to SQL.
# The extra layer is ICU and tzdata, nothing else; still no shell, no root.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS final
WORKDIR /app
COPY --from=build /app/publish .

# Chiselled images ship no shell and run as a non-root user by default.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Payments.Api.dll"]
