# ── BUILD ─────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY *.csproj .
RUN dotnet restore --verbosity quiet

COPY . .
RUN dotnet publish -c Release -o /app/publish --no-restore

# ── RUNTIME ───────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

# Criar pastas necessárias
RUN mkdir -p wwwroot/uploads wwwroot/brand

ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_RUNNING_IN_CONTAINER=true

# Railway injeta $PORT — shell form expande a variável
CMD dotnet AgenciaOS.dll --urls "http://0.0.0.0:$PORT"
