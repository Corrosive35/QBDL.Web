#!/bin/sh
# Point d'entrée de l'image QBDL.Web.
#
# Démarre en root parce que Docker crée en root un dossier d'hôte absent monté sur /config ou
# /downloads, où qbdlx (UID 1000) ne pourrait rien écrire. Le script répare ces droits, puis rend
# la main à l'application sous qbdlx : elle ne tourne jamais en root. Il ne change rien sur une
# installation en ordre : seul ce qui n'appartient pas à 1000 ET n'est pas inscriptible par
# qbdlx est repris.
set -eu

APP_USER=qbdlx
APP_UID=1000
APP_GID=1000
CONFIG_DIR="${CONFIG_PATH:-/config}"
DOWNLOADS_DIR=/downloads

# Lancé directement en non-root (docker run --user …) : pas le droit de corriger quoi que ce
# soit, et rien à basculer. L'application démarre telle quelle.
if [ "$(id -u)" != "0" ]; then
    exec "$@"
fi

writable_by_app() {
    su-exec "$APP_USER" sh -c 'test -w "$1"' sh "$1"
}

# Premier élément de /config (dossier compris) dans lequel qbdlx ne peut pas écrire, ou rien.
# Seuls les éléments qui n'appartiennent pas à 1000 sont testés : sur une installation saine,
# find ne renvoie rien et le test est immédiat.
first_unwritable_config() {
    find "$CONFIG_DIR" ! -user "$APP_UID" 2>/dev/null | while IFS= read -r path; do
        if ! writable_by_app "$path"; then
            printf '%s\n' "$path"
            break
        fi
    done
}

# /config : petit (réglages, comptes, trousseau), repris récursivement s'il le faut.
if [ -d "$CONFIG_DIR" ] && [ -n "$(first_unwritable_config)" ]; then
    echo "QBDL.Web : droits de $CONFIG_DIR corrigés pour $APP_UID:$APP_GID."
    chown -R "$APP_UID:$APP_GID" "$CONFIG_DIR" 2>/dev/null || true
fi

# /downloads : le dossier lui-même seulement, jamais son contenu. La bibliothèque peut être
# énorme, et ses fichiers comme ses sous-dossiers existants appartiennent à l'utilisateur.
if [ -d "$DOWNLOADS_DIR" ] && [ "$(stat -c %u "$DOWNLOADS_DIR")" != "$APP_UID" ] \
        && ! writable_by_app "$DOWNLOADS_DIR"; then
    echo "QBDL.Web : propriétaire de $DOWNLOADS_DIR corrigé pour $APP_UID:$APP_GID (contenu inchangé)."
    chown "$APP_UID:$APP_GID" "$DOWNLOADS_DIR" 2>/dev/null || true
fi

# chown peut être refusé (partage réseau en root squash, montage en lecture seule…). Sans
# /config inscriptible l'application ne peut pas démarrer : un message clair plutôt qu'une pile
# .NET répétée à chaque redémarrage.
blocked="$(first_unwritable_config)"
if [ -n "$blocked" ]; then
    cat >&2 <<EOF

QBDL.Web : impossible d'écrire dans $blocked.
Le volume monté sur $CONFIG_DIR doit appartenir à $APP_UID:$APP_GID (utilisateur qbdlx), et le
conteneur n'a pas pu le corriger lui-même. Sur l'hôte, lancez :

    chown -R $APP_UID:$APP_GID <dossier monté sur $CONFIG_DIR>

puis redémarrez le conteneur.

QBDL.Web: cannot write to $blocked. The volume mounted on $CONFIG_DIR must be owned by
$APP_UID:$APP_GID; run "chown -R $APP_UID:$APP_GID <host folder>" on the host, then restart.

EOF
    exit 1
fi

if [ -d "$DOWNLOADS_DIR" ] && ! writable_by_app "$DOWNLOADS_DIR"; then
    echo "QBDL.Web : attention, $DOWNLOADS_DIR n'est pas inscriptible par $APP_UID:$APP_GID ; les téléchargements échoueront. Sur l'hôte : chown $APP_UID:$APP_GID <dossier monté sur $DOWNLOADS_DIR>" >&2
fi

# HOME de qbdlx : su-exec garde l'environnement de root, dont HOME=/root, illisible pour qbdlx.
export HOME=/home/$APP_USER
exec su-exec "$APP_USER" "$@"
