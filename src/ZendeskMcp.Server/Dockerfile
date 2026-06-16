# Multi-stage build for the Zendesk MCP server.
# Build context is the repository root: docker build -f src/ZendeskMcp.Server/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/ZendeskMcp.Server/ZendeskMcp.Server.csproj", "src/ZendeskMcp.Server/"]
RUN dotnet restore "src/ZendeskMcp.Server/ZendeskMcp.Server.csproj"
COPY . .
RUN dotnet publish "src/ZendeskMcp.Server/ZendeskMcp.Server.csproj" \
    -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Run as the non-root user that ships with the .NET runtime images.
USER $APP_UID

# Default transport is stdio, so `docker run --rm -i` behaves like the original
# Python server. For hosted HTTP, run with: -e TRANSPORT=http -p 8080:8080
ENV TRANSPORT=stdio
EXPOSE 8080

ENTRYPOINT ["dotnet", "ZendeskMcp.Server.dll"]
