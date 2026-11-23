#!/bin/sh
# Point d'entrée de l'image QBDL.Web.
#
# Démarre en root parce que Docker crée en root un dossier d'hôte absent monté sur /config ou
# /downloads, où l'application ne pourrait rien écrire. Le script répare ces droits, puis rend
# la main à l'application sous l'UID:GID choisi : elle ne tourne jamais en root. Il ne change
# rien sur une installation en ordre : seul ce qui n'appartient pas à cet UID ET n'est pas
# inscriptible par lui est repris.
#
# UID:GID : 1000:1000 par défaut, ou PUID et PGID (99 et 100 sous Unraid, dont les partages
# appartiennent à nobody:users). Les droits se règlent sur ces seuls numéros : le nom du compte
# de l'image (Dockerfile) ne sert ici qu'à trouver son dossier HOME.
set -eu

APP_USER=qbdl
APP_HOME=/home/$APP_USER
CONFIG_DIR="${CONFIG_PATH:-/config}"
DOWNLOADS_DIR=/downloads

# Lancé directement en non-root (docker run --user …) : pas le droit de corriger quoi que ce
# soit, et rien à basculer. L'application démarre telle quelle ; PUID et PGID sont sans effet.
if [ "$(id -u)" != "0" ]; then
    if [ -n "${PUID:-}${PGID:-}" ]; then
        echo "QBDL.Web : PUID/PGID ignorés, le conteneur est lancé avec --user ($(id -u):$(id -g))." >&2
    fi
    exec "$@"
fi

# Une variable vide compte comme absente : un gabarit Unraid transmet aussi les champs vides.
APP_UID="${PUID:-1000}"
APP_GID="${PGID:-1000}"

# Nombre entier positif, sans zéro en tête. 0 est refusé plutôt que remplacé : l'application ne
# tourne jamais en root, et un repli silencieux sur 1000 donnerait à la bibliothèque un
# propriétaire que personne n'a choisi.
for setting in "PUID=$APP_UID" "PGID=$APP_GID"; do
    case "${setting#*=}" in
        0*|*[!0-9]*)
            cat >&2 <<EOF

QBDL.Web : $setting refusé. Attendu : un nombre entier positif sans zéro en tête, par
exemple PUID=99 et PGID=100 sous Unraid. 0 (root) n'est pas accepté.

QBDL.Web: $setting rejected. Expected a positive whole number without leading zeros, for
example PUID=99 and PGID=100 on Unraid. 0 (root) is not allowed.

EOF
            exit 1
            ;;
    esac
done

writable_by_app() {
    su-exec "$APP_UID:$APP_GID" sh -c 'test -w "$1"' sh "$1"
}

# Premier élément de /config (dossier compris) dans lequel l'application ne peut pas écrire, ou
# rien. Seuls les éléments qui n'appartiennent pas à son UID sont testés : sur une installation
# saine, find ne renvoie rien et le test est immédiat.
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
Le volume monté sur $CONFIG_DIR doit appartenir à $APP_UID:$APP_GID, et le conteneur n'a pas
pu le corriger lui-même. Sur l'hôte, lancez :

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

# HOME : su-exec le remplace par le dossier du compte qui porte l'UID, ou par « / » si aucun ne
# le porte (99 sous Alpine). Il est donc fixé après su-exec, par env, sur le dossier du compte de
# l'image ; ce dossier suit l'UID:GID choisi pour rester inscriptible. env passe la main à
# l'application par exec : elle reste le processus 1.
if [ -d "$APP_HOME" ] && [ "$(stat -c %u:%g "$APP_HOME")" != "$APP_UID:$APP_GID" ]; then
    chown -R "$APP_UID:$APP_GID" "$APP_HOME" 2>/dev/null || true
fi
exec su-exec "$APP_UID:$APP_GID" env HOME="$APP_HOME" "$@"
