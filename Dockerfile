# msg2eml — multi-stage build (plan, section 10)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# separate restore to leverage the layer cache
COPY Directory.Build.props .
COPY src/Msg2Eml.Core/Msg2Eml.Core.csproj src/Msg2Eml.Core/
COPY src/Msg2Eml.Web/Msg2Eml.Web.csproj  src/Msg2Eml.Web/
COPY src/Msg2Eml.Server/Msg2Eml.Server.csproj src/Msg2Eml.Server/
RUN dotnet restore src/Msg2Eml.Server/Msg2Eml.Server.csproj

COPY src/ src/
RUN dotnet publish src/Msg2Eml.Server/Msg2Eml.Server.csproj -c Release --no-restore -o /app

# ------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

ENV DOTNET_EnableDiagnostics=0 \
    ASPNETCORE_HTTP_PORTS=8080

# default non-root user of the aspnet images (uid 1654)
USER $APP_UID
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
  CMD ["dotnet", "msg2eml-server.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "msg2eml-server.dll"]
