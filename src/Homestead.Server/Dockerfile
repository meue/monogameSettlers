# The multiplayer server (docs/MULTIPLAYER.md). Built from the repository's root, which holds the version (git tags, MinVer):
#   docker build -f src/Homestead.Server/Dockerfile -t homestead-server .
#   docker run -p 8080:8080 homestead-server
# Without .git (e.g. a source archive): --build-arg VERSION=1.2.3.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG VERSION=
WORKDIR /source
COPY . .
RUN dotnet publish src/Homestead.Server/Homestead.Server.csproj -c Release -o /app \
    ${VERSION:+-p:MinVerVersionOverride=$VERSION}

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Homestead.Server.dll"]
