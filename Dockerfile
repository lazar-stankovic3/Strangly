# ============================================================
# 1. BUILD STAGE
# ============================================================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Kopiraj samo solution + csproj prvo (bolji layer cache)
# restore se ne ponavlja dok se dependencies ne promijene
COPY OmegleCloneMVC.sln ./
COPY OmegleCloneMVC/OmegleCloneMVC.csproj OmegleCloneMVC/
RUN dotnet restore OmegleCloneMVC/OmegleCloneMVC.csproj

# Sada kopiraj ostatak koda i publishuj
COPY OmegleCloneMVC/ OmegleCloneMVC/
WORKDIR /src/OmegleCloneMVC
RUN dotnet publish -c Release -o /app/out --no-restore

# ============================================================
# 2. RUNTIME STAGE
# ============================================================
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Non-root user (security)
RUN adduser --disabled-password --gecos "" appuser && chown -R appuser /app
USER appuser

COPY --from=build /app/out ./

EXPOSE 10000

# Shell forma CMD-a je potrebna da bi se $PORT expandovao u runtime-u.
# Render postavlja PORT=10000; fallback je 10000 ako PORT nije setovan.
CMD ASPNETCORE_URLS=http://0.0.0.0:${PORT:-10000} dotnet OmegleCloneMVC.dll
