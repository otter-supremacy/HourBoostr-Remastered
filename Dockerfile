FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY NuGet.Config Directory.Build.props global.json ./
COPY src/HourBoostr.Remastered/HourBoostr.Remastered.csproj src/HourBoostr.Remastered/
RUN dotnet restore src/HourBoostr.Remastered/HourBoostr.Remastered.csproj
COPY src/ src/
COPY LICENSE NOTICE ./
COPY *.md ./
COPY docs/ docs/
COPY scripts/ scripts/
RUN dotnet publish src/HourBoostr.Remastered/HourBoostr.Remastered.csproj -c Release --no-restore -o /app
FROM mcr.microsoft.com/dotnet/aspnet:10.0
USER root
RUN mkdir /data && chown app:app /data
WORKDIR /app
COPY --from=build /app ./
USER app
ENV HBR_DATA_DIR=/data DOTNET_CLI_TELEMETRY_OPTOUT=1
VOLUME /data
HEALTHCHECK --interval=60s --start-period=30s --timeout=15s CMD ["dotnet", "HourBoostr.Remastered.dll", "status"]
ENTRYPOINT ["dotnet", "HourBoostr.Remastered.dll"]
CMD ["serve"]
