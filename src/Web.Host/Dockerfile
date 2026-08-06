FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY Directory.Build.props ItPlatform.slnx ./
COPY src/AppHost/AppHost.csproj src/AppHost/
COPY src/Contracts/Contracts.csproj src/Contracts/
COPY src/Modules/Assets/Modules.Assets.csproj src/Modules/Assets/
COPY src/Modules/Helpdesk/Modules.Helpdesk.csproj src/Modules/Helpdesk/
COPY src/Modules/Monitoring/Modules.Monitoring.csproj src/Modules/Monitoring/
COPY src/Platform/Platform.csproj src/Platform/
COPY src/Web.Host/Web.Host.csproj src/Web.Host/
RUN dotnet restore src/Web.Host/Web.Host.csproj

COPY src/ src/
RUN dotnet publish src/Web.Host/Web.Host.csproj --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Web.Host.dll"]
