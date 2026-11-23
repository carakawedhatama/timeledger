FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/TimeLedger.Domain/TimeLedger.Domain.csproj src/TimeLedger.Domain/
COPY src/TimeLedger.Api/TimeLedger.Api.csproj src/TimeLedger.Api/
RUN dotnet restore src/TimeLedger.Api/TimeLedger.Api.csproj
COPY src/ src/
RUN dotnet publish src/TimeLedger.Api/TimeLedger.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
# The non-root user built into the .NET images.
USER $APP_UID
ENTRYPOINT ["dotnet", "TimeLedger.Api.dll"]
