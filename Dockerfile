FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

ENV DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    NUGET_XMLDOC_MODE=skip

COPY global.json ./
COPY RefactorMCP.ConsoleApp/RefactorMCP.ConsoleApp.csproj RefactorMCP.ConsoleApp/

RUN dotnet restore RefactorMCP.ConsoleApp/RefactorMCP.ConsoleApp.csproj

COPY RefactorMCP.ConsoleApp/ RefactorMCP.ConsoleApp/

RUN dotnet publish RefactorMCP.ConsoleApp/RefactorMCP.ConsoleApp.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS runtime

WORKDIR /app

ENV DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    REFACTOR_MCP_DEBUG_SYMBOLS=1

COPY --from=build /app/publish/ ./

ENTRYPOINT ["dotnet", "RefactorMCP.ConsoleApp.dll"]
