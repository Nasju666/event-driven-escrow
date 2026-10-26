# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ["src/EscrowMarketplace.Domain/EscrowMarketplace.Domain.csproj", "src/EscrowMarketplace.Domain/"]
COPY ["src/EscrowMarketplace.Application/EscrowMarketplace.Application.csproj", "src/EscrowMarketplace.Application/"]
COPY ["src/EscrowMarketplace.Infrastructure/EscrowMarketplace.Infrastructure.csproj", "src/EscrowMarketplace.Infrastructure/"]
COPY ["src/EscrowMarketplace.Api/EscrowMarketplace.Api.csproj", "src/EscrowMarketplace.Api/"]

RUN dotnet restore "src/EscrowMarketplace.Api/EscrowMarketplace.Api.csproj"

COPY . .
WORKDIR "/src/src/EscrowMarketplace.Api"
RUN dotnet publish "EscrowMarketplace.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
EXPOSE 8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "EscrowMarketplace.Api.dll"]
