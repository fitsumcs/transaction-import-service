# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Restore in a separate layer so source edits do not invalidate the package cache.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/TransactionImport.Domain/TransactionImport.Domain.csproj src/TransactionImport.Domain/
COPY src/TransactionImport.Application/TransactionImport.Application.csproj src/TransactionImport.Application/
COPY src/TransactionImport.Infrastructure/TransactionImport.Infrastructure.csproj src/TransactionImport.Infrastructure/
COPY src/TransactionImport.Api/TransactionImport.Api.csproj src/TransactionImport.Api/
COPY src/TransactionImport.Worker/TransactionImport.Worker.csproj src/TransactionImport.Worker/
COPY src/TransactionImport.Migrator/TransactionImport.Migrator.csproj src/TransactionImport.Migrator/
RUN dotnet restore src/TransactionImport.Api/TransactionImport.Api.csproj \
 && dotnet restore src/TransactionImport.Worker/TransactionImport.Worker.csproj \
 && dotnet restore src/TransactionImport.Migrator/TransactionImport.Migrator.csproj

COPY src/ src/

FROM build AS publish-api
RUN dotnet publish src/TransactionImport.Api/TransactionImport.Api.csproj -c Release -o /app --no-restore

FROM build AS publish-worker
RUN dotnet publish src/TransactionImport.Worker/TransactionImport.Worker.csproj -c Release -o /app --no-restore

FROM build AS publish-migrator
RUN dotnet publish src/TransactionImport.Migrator/TransactionImport.Migrator.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime-base
WORKDIR /app
# ICU provides Unicode normalization used by description normalization.
RUN apk add --no-cache icu-libs \
 && mkdir -p /data/uploads \
 && chown -R $APP_UID:$APP_UID /data/uploads
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    DOTNET_gcServer=1
EXPOSE 8080
USER $APP_UID

FROM runtime-base AS api
COPY --from=publish-api /app .
ENTRYPOINT ["dotnet", "TransactionImport.Api.dll"]

FROM runtime-base AS worker
COPY --from=publish-worker /app .
ENTRYPOINT ["dotnet", "TransactionImport.Worker.dll"]

FROM runtime-base AS migrator
COPY --from=publish-migrator /app .
ENTRYPOINT ["dotnet", "TransactionImport.Migrator.dll"]
