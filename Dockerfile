# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG VERSION=0.0.0
WORKDIR /src

# Restore first for better layer caching.
COPY MusicManager.slnx ./
COPY src/MusicManager.Core/MusicManager.Core.csproj src/MusicManager.Core/
COPY src/MusicManager.Web/MusicManager.Web.csproj src/MusicManager.Web/
RUN dotnet restore src/MusicManager.Web/MusicManager.Web.csproj

COPY src/ src/
RUN dotnet publish src/MusicManager.Web/MusicManager.Web.csproj -c Release -o /app --no-restore -p:Version=${VERSION}

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# ffmpeg/ffprobe for conversion, fpcalc (Chromaprint) for fingerprints.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg libchromaprint-tools \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080 \
    MusicManager__InboxPath=/inbox \
    MusicManager__LibraryPath=/music \
    MusicManager__TrashPath=/trash \
    MusicManager__DataPath=/data \
    HOME=/tmp

# Commit the image was built from, shown in the app bar. Last, so a new commit only changes this layer.
ARG COMMIT=""
ARG COMMIT_TIME=""
ENV MUSICMANAGER_COMMIT=${COMMIT} \
    MUSICMANAGER_COMMIT_TIME=${COMMIT_TIME}

VOLUME ["/inbox", "/music", "/trash", "/data"]
EXPOSE 8080
ENTRYPOINT ["dotnet", "MusicManager.Web.dll"]
