# Stage 1: Build & Publish
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files and restore dependencies
COPY host/PageToMovie.Core/PageToMovie.Core.csproj host/PageToMovie.Core/
COPY host/PageToMovie.Engine/PageToMovie.Engine.csproj host/PageToMovie.Engine/
COPY host/PageToMovie.Fakes/PageToMovie.Fakes.csproj host/PageToMovie.Fakes/
COPY host/PageToMovie.Web/PageToMovie.Web.csproj host/PageToMovie.Web/
COPY host/PageToMovie.Api/PageToMovie.Api.csproj host/PageToMovie.Api/
RUN dotnet restore host/PageToMovie.Api/PageToMovie.Api.csproj

# Force Railway Docker cache invalidation for new deployment
ARG CACHEBUSTER=20260724183000
RUN echo "Invalidating build cache: ${CACHEBUSTER}"

# Copy remaining source code
COPY host/ host/

# Publish Api (which automatically bundles PageToMovie.Web static assets into /app/publish/wwwroot).
# RequiresAspNetWebAssets is set on the csproj so blazor.web.js is included (Linux/Docker often
# skips Microsoft.AspNetCore.App.Internal.Assets unless that property is true).
RUN dotnet publish host/PageToMovie.Api/PageToMovie.Api.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false \
    && test -f /app/publish/wwwroot/_framework/blazor.web.js \
    || (echo "ERROR: blazor.web.js missing from publish output — framework static assets not packaged" && ls -laR /app/publish/wwwroot 2>/dev/null; exit 1)

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Install ffmpeg and font dependencies for Linux container
RUN apt-get update && apt-get install -y --no-install-recommends \
    ffmpeg \
    fonts-dejavu-core \
    fontconfig \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Environment defaults
ENV ASPNETCORE_URLS="http://0.0.0.0:5088"
ENV ASPNETCORE_HTTP_PORTS=5088
ENV PORT=5088
ENV PageToMovie_JWT_KEY="PageToMovie-Production-Docker-Secret-Key-64Chars-Long-1234567890!!"
ENV PAGETOMOVIE_JWT_KEY="PageToMovie-Production-Docker-Secret-Key-64Chars-Long-1234567890!!"
ENV PageToMovie__Auth__JwtSigningKey="PageToMovie-Production-Docker-Secret-Key-64Chars-Long-1234567890!!"
ENV PageToMovie__WorkspaceRoot="/data"

# Create persistent storage volume directory
RUN mkdir -p /data
VOLUME ["/data"]

EXPOSE 5088

ENTRYPOINT ["dotnet", "PageToMovie.Api.dll"]
