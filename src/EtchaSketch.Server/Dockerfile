# The EtchaSketch sync server. Build from the repository root:
#   docker build -f src/EtchaSketch.Server/Dockerfile -t etchasketch-sync .
# It only ever stores sealed changes it can't read, in /data.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/EtchaSketch.Core/EtchaSketch.Core.csproj src/EtchaSketch.Core/
COPY src/EtchaSketch.Data/EtchaSketch.Data.csproj src/EtchaSketch.Data/
COPY src/EtchaSketch.Server/EtchaSketch.Server.csproj src/EtchaSketch.Server/
RUN dotnet restore src/EtchaSketch.Server
COPY src/EtchaSketch.Core/ src/EtchaSketch.Core/
COPY src/EtchaSketch.Data/ src/EtchaSketch.Data/
COPY src/EtchaSketch.Server/ src/EtchaSketch.Server/
RUN dotnet publish src/EtchaSketch.Server -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
RUN mkdir /data && chown "$APP_UID" /data
ENV Sync__StorePath=/data/sync.db
VOLUME /data
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "EtchaSketch.Server.dll"]
