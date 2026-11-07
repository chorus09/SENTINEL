FROM mcr.microsoft.com/dotnet/sdk:10.0

WORKDIR /src

COPY . .

RUN dotnet restore tests/Sentinel.IntegrationTests/Sentinel.IntegrationTests.csproj

ENTRYPOINT ["dotnet", "run", "--project", "tests/Sentinel.IntegrationTests/Sentinel.IntegrationTests.csproj", "--no-restore"]
