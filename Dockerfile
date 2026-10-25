FROM --platform=$BUILDPLATFORM node:24-slim AS web
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
COPY build/allowed-licenses.json /src/build/allowed-licenses.json
RUN npm run build

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS api
ARG TARGETARCH
ARG VERSION=0.0.0
WORKDIR /src
COPY global.json Directory.Build.props dotnet-tools.json ./
COPY build/ build/
COPY src/Wishlistarr.Core/Wishlistarr.Core.csproj src/Wishlistarr.Core/
COPY src/Wishlistarr.Data/Wishlistarr.Data.csproj src/Wishlistarr.Data/
COPY src/Wishlistarr.Integrations/Wishlistarr.Integrations.csproj src/Wishlistarr.Integrations/
COPY src/Wishlistarr.Api/Wishlistarr.Api.csproj src/Wishlistarr.Api/
RUN dotnet restore src/Wishlistarr.Api/Wishlistarr.Api.csproj -a $TARGETARCH
COPY src/ src/
COPY --from=web /src/src/Wishlistarr.Api/wwwroot src/Wishlistarr.Api/wwwroot
RUN dotnet publish src/Wishlistarr.Api/Wishlistarr.Api.csproj \
    -c Release -a $TARGETARCH --no-restore -p:Version=$VERSION -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl tzdata \
    && rm -rf /var/lib/apt/lists/* \
    && (userdel -r ubuntu 2>/dev/null || true) \
    && (groupdel ubuntu 2>/dev/null || true) \
    && mkdir /data
WORKDIR /app
COPY --from=api /app ./
COPY docker/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod 0755 /usr/local/bin/entrypoint.sh

ENV ASPNETCORE_HTTP_PORTS=8080 \
    WISHLISTARR_DATA_DIR=/data \
    PUID=1000 \
    PGID=1000 \
    TZ=UTC

VOLUME /data
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://127.0.0.1:8080/api/health || exit 1
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
CMD ["dotnet", "/app/Wishlistarr.Api.dll"]
