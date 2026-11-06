FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY . .
RUN dotnet restore src/MeterFlow.Api --locked-mode && dotnet publish src/MeterFlow.Api -c Release --no-restore -o /app
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /var/meterflow/keys && chown $APP_UID:$APP_UID /var/meterflow/keys
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "MeterFlow.Api.dll"]
