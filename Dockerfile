FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Spiza.Api/Spiza.Api.csproj Spiza.Api/
RUN dotnet restore Spiza.Api/Spiza.Api.csproj
COPY Spiza.Api/ Spiza.Api/
RUN dotnet publish Spiza.Api/Spiza.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV SPIZA_PORT=7492 \
    SPIZA_DATABASE_PATH=/data/spiza.db
EXPOSE 7492
RUN mkdir -p /data && chown 1654:1654 /data
COPY --from=build /app/publish .
USER 1654:1654
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 CMD ["dotnet", "Spiza.Api.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "Spiza.Api.dll"]
