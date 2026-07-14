FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/CrystalJobRank.Core/ src/CrystalJobRank.Core/
COPY src/CrystalJobRank.Server/ src/CrystalJobRank.Server/
RUN dotnet publish src/CrystalJobRank.Server/CrystalJobRank.Server.csproj -c Release -o /app --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV Leaderboard__DataPath=/data/server-data.json
EXPOSE 8080
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "CrystalJobRank.Server.dll"]

