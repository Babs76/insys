# The INSYS API (ADR-027). Build from the repository root: docker build -f deploy/docker/api.Dockerfile --build-arg REVISION=$(git rev-parse HEAD) -t insys-api .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
# The commit is required (Ade, 27 November 2026): the image records it as its OCI revision label and in the API's build, which
# /health/version shows. The build context has no .git, so pass it: --build-arg REVISION=$(git rev-parse HEAD).
ARG REVISION
RUN printf '%s' "$REVISION" | grep -Eqx '[0-9a-f]{40}' || { echo "Build with --build-arg REVISION=<the full 40-character commit>" >&2; exit 1; }
COPY . .
RUN dotnet publish src/Host/Insys.Api -c Release -o /app --no-self-contained -p:SourceRevisionId="$REVISION"

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG REVISION
LABEL org.opencontainers.image.revision=$REVISION
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Insys.Api.dll"]
