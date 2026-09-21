ARG SYSTEM_VERSION="10.0-alpine" # ARG: Variável interpretada apenas no build da imagem
ENV PORT=8080 # ENV: Variável interpretada no build e inicialização do container

FROM mcr.microsoft.com/dotnet/sdk:${SYSTEM_VERSION} AS build
WORKDIR /src
COPY ["ext-redigiai-api/ext-redigiai-api.csproj", "ext-redigiai-api/"]
RUN dotnet restore "ext-redigiai-api/ext-redigiai-api.csproj"
COPY . .
RUN dotnet publish "ext-redigiai-api/ext-redigiai-api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${SYSTEM_VERSION}
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080

ENTRYPOINT ["dotnet", "ext-redigiai-api.dll"]