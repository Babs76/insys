# The INSYS migrations and their runner (ADR-027). Build from the repository root: docker build -f deploy/docker/migrator.Dockerfile --build-arg REVISION=$(git rev-parse HEAD) -t insys-migrator .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
# The commit is required (Ade, 27 November 2026): the image records it as its OCI revision label and in the migrator's build.
# The build context has no .git, so pass it: --build-arg REVISION=$(git rev-parse HEAD).
ARG REVISION
RUN printf '%s' "$REVISION" | grep -Eqx '[0-9a-f]{40}' || { echo "Build with --build-arg REVISION=<the full 40-character commit>" >&2; exit 1; }
COPY . .
RUN dotnet publish tools/Insys.DbMigrator -c Release -o /app --no-self-contained -p:SourceRevisionId="$REVISION"

FROM mcr.microsoft.com/dotnet/runtime:10.0
ARG REVISION
LABEL org.opencontainers.image.revision=$REVISION
WORKDIR /app
COPY --from=build /app .
COPY db ./db
USER $APP_UID
ENTRYPOINT ["dotnet", "Insys.DbMigrator.dll"]
