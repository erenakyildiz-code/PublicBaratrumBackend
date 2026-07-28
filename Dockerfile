# ------------------------------
# 1. Build Stage
# ------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project file first (for layer caching)
COPY AuthApp.csproj .
RUN dotnet restore

# Copy everything else and build
COPY . .
RUN dotnet publish -c Release -o /app/publish --no-restore

# ------------------------------
# 2. Runtime Stage
# ------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN mkdir -p /app/Storage && chown app:app /app/Storage
COPY --chown=app:app system.txt /app/system.txt
COPY --from=build --chown=app:app /app/publish .

USER app

EXPOSE 8080

ENTRYPOINT ["dotnet", "AuthApp.dll"]