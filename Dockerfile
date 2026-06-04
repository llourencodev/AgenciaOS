# ── BUILD ─────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY *.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app/publish

# ── RUNTIME ───────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production

# Railway injeta $PORT dinamicamente — usamos shell form no CMD para expandir a variável
CMD dotnet AgenciaOS.dll --urls "http://0.0.0.0:$PORT"
