# CB Test Bridge — container image. Build: docker build -t cbbridge .
# No NuGet packages: the build needs only the .NET SDK image.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY CbBridge.sln ./
COPY src/ src/
RUN dotnet publish src/CbBridge.Web -c Release -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0
# The run history uses the system SQLite library (no package) — the slim base image does not have it.
RUN apt-get update && apt-get install -y --no-install-recommends libsqlite3-0 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /publish .
# Writable folders for the non-root user: history database, result files
RUN mkdir -p /app/data /app/results && chown -R $APP_UID /app/data /app/results
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
# Liveness: GET /health (the image has no curl — let your orchestrator probe it)
USER $APP_UID
ENTRYPOINT ["dotnet", "CbBridge.Web.dll"]
