# Stage 1: Build & Publish
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files and restore dependencies
# PageToMovie.Web.csproj sets RequiresAspNetWebAssets=true so restore pulls
# Microsoft.AspNetCore.App.Internal.Assets (blazor.web.js). Api keeps that false
# so publish does not double-register the same framework files.
COPY host/PageToMovie.Core/PageToMovie.Core.csproj host/PageToMovie.Core/
COPY host/PageToMovie.Engine/PageToMovie.Engine.csproj host/PageToMovie.Engine/
COPY host/PageToMovie.Fakes/PageToMovie.Fakes.csproj host/PageToMovie.Fakes/
COPY host/PageToMovie.Web/PageToMovie.Web.csproj host/PageToMovie.Web/
COPY host/PageToMovie.Api/PageToMovie.Api.csproj host/PageToMovie.Api/
RUN dotnet restore host/PageToMovie.Api/PageToMovie.Api.csproj

# Force Railway Docker cache invalidation when asset packaging changes
ARG CACHEBUSTER=20260724201500
RUN echo "Invalidating build cache: ${CACHEBUSTER}"

# Copy remaining source code
COPY host/ host/

# Re-restore after full source copy so Linux restore graph matches final csproj props
# (avoids stale --no-restore when only .cs files changed but package needs differ).
RUN dotnet restore host/PageToMovie.Api/PageToMovie.Api.csproj

# Publish Api host; Web static web assets (including _framework/blazor.web.js) flow in
# via ProjectReference. Fail the image build if framework JS is missing — blank UI on Railway.
RUN dotnet publish host/PageToMovie.Api/PageToMovie.Api.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false \
    && test -f /app/publish/wwwroot/_framework/blazor.web.js \
    && test -f /app/publish/PageToMovie.Api.staticwebassets.endpoints.json \
    || (echo "ERROR: blazor.web.js or staticwebassets endpoints missing from publish — check RequiresAspNetWebAssets on PageToMovie.Web" \
        && ls -la /app/publish 2>/dev/null; ls -laR /app/publish/wwwroot 2>/dev/null; exit 1)

# Stage 2: Runtime
# mcr.microsoft.com/dotnet/aspnet:10.0 is Ubuntu 24.04 (noble), not Debian —
# use Ubuntu package names (e.g. libjpeg-turbo8, not libjpeg62-turbo).
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# ffmpeg for remux; fontconfig/freetype/png/jpeg for SkiaSharp + PDFtoImage (Pdfium)
# page renders used by picture-book OCR (Buster etc.). Missing these → silent 0 page images
# → "No page images for vision" / failed book import on Railway.
RUN apt-get update && apt-get install -y --no-install-recommends \
    ffmpeg \
    fonts-dejavu-core \
    fontconfig \
    libfontconfig1 \
    libfreetype6 \
    libpng16-16 \
    libjpeg-turbo8 \
    libharfbuzz0b \
    libexpat1 \
    ca-certificates \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Environment defaults (set JWT / API secrets via Railway Variables — not baked into the image)
ENV ASPNETCORE_URLS="http://0.0.0.0:5088"
ENV ASPNETCORE_HTTP_PORTS=5088
ENV PORT=5088
ENV PageToMovie__WorkspaceRoot="/data"

# Persistent storage path for projects/DB/keys. On Railway, mount a Volume at /data
# (Dockerfile VOLUME is not supported — configure the mount in Railway, not here).
RUN mkdir -p /data

EXPOSE 5088

ENTRYPOINT ["dotnet", "PageToMovie.Api.dll"]
