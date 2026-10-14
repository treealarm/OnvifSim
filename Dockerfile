# syntax=docker/dockerfile:1
# OnvifSim: the editor built with Node, the server published with the .NET SDK (both on the build
# machine's own architecture, cross-targeting the image's), then the ASP.NET Core runtime (Ubuntu
# 24.04) plus the distribution's ffmpeg for the H.264 streams.

FROM --platform=$BUILDPLATFORM node:22-slim AS editor
WORKDIR /src/src/OnvifSim.Web
COPY src/OnvifSim.Web/package.json src/OnvifSim.Web/package-lock.json ./
RUN npm ci
COPY src/OnvifSim.Web/ ./
RUN npm run build

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS server
ARG TARGETARCH
ARG VERSION=0.0.0-dev
WORKDIR /src
COPY Directory.Build.props ./
COPY src/OnvifSim.Rtsp/OnvifSim.Rtsp.csproj src/OnvifSim.Rtsp/
COPY src/OnvifSim.Server/OnvifSim.Server.csproj src/OnvifSim.Server/
RUN dotnet restore src/OnvifSim.Server -a $TARGETARCH
COPY src/OnvifSim.Rtsp/ src/OnvifSim.Rtsp/
COPY src/OnvifSim.Server/ src/OnvifSim.Server/
COPY --from=editor /src/src/OnvifSim.Web/dist src/OnvifSim.Web/dist/
RUN dotnet publish src/OnvifSim.Server -c Release -a $TARGETARCH --no-restore --self-contained false \
    -p:BuildEditor=false -p:Version=$VERSION -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir /data && chown app:app /data
WORKDIR /app
COPY --from=server /app ./
LABEL org.opencontainers.image.source="https://github.com/treealarm/OnvifSim" \
      org.opencontainers.image.description="A virtual 3D world whose cameras are ONVIF devices" \
      org.opencontainers.image.licenses="MIT"
# The base image's ASPNETCORE_HTTP_PORTS would only be overridden by --port; clearing it keeps Kestrel
# from warning about that on every start.
ENV ASPNETCORE_HTTP_PORTS= \
    ONVIFSIM_DATA=/data
VOLUME /data
# Editor and API, ONVIF (one port per camera, from 8001), RTSP, RTP/RTCP, WS-Discovery.
EXPOSE 8080 8001-8016 8554 6970-6971/udp 3702/udp
USER app
ENTRYPOINT ["dotnet", "OnvifSim.dll"]
