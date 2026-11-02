# The INSYS migrations and their runner (ADR-027). Build from the repository root: docker build -f deploy/docker/migrator.Dockerfile -t insys-migrator .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY . .
RUN dotnet publish tools/Insys.DbMigrator -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
COPY db ./db
USER app
ENTRYPOINT ["dotnet", "Insys.DbMigrator.dll"]
