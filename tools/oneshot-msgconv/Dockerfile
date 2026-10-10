# One-shot MSG -> EML converter (fidelity probe).
# Multi-stage build: SDK to compile, runtime to run.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY msgconv.csproj .
RUN dotnet restore
COPY Program.cs .
RUN dotnet publish -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .
# no writes outside /data
ENTRYPOINT ["dotnet", "/app/msgconv.dll"]
