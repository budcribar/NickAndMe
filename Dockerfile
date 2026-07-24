# Stage 1: Build & Publish
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files and restore dependencies
COPY host/PageToMovie.Core/PageToMovie.Core.csproj host/PageToMovie.Core/
COPY host/PageToMovie.Engine/PageToMovie.Engine.csproj host/PageToMovie.Engine/
COPY host/PageToMovie.Fakes/PageToMovie.Fakes.csproj host/PageToMovie.Fakes/
COPY host/PageToMovie.Api/PageToMovie.Api.csproj host/PageToMovie.Api/
RUN dotnet restore host/PageToMovie.Api/PageToMovie.Api.csproj

# Copy remaining source code
COPY host/ host/
WORKDIR /src/host/PageToMovie.Api
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

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
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "PageToMovie.Api.dll"]
