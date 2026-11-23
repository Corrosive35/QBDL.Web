# Stage 1: Build
# Le SDK tourne sur l'architecture de build ($BUILDPLATFORM) et compile pour la cible
# ($TARGETARCH) : sans cela, l'image arm64 construite sur un runner x86 ferait tourner tout le
# SDK sous émulation QEMU, lent et non pris en charge par Microsoft.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
ARG TARGETARCH
WORKDIR /src
COPY QBDL.Web.csproj .
RUN dotnet restore -a $TARGETARCH
COPY . .
# Version affichée (barre latérale, /healthz). Vide par défaut : l'image annonce <Version> du
# csproj. Seul le workflow de dev la renseigne. Déclarée après la restauration, pour que son
# changement à chaque commit n'invalide pas le cache du « dotnet restore ».
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
# su-exec : le point d'entrée démarre en root le temps de réparer les droits des volumes, puis
#   bascule sur l'UID:GID de l'application (voir docker-entrypoint.sh).
RUN apk add --no-cache flac icu-libs icu-data-full su-exec

# Compte de l'image, UID:GID 1000:1000 : ceux de l'application quand PUID et PGID sont absents.
# Son nom ne figure qu'ici et dans APP_USER (docker-entrypoint.sh) ; ailleurs, les droits se
# règlent sur les numéros.
RUN addgroup -g 1000 qbdlx && adduser -u 1000 -G qbdlx -D qbdlx

RUN mkdir -p /downloads /config && chown -R 1000:1000 /downloads /config

COPY --from=build --chown=1000:1000 /app .

ENV ASPNETCORE_URLS=http://+:8080
ENV CONFIG_PATH=/config
# L'image de base positionne cette variable à true ; ICU étant installé, on la désactive.
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
EXPOSE 8080

VOLUME ["/downloads", "/config"]

# Pas de USER : le point d'entrée démarre en root pour corriger les droits d'un volume que Docker
# a créé en root sur l'hôte, puis lance l'application sous 1000:1000, ou sous PUID:PGID s'ils
# sont fournis (su-exec). L'application elle-même ne tourne jamais en root ; un conteneur lancé
# avec --user démarre directement.
# Le sed retire d'éventuels retours chariot Windows, qui rendraient le script inexécutable.
COPY docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN sed -i 's/\r$//' /usr/local/bin/docker-entrypoint.sh && chmod 755 /usr/local/bin/docker-entrypoint.sh

ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
CMD ["dotnet", "QBDL.Web.dll"]
