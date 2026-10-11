# Uma imagem, dois serviços: `api` (padrão) e `worker`, escolhidos pelo argumento.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/RedigiAI.Domain/RedigiAI.Domain.csproj src/RedigiAI.Domain/
COPY src/RedigiAI.Application/RedigiAI.Application.csproj src/RedigiAI.Application/
COPY src/RedigiAI.Infrastructure/RedigiAI.Infrastructure.csproj src/RedigiAI.Infrastructure/
COPY src/RedigiAI.Api/RedigiAI.Api.csproj src/RedigiAI.Api/
RUN dotnet restore src/RedigiAI.Api/RedigiAI.Api.csproj

COPY src/ src/
RUN dotnet publish src/RedigiAI.Api/RedigiAI.Api.csproj -c Release -o /app --no-restore \
    -p:OpenApiGenerateDocumentsOnBuild=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID

ENTRYPOINT ["dotnet", "RedigiAI.Api.dll"]
CMD ["api"]
