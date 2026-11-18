# Stage 1: Build
# Le SDK tourne sur l'architecture de la machine de build ($BUILDPLATFORM) et compile pour
# l'architecture visée ($TARGETARCH : amd64 ou arm64), deux variables que BuildKit renseigne seul.
# Sans cela, l'image arm64 construite sur un runner x86 ferait tourner tout le SDK .NET sous
# émulation QEMU : lent, et non pris en charge par Microsoft.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
ARG TARGETARCH
WORKDIR /src
COPY QBDL.Web.csproj .
RUN dotnet restore -a $TARGETARCH
COPY . .
# Version affichée (barre latérale, /healthz). Vide par défaut : l'image annonce alors <Version>
# du csproj, comme une release. Seul le workflow de la branche dev la renseigne
# (« 1.8.0-dev+<commit> »). Déclarée ici, après la restauration, pour que son changement à chaque
# commit n'invalide pas le cache du « dotnet restore ».
ARG APP_VERSION=
RUN dotnet publish -c Release -a $TARGETARCH -o /app --no-restore \
    ${APP_VERSION:+"-p:InformationalVersion=$APP_VERSION"}

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS runtime
WORKDIR /app

# flac : correction du MD5 des fichiers FLAC
# icu-libs + icu-data-full : l'image Alpine tourne en globalization-invariant mode par défaut,
#   sans quoi aucune culture nommée (fr-FR, en-US) n'existe — formats de nombres et tri
#   alphabétique tenant compte des accents.
RUN apk add --no-cache flac icu-libs icu-data-full

# Create non-root user
RUN addgroup -g 1000 qbdlx && adduser -u 1000 -G qbdlx -D qbdlx

# Create volumes and set ownership
RUN mkdir -p /downloads /config && chown -R qbdlx:qbdlx /downloads /config

COPY --from=build --chown=qbdlx:qbdlx /app .

ENV ASPNETCORE_URLS=http://+:8080
ENV CONFIG_PATH=/config
# L'image de base positionne cette variable à true ; ICU étant installé, on la désactive.
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
EXPOSE 8080

VOLUME ["/downloads", "/config"]

USER qbdlx
ENTRYPOINT ["dotnet", "QBDL.Web.dll"]
