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

# Force Railway Docker cache invalidation
ARG CACHEBUSTER=20260724120100
RUN echo "Invalidating build cache: ${CACHEBUSTER}"

# Copy remaining source code
COPY host/ host/
WORKDIR /src/host/PageToMovie.Api
RUN dotnet publish -c Release --no-restore -o /app/publish /p:UseAppHost=false

WORKDIR /src
RUN dotnet publish host/PageToMovie.Web/PageToMovie.Web.csproj -c Release --no-restore -o /app/web_publish /p:UseAppHost=false
RUN cp -r /app/web_publish/wwwroot/* /app/publish/wwwroot/

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
EXPOSE 5088

ENTRYPOINT ["dotnet", "PageToMovie.Api.dll"]
