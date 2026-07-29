FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/Demo.Contracts/Demo.Contracts.csproj", "src/Demo.Contracts/"]
COPY ["src/Demo.UploadApi/Demo.UploadApi.csproj", "src/Demo.UploadApi/"]
COPY ["db/schema.sqlite.sql", "db/schema.sqlite.sql"]
RUN dotnet restore "src/Demo.UploadApi/Demo.UploadApi.csproj"
COPY . .
RUN dotnet publish "src/Demo.UploadApi/Demo.UploadApi.csproj" -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /data/media/temp/uploads /data/media/temp/transcodes /data/media/source /data/media/output /data/db \
    && chown -R app:app /data
USER app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Demo.UploadApi.dll"]
