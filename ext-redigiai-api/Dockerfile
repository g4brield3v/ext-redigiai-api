# ============================================================
# 1. BASE
# ============================================================

# Define a imagem base utilizada para executar a aplicação.
# ASP.NET Runtime 10.0 sobre Alpine Linux.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS base

# Define o usuário que executará a aplicação dentro do container.
USER $APP_UID

# Define o diretório de trabalho dentro do container.
WORKDIR /app

# Documenta que a aplicação utiliza a porta HTTP 8080.
EXPOSE 8080

# Documenta que a aplicação utiliza a porta HTTPS 8081.
EXPOSE 8081


# ============================================================
# 2. BUILD
# ============================================================

# Define a imagem do SDK .NET utilizada para compilar a aplicação.
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build

# Define a configuração de compilação.
# Caso BUILD_CONFIGURATION não seja informado, utiliza "Release".
ARG BUILD_CONFIGURATION=Release

# Define o diretório de trabalho utilizado durante o build.
WORKDIR /src

# Copia apenas o arquivo .csproj para dentro do container.
COPY ["ext-redigiai-api/ext-redigiai-api.csproj", "ext-redigiai-api/"]

# Restaura as dependências NuGet especificadas no .csproj.
RUN dotnet restore "./ext-redigiai-api/ext-redigiai-api.csproj"

# Copia o restante dos arquivos do projeto para dentro do container.
COPY . .

# Define o diretório onde está o projeto.
WORKDIR "/src/ext-redigiai-api"

# Compila o projeto na configuração especificada.
# Os arquivos compilados serão colocados em /app/build.
RUN dotnet build "./ext-redigiai-api.csproj" \
    -c $BUILD_CONFIGURATION \
    -o /app/build


# ============================================================
# 3. PUBLISH
# ============================================================

# Cria uma nova etapa utilizando a imagem resultante da etapa "build".
FROM build AS publish

# Define a configuração de publicação.
# Caso não seja informada, utiliza "Release".
ARG BUILD_CONFIGURATION=Release

# Publica a aplicação .NET.
# Os arquivos publicados serão colocados em /app/publish.
# UseAppHost=false evita a criação de um executável nativo da aplicação.
RUN dotnet publish "./ext-redigiai-api.csproj" \
    -c $BUILD_CONFIGURATION \
    -o /app/publish \
    /p:UseAppHost=false


# ============================================================
# 4. FINAL
# ============================================================

# Utiliza a imagem "base" como base da imagem final.
FROM base AS final

# Define o diretório de trabalho da aplicação.
WORKDIR /app

# Copia os arquivos publicados da etapa "publish"
# para o diretório /app da imagem final.
COPY --from=publish /app/publish .

# Define o processo principal executado quando o container iniciar.
ENTRYPOINT ["dotnet", "ext-redigiai-api.dll"]