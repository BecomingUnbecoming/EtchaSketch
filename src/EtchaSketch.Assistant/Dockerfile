# The EtchaSketch household assistant. Build from the repository root:
#   docker build -f src/EtchaSketch.Assistant/Dockerfile -t etchasketch-assistant .
# Its data lives in /data and its database key in /keys: keep those on separate volumes.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/EtchaSketch.Core/EtchaSketch.Core.csproj src/EtchaSketch.Core/
COPY src/EtchaSketch.Data/EtchaSketch.Data.csproj src/EtchaSketch.Data/
COPY src/EtchaSketch.Assistant/EtchaSketch.Assistant.csproj src/EtchaSketch.Assistant/
RUN dotnet restore src/EtchaSketch.Assistant
COPY src/EtchaSketch.Core/ src/EtchaSketch.Core/
COPY src/EtchaSketch.Data/ src/EtchaSketch.Data/
COPY src/EtchaSketch.Assistant/ src/EtchaSketch.Assistant/
RUN dotnet publish src/EtchaSketch.Assistant -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
RUN mkdir /data /keys && chown "$APP_UID" /data /keys && chmod 700 /keys
ENV Assistant__DataDirectory=/data Assistant__KeyDirectory=/keys
VOLUME /data /keys
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "EtchaSketch.Assistant.dll"]
