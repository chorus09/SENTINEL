
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

COPY ["src/Sentinel.Api/Sentinel.Api.csproj", "src/Sentinel.Api/"]
COPY ["src/Sentinel.Application/Sentinel.Application.csproj", "src/Sentinel.Application/"]
COPY ["src/Sentinel.Domain/Sentinel.Domain.csproj", "src/Sentinel.Domain/"]
COPY ["src/Sentinel.Infrastructure/Sentinel.Infrastructure.csproj", "src/Sentinel.Infrastructure/"]

RUN dotnet restore "src/Sentinel.Api/Sentinel.Api.csproj"

COPY . .

RUN dotnet publish "src/Sentinel.Api/Sentinel.Api.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Development

EXPOSE 8080

COPY --from=build /app/publish .

USER $APP_UID

ENTRYPOINT ["dotnet", "Sentinel.Api.dll"]
