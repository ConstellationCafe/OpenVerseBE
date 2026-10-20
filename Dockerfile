FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/OpenVerseBE/OpenVerseBE.csproj src/OpenVerseBE/
RUN dotnet restore src/OpenVerseBE/OpenVerseBE.csproj
COPY src/ src/
RUN dotnet publish src/OpenVerseBE/OpenVerseBE.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
# only Caddy talks to this, over the compose network
ENV ASPNETCORE_URLS=http://0.0.0.0:5080
EXPOSE 5080
# the asset index survives restarts here (a named volume, which takes this ownership on first use)
RUN mkdir -p /app/state && chown "$APP_UID" /app/state
USER $APP_UID
ENTRYPOINT ["dotnet", "OpenVerseBE.dll"]
