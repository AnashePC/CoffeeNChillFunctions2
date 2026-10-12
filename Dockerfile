# syntax=docker/dockerfile:1
# Build context = repository root:  docker build -t <user>/coffeenchill-functions:v1.0 .

# ---------- build stage ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY CoffeeNChillFunctions/CoffeeNChillFunctions.csproj CoffeeNChillFunctions/
RUN dotnet restore CoffeeNChillFunctions/CoffeeNChillFunctions.csproj
COPY CoffeeNChillFunctions/ CoffeeNChillFunctions/
RUN dotnet publish CoffeeNChillFunctions/CoffeeNChillFunctions.csproj -c Release -o /app/publish --no-restore

# ---------- runtime stage ----------
FROM mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated8.0 AS final
WORKDIR /home/site/wwwroot
COPY --from=build /app/publish .

ENV AzureWebJobsScriptRoot=/home/site/wwwroot \
    AzureFunctionsJobHost__Logging__Console__IsEnabled=true \
    FUNCTIONS_WORKER_RUNTIME=dotnet-isolated

# The Functions host listens on port 80 inside the container.
# AzureWebJobsStorage is NOT baked in - pass it at run time with -e.
EXPOSE 80
