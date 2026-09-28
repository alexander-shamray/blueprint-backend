# src/Services/Shipping/Shipping.Migrator/Dockerfile

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
COPY src/Services/Shipping/Shipping.Domain/Shipping.Domain.csproj src/Services/Shipping/Shipping.Domain/
COPY src/Services/Shipping/Shipping.Application/Shipping.Application.csproj src/Services/Shipping/Shipping.Application/
COPY src/Services/Shipping/Shipping.Infrastructure/Shipping.Infrastructure.csproj src/Services/Shipping/Shipping.Infrastructure/
COPY src/Services/Shipping/Shipping.Migrator/Shipping.Migrator.csproj src/Services/Shipping/Shipping.Migrator/
RUN dotnet restore src/Services/Shipping/Shipping.Migrator/Shipping.Migrator.csproj

# .editorconfig is a build input under ADR-019 — without it this publish
# enforces a weaker style policy than every other build.
COPY .editorconfig ./
COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Services/Shipping/ src/Services/Shipping/

# Ordering's contract. The migrator references Shipping.Infrastructure, which
# compiles that .proto by relative path as its csproj argues, so the file has to
# be in the build context though no Ordering project is.
COPY src/Services/Ordering/Ordering.Api/Protos/ src/Services/Ordering/Ordering.Api/Protos/

RUN dotnet publish src/Services/Shipping/Shipping.Migrator/Shipping.Migrator.csproj \
    -c Release -o /app/publish --no-restore /p:UseAppHost=false

# Runtime, not aspnet — the migrator has no listener. -extra, because plain
# chiseled runs globalization-invariant and Microsoft.Data.SqlClient refuses
# it; ICU and tzdata are the whole difference.
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled-extra AS final
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Shipping.Migrator.dll"]
