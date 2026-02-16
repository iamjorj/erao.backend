# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy solution and project files
COPY Erao.sln .
COPY src/Erao.Core/Erao.Core.csproj src/Erao.Core/
COPY src/Erao.Infrastructure/Erao.Infrastructure.csproj src/Erao.Infrastructure/
COPY src/Erao.Application/Erao.Application.csproj src/Erao.Application/
COPY src/Erao.API/Erao.API.csproj src/Erao.API/

# Restore dependencies
RUN dotnet restore

# Copy all source code
COPY . .

# Build the application
RUN dotnet build -c Release --no-restore

# Publish the application
RUN dotnet publish src/Erao.API/Erao.API.csproj -c Release -o /app/publish --no-build

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Create Parquet data directory and non-root user for security
RUN mkdir -p /app/data/parquet && \
    adduser --disabled-password --gecos "" appuser && \
    chown -R appuser /app
USER appuser

# Copy published application
COPY --from=build /app/publish .

# Railway injects PORT env var - default to 8080 for local
ENV PORT=8080
EXPOSE ${PORT}

# .NET container optimizations
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
ENV ASPNETCORE_ENVIRONMENT=Production

# Use PORT env var so Railway can control the port
ENTRYPOINT ["sh", "-c", "dotnet Erao.API.dll --urls http://+:${PORT}"]
