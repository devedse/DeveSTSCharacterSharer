# The build stage always runs natively: a framework-dependent .NET app is the same for every CPU,
# so it's built once and copied into the amd64 and arm64 images (no slow emulated SDK).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_VERSION=1.0.0
WORKDIR /src
COPY src/DeveSTSCharacterSharer/DeveSTSCharacterSharer.csproj src/DeveSTSCharacterSharer/
RUN dotnet restore src/DeveSTSCharacterSharer/DeveSTSCharacterSharer.csproj
COPY src/ src/
# Restore again now the .razor files are here: that's what pulls in the package serving blazor.web.js.
RUN dotnet publish src/DeveSTSCharacterSharer/DeveSTSCharacterSharer.csproj -c Release -o /app /p:UseAppHost=false /p:Version=$BUILD_VERSION

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# FORWARDEDHEADERS: behind a reverse proxy, use its https:// scheme and host for links.
ENV DATA_DIR=/data \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
EXPOSE 8080
ENTRYPOINT ["dotnet", "DeveSTSCharacterSharer.dll"]
