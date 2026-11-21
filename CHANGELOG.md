# Changelog

All notable changes to QBDL.Web (formerly QobuzDLX.Web) are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.8.0] - 2026-11-22

### Added
- **Image de développement `:dev`.** Chaque push sur la branche `dev` construit, teste puis publie
  `thimble9057/qobuzdlx.web:dev` (amd64 et arm64). Elle ne publie jamais `latest` ni de tag de
  version. Une image dev s'annonce `1.8.0-dev+<commit>` dans la barre latérale et dans `/healthz` ;
  une release continue d'afficher la version seule.
- **Barre d'onglets sur mobile** : Recherche, Téléchargement (avec le nombre d'éléments restants),
  Paramètres, et Utilisateurs pour un administrateur. Elle remplace le menu latéral escamotable et
  se retire pendant la saisie au clavier. Langue, déconnexion et version passent en bas des
  Paramètres.
- **Mini-carte « En cours »** dans la barre latérale du bureau : pochette, titre et progression du
  téléchargement en cours.
- **Police Space Grotesk**, seule police de l'interface (titres et texte), auto-hébergée dans
  `wwwroot/fonts` avec sa licence SIL OFL 1.1 : aucune requête vers un service tiers. Échelle
  resserrée : titres de page de 27 à 34 px, texte courant à 14 px, graisses de 400 à 600.
  Chiffres tabulaires pour les valeurs qui changent en direct (pourcentages, compteurs, débit).

### Changed
- **Nouvelle interface, première étape : les fondations.** Fond nuit gravé de sillons de disque,
  verre dépoli, actions en blanc ; le rose-rouge disparaît. Les pages gardent leur disposition en
  attendant leur refonte, page par page.
- **Barre latérale du bureau redessinée** : navigation avec icônes, puis bloc Session en bas
  (compte, compte Qobuz et abonnement, langue, déconnexion, version).
- **Reconnexion discrète** : un bandeau en haut de page remplace l'écran plein. Mêmes messages, et
  le bouton « Recharger » est disponible dès la première tentative.
- **Recherche en grille.** Les albums s'affichent en grandes pochettes (deux colonnes sur
  téléphone), avec sous chacune titre, artiste, date, pistes, format, et une capsule « choisir |
  Télécharger ». « Dans la file » et « Déjà téléchargé » restent visibles. Pistes et artistes en
  listes denses ; un artiste déplié montre sa discographie en grille, avec « Tout télécharger ».
  La barre de sélection ne masque plus la dernière rangée.
- **Téléchargement.** Carte « En cours » avec la pochette et le disque qui en sort (il tourne
  pendant le téléchargement, sauf si l'appareil demande moins d'animations), grand pourcentage et
  un segment par piste pour les albums et playlists. File avec badges d'état et boutons ronds ;
  Annuler et Passer passent dans la carte « En cours ». Journal à côté de la file sur grand écran.
- **Paramètres** en colonnes (trois au bureau), **Utilisateurs** et écrans de **connexion**
  habillés dans le nouveau style ; les formulaires de connexion ne changent pas.
- **Fond animé par les pages** : les pochettes affichées (résultats, file) tapissent le fond, en
  gris et floutées.
- **Nouvelle icône d'application**, aux couleurs de l'interface : une flèche posée sur les
  sillons, pour l'onglet du navigateur comme pour l'écran d'accueil, aux bords arrondis là où le
  système ne découpe pas l'icône.

### Fixed
- **Plantage en boucle au démarrage quand le dossier de configuration n'existait pas sur
  l'hôte.** Docker le créait au nom de root, et l'application, qui tourne en `qbdlx` (UID 1000),
  ne pouvait pas y écrire. L'image démarre désormais en root le temps de reprendre `/config` et le
  seul dossier `/downloads` (jamais son contenu), puis lance l'application en `qbdlx`. Rien ne
  change quand les droits sont déjà bons, ni quand le conteneur est lancé avec `--user`. Si la
  correction est impossible (partage réseau, par exemple), un message indique la commande
  `chown` à lancer sur l'hôte.
- Dans les Paramètres, cliquer sur le libellé d'une case la coche désormais (il n'y était pas
  relié).
- Les variables de modèle (Paramètres → Modèles) se copient aussi au clavier.
- Focus clavier visible sur tous les boutons ; la bascule FR | EN annonce la langue et son état.
- **Image testée avant publication.** Avant de publier, la construction démarre l'image, vérifie
  que `/healthz` annonce `QBDL.Web` avec la version attendue et que la page d'accueil répond. En
  cas d'échec, elle affiche les journaux du conteneur et ne publie rien.

## [1.7.0] - 2026-11-16

### Added
- **Comptes utilisateurs.** L'instance n'a plus un mot de passe unique mais des comptes, gérés par un
  ou plusieurs administrateurs depuis la nouvelle page **Utilisateurs** : création, renommage, rôle,
  réinitialisation du mot de passe, dossier de téléchargement, suppression. Supprimer un compte ne
  supprime aucun fichier.
  - **Un seul compte Qobuz, partagé.** L'administrateur le connecte ; tous les utilisateurs
    téléchargent avec. Tant qu'il n'est pas connecté, les autres voient un message d'attente.
  - **Un dossier par utilisateur**, fixé par l'administrateur (`/downloads/<identifiant>` par défaut)
    et en lecture seule pour l'utilisateur. Le serveur refuse `/` et tout chemin sous `/config` :
    un modèle de nom ne peut pas aller écraser `users.json`.
  - **Des réglages par utilisateur** : qualité, modèles, étiquetage, pochettes, options avancées.
  - **Une langue par utilisateur**, enregistrée sur le compte — journal de téléchargement compris.
    Avant la connexion, la langue suit le choix fait sur l'écran, sinon le navigateur.
  - **Une file par utilisateur**, que lui seul voit et pilote, avec sa progression et son journal.
- **Téléchargements en parallèle.** Chaque utilisateur télécharge en même temps que les autres, sans
  attendre son tour. Au sein d'une même file, les éléments restent traités un par un, dans l'ordre.
- **Changement de son propre mot de passe**, dans Paramètres → *Mon compte*. Les autres appareils de
  ce compte sont déconnectés ; celui qui fait le changement reste connecté.
- **Écran « Créer le compte administrateur »** à la première visite d'une instance sans compte.

### Changed
- **Le projet devient QBDL.Web.** Le nom affiché passe à « QBDL » (titre, logo, écran d'accueil
  du téléphone), le projet et ses espaces de noms à `QBDL.Web`, et `/healthz` répond
  `"app": "QBDL.Web"` — QobuzDL.Remote accepte les deux noms. Rien ne change côté Docker : même
  image, même conteneur, même volume `/config`. Les identifiants persistants restent eux aussi
  inchangés (cookie `qbdlx_access`, `window.qbdlxResume()`, utilisateur `qbdlx`, dossier
  `qbdlx-temp`, fichiers de `/config`). Le nom d'application Data Protection change avec le projet :
  sans effet supplémentaire, la reconnexion par appareil étant déjà imposée par cette version.
- **Échecs de connexion Qobuz explicites.** L'échec de la récupération automatique des
  identifiants d'application n'est plus avalé avant un repli silencieux : il s'affiche, et il est
  journalisé en *warning*, visible dans `docker logs` — sans jeton, secret ni mot de passe. Un jeton
  refusé par Qobuz a son propre message : invalide, expiré ou émis pour une autre application, la
  réponse ne permettant pas de les distinguer, avec les trois façons d'en sortir (e-mail, jeton
  copié depuis play.qobuz.com, identifiants personnalisés correspondants).
- **Migration automatique depuis la 1.6.** Avec un mot de passe d'accès, il devient celui d'un compte
  administrateur nommé **`admin`**, qui reprend les réglages, le dossier et la langue de l'instance.
  Sans mot de passe, ces réglages attendent le premier administrateur créé, qui en hérite. Une copie
  de l'ancienne configuration est conservée : `settings.json.pre-1.7.0.bak`.
- **Une reconnexion par appareil** après la mise à jour : les cookies de la 1.6 ne désignent aucun
  compte et sont refusés.
- **« Déconnexion » déconnecte de l'application, et d'elle seule.** Elle vidait jusqu'ici le compte
  Qobuz — ce qui couperait désormais tout le monde. La déconnexion du compte Qobuz est passée dans
  Paramètres → *Compte Qobuz*, réservée aux administrateurs et confirmée par un second clic.
- **La bascule FR | EN recharge la page.** La langue est fixée à chaque requête ; ce rechargement ne
  redemande pas le mot de passe, la connexion vivant dans un cookie depuis la 1.6.0.
- **La recherche n'est plus partagée** : chacun retrouve la sienne d'une page à l'autre.

### Removed
- La carte **« Mot de passe d'accès »** des Paramètres, et avec elle le mode sans mot de passe :
  l'accès passe toujours par un compte.
- **Les identifiants d'application intégrés au code.** Les couples `app_id` / `app_secret` de repli
  disparaissent : la connexion repose sur la récupération automatique depuis le lecteur web Qobuz,
  ou sur les identifiants personnalisés. Conséquence : un jeton émis pour une application absente
  du lecteur web — le cas du quatrième couple ajouté en 1.5.1 — ne se connecte plus qu'avec les
  identifiants personnalisés de cette application. Une session déjà établie n'est pas touchée :
  elle réutilise les identifiants enregistrés dans `settings.json`, qui pré-remplissent aussi les
  champs personnalisés.
- **La lecture directe du secret dans le lecteur web**, dernier recours adopté sans validation : le
  secret qu'elle renvoyait est refusé par Qobuz, et il figure déjà parmi les candidats testés.

### Fixed
- **Une connexion Qobuz échouée ne laisse plus l'application à moitié connectée.** Un jeton refusé,
  ou un jeton obtenu sans que le secret d'application soit trouvé, restait en mémoire : au
  rechargement suivant, l'application paraissait connectée pour tous les utilisateurs, mais
  recherches et téléchargements échouaient. Après un échec, les derniers identifiants valides
  enregistrés sont rétablis — aucun sur une installation neuve.
- **Un mauvais mot de passe Qobuz ne s'affiche plus comme une panne réseau.** Avec les identifiants
  personnalisés, tout refus HTTP de Qobuz donnait « Impossible de joindre les serveurs Qobuz ».

### Security
- **Champ App Secret masqué** dans les identifiants personnalisés. Il est pré-rempli depuis
  `settings.json` : seul l'affichage est masqué, la valeur reste dans la page de l'administrateur.
- **Les fragments du secret ne sont plus journalisés.** Une trace de débogage écrivait les trois
  morceaux qui, mis bout à bout, redonnent le secret d'application.
- **La langue ne s'écrit plus dans la configuration partagée sans être connecté.** Le sélecteur de
  l'écran de verrouillage modifiait `settings.json` ; il ne pose plus qu'un cookie sur le navigateur
  du visiteur.
- **Un identifiant inexistant répond aussi lentement qu'un mauvais mot de passe** : la durée de la
  réponse ne permet pas de dresser la liste des comptes.
- **`users.json` est écrit de façon atomique**, et un fichier illisible bloque le démarrage plutôt
  que de rouvrir l'écran de création d'administrateur au premier venu.

## [1.6.0] - 2026-10-11

### Added
- **Section « Compte » dans les Paramètres.** L'abonnement Qobuz y est affiché tel que le serveur
  le voit : offre, date d'expiration, droits de streaming (Hi-Res, sans perte, avec perte) et pays
  du compte. L'information est lue à la connexion, y compris **par jeton** — l'appel `user/get`
  répond de la même façon dans les deux cas.
  - Un bouton **« Actualiser »** relit la fiche sans passer par une déconnexion. C'est ce qu'il faut
    cliquer après un renouvellement : il remet aussi la file d'attente en marche.
- **Pastille dans la barre latérale** quand l'abonnement manque (« Sans abonnement ») ou qu'il a
  expiré (« Expiré »). Elle mène d'un clic à la section « Compte ». Jusqu'ici, un compte sans
  abonnement n'affichait *rien* à cet endroit : le libellé d'offre étant vide, la barre latérale
  paraissait normale.
- **Relecture de l'état du compte au démarrage.** Le jeton enregistré était réutilisé sans aucune
  vérification : un abonnement arrivé à échéance restait invisible jusqu'au premier téléchargement.
  L'appel est borné à 10 secondes et **ne conditionne pas l'accès** — si Qobuz est injoignable,
  l'application démarre avec le dernier état connu.

### Fixed
- **Un compte sans abonnement remplissait le journal de `FLAC stream not found at starting
  position`**, une ligne par piste, sans jamais nommer la cause. Qobuz ne renvoie pas d'erreur dans
  ce cas : l'appel aboutit avec un **extrait de 30 secondes en MP3**, que l'application téléchargeait
  dans un fichier `.flac` avant de le confier à l'étiqueteur, qui n'y trouvait évidemment aucun flux
  FLAC. L'extrait est désormais reconnu — explicitement (`sample`) ou par un type MIME qui ne
  correspond pas à la qualité demandée — et l'élément échoue sur **« Abonnement requis ou expiré »**.
  - La **file se bloque alors au premier élément** au lieu de dérouler toute la liste pour le même
    résultat. Les éléments suivants restent *en attente*, prêts à repartir : il suffit d'actualiser
    le compte depuis les Paramètres, ou de cliquer « relancer », pour que le traitement reprenne.
  - Un extrait n'est plus réessayé : les deux tentatives suivantes renverraient le même extrait.
    Une piste coûtait jusqu'à **douze appels** à l'API pour rien, elle en coûte au plus quatre.
  - Un extrait sur une seule qualité ne bloque rien. Un compte sans Hi-Res en reçoit un pour le
    24 bits puis est servi en FLAC 16 bits sans broncher : seul un extrait sur **toute** la chaîne,
    MP3 320 compris, signe une absence de droits.
- **Un MP3 pouvait être écrit dans un fichier `.flac`.** Lorsque toutes les qualités FLAC échouent
  pour une piste, le téléchargement se replie sur le MP3 320 — mais l'extension était choisie avant
  la première tentative, d'après la qualité demandée. Le fichier produit était donc mal nommé, et
  l'étiqueteur, qui choisit son analyseur sur l'extension, y écrivait des commentaires Vorbis au
  lieu de balises ID3. La réparation des sommes MD5, elle, lançait `flac` sur un MP3. L'extension
  suit désormais la qualité réellement servie. Cas rare — il faut que les trois paliers FLAC
  échouent — mais le fichier restait silencieusement inexploitable.

### Security

- **Redirection ouverte depuis l'écran de verrouillage.** Le champ caché qui mémorise la page
  d'origine n'acceptait qu'un chemin local, mais le test laissait passer `/\exemple.com` : les
  navigateurs normalisent l'antislash en barre oblique, et la redirection partait hors du site.
  Un mot de passe valide n'était même pas nécessaire — un échec redirige aussi. Le chemin est
  désormais filtré comme le fait `Url.IsLocalUrl`.
- **Le mot de passe d'accès hérité est enfin remigré.** Les installations antérieures stockaient
  une empreinte SHA256 sans sel et sur un seul tour, à la portée d'une table précalculée si
  `settings.json` venait à fuir. Elle est maintenant remplacée par du PBKDF2 au premier
  déverrouillage réussi, le seul instant où l'application détient le mot de passe en clair. La
  comparaison héritée se fait de surcroît en temps constant. Les autres appareils déjà
  déverrouillés redemandent le mot de passe une fois après cette migration.

## [1.5.1] - 2026-09-28

### Changed
- **L'application Android quitte ce dépôt.** Le dossier `android/` devient le projet autonome
  `QobuzDL.Remote`, à côté de celui-ci, et son identifiant d'application passe de
  `com.qobuzdlx.app` à `com.qobuzdl.remote`. Rien ne change côté serveur : la coquille WebView
  n'affiche que l'interface web et n'a jamais rien partagé avec le projet .NET. Les liens
  `android/…` des entrées antérieures de ce fichier ne sont donc plus valides — ils sont conservés
  tels quels, l'historique décrivant l'arborescence de l'époque.
  - Conséquence côté téléphone : l'identifiant ayant changé, Android y voit une nouvelle
    application. Elle s'installe à côté de l'ancienne, l'adresse du serveur est à ressaisir, et
    l'ancienne APK reste à désinstaller à la main.

### Fixed
- **Un quatrième couple `app_id` / `app_secret` de repli** à l'essai automatique lors de la
  connexion par jeton, pour les comptes que les trois précédents n'authentifient pas. Toujours
  aucune saisie demandée à l'utilisateur.
- **Séparateur décimal dans les noms de fichiers.** Depuis la 1.3.0, la culture du processus suit
  la langue de l'interface : en français, `%SampleRate%` et `%FormatWithQuality%` écrivaient
  `44,1kHz` au lieu de `44.1kHz`. Les gabarits sont désormais formatés en culture invariante,
  indépendamment de la langue choisie.

## [1.5.0] - 2026-09-07

### Added
- **Application Android** (`android/`). Une coquille WebView qui affiche l'interface web de votre
  instance : aucune interface n'est dupliquée, l'application suit donc chaque évolution du site.
  - **Écran de configuration** : saisir `192.168.1.42`, `qobuz.mondomaine.fr` ou une URL complète.
    L'adresse est essayée en HTTPS et en HTTP, avec et sans le port 8080 — les quatre en
    parallèle — et n'est enregistrée qu'une fois `/healthz` confirmé. En cas d'échec, le message
    liste les adresses tentées : un port publié autre que 8080 ne peut pas être deviné et doit
    être saisi.
  - **Changer de serveur** par appui long sur l'icône du lanceur, sans avoir à effacer les
    données de l'application.
  - **Avertissement en clair** : une adresse `http://` vers un hôte qui n'est ni sur un réseau
    privé ni dans un tunnel (Tailscale, WireGuard) déclenche une confirmation explicite, le mot
    de passe d'accès circulant alors en clair.
  - Rotation et bascule clair/sombre **ne rechargent plus la page** ; retour arrière, insets et
    clavier gérés nativement ; écran d'erreur avec « Réessayer » et « Changer de serveur ».
  - Compilation depuis Android Studio, voir [android/README.md](android/README.md).
- **Déverrouillage persistant.** Le mot de passe d'accès n'est plus redemandé à chaque
  rechargement : il est échangé contre un cookie de 30 jours à expiration glissante, qui survit à
  la fermeture du navigateur **et au redémarrage du conteneur**.
  - Changer ou retirer le mot de passe dans les Paramètres **déverrouille tous les appareils**.
- **Reconnexion automatique.** Quand le circuit tombe (mise en veille, coupure Wi-Fi, redémarrage
  du serveur), un bandeau aux couleurs de l'application remplace l'overlay gris illisible de
  Blazor, et la page se recharge d'elle-même si les tentatives échouent. Aucun état n'est perdu :
  recherche, tris et file de téléchargement sont restitués tels quels.
- **Icône et manifeste web** : l'application peut être ajoutée à l'écran d'accueil et s'ouvre
  alors sans barre d'adresse.
- Sonde **`/healthz`** exposant le nom et la version de l'application.

### Fixed
- L'écran de verrouillage **répond immédiatement**. Le formulaire attendait l'établissement du
  circuit SignalR : sur un réseau lent, le bouton restait inerte plusieurs secondes alors que la
  page était visible. C'est désormais un formulaire HTML classique, qui fonctionne dès le
  prérendu — et que le gestionnaire de mots de passe du téléphone sait remplir.
- La **limite de tentatives** de déverrouillage était décorative : son compteur vivait dans le
  circuit, donc ouvrir un nouvel onglet le remettait à zéro. Il est maintenant global à
  l'application, avec un délai qui double à chaque série d'échecs (30 s, 1 min, 5 min, 15 min).
- Un changement de langue n'est plus interrompu à mi-chemin lorsqu'un onglet fermé se trouve
  parmi les abonnés.

## [1.4.0] - 2026-08-25

### Added
- **File d'attente de téléchargement.** Cliquer sur « Télécharger » n'attend plus la fin du
  téléchargement en cours : la demande est **empilée** et traitée dès que possible. On peut donc
  lancer l'intégrale d'un artiste, continuer à chercher, et ajouter trois albums au passage sans
  jamais bloquer l'interface.
  - La page **Téléchargement** affiche la file : vignette, titre, artiste, état
    (*En attente*, *En cours*, *Terminé*, *Échec*, *Annulé*) et actions.
  - Actions sur les éléments **en attente** : monter, descendre, retirer.
  - Actions sur les éléments **terminés** : relancer, retirer. Ils restent visibles jusqu'à
    « Effacer les terminés ».
  - **Pastille** sur « Téléchargement » dans le menu : nombre d'éléments restants.
  - Côté **Recherche**, un **toast** confirme l'ajout, le bouton d'un élément déjà en file est
    **grisé**, et un **✓** discret marque ce qui a déjà été téléchargé. Le bouton reste cliquable
    une fois l'élément terminé, pour retélécharger si besoin.
  - La file vit **en mémoire** : elle est perdue au redémarrage du conteneur, comme les résultats
    de recherche.
- **Trois gestes d'interruption distincts**, là où il n'y avait qu'un bouton « ANNULER » :
  - **PASSER** saute l'album en cours et poursuit le même élément (utile sur un artiste de vingt
    albums) ;
  - **ANNULER** abandonne l'élément entier et enchaîne sur le suivant ;
  - **VIDER LA FILE** retire les éléments en attente, sans toucher à celui qui tourne.
- Bouton **« Effacer le journal »**.
- **Adaptation mobile.** L'interface est utilisable sur téléphone : barre supérieure avec menu
  tiroir sous 768 px, listes de résultats densifiées, boutons tactiles élargis, prise en compte
  des encoches et barres système (`safe-area`), et plus de zoom intempestif au focus sur iOS.

### Changed
- Le **journal ne s'efface plus automatiquement** au début d'un téléchargement : avec une file
  qui enchaîne, tout effacement automatique serait arbitraire. Il est borné à 1200 lignes, séparé
  par un en-tête à chaque élément, et effaçable à la demande.
- Le journal **défile automatiquement** vers les nouvelles lignes (il ne défilait pas du tout).
- La carte album et la vitesse sont **remises à zéro** en fin de file, au lieu de rester
  affichées indéfiniment.
- Les boutons **ANNULER** et **PASSER** quittent la barre d'URL pour la ligne de l'élément en
  cours, où ils désignent sans ambiguïté ce sur quoi ils agissent.
- Le **téléchargement en lot** depuis la Recherche et la zone multi-URL alimentent la file au
  lieu de bloquer l'interface jusqu'au dernier élément.

### Fixed
- Les URL portant une **chaîne de requête** (`.../album/abc?utm_source=share`) échouaient : le
  `?utm_source=share` finissait dans l'identifiant envoyé à l'API. Elles sont désormais
  normalisées à l'ajout, ce qui les rend aussi reconnaissables comme doublons.
- **Tempête d'événements** pendant les téléchargements : la progression était signalée à chaque
  bloc de 80 Ko reçu — des dizaines de fois par seconde, sur tous les onglets ouverts. Elle est
  maintenant cadencée à 200 ms. Le calcul de vitesse, lui, reste une moyenne depuis le début du
  fichier.
- L'arrêt du conteneur (`docker stop`) interrompt immédiatement le téléchargement en cours au
  lieu d'attendre l'expiration du délai de grâce.
- Un échec sur un élément n'interrompt plus la suite : le traitement se poursuit avec l'élément
  suivant, l'élément fautif étant marqué *Échec* avec son message d'erreur.

## [1.3.0] - 2026-08-23

### Added
- **Interface multilingue français / anglais.** Une bascule `FR | EN` est disponible dans la
  barre latérale, sur l'écran de connexion et sur l'écran de mot de passe. Le changement est
  immédiat, sans rechargement de page : les résultats de recherche, un téléchargement en cours
  et le déverrouillage par mot de passe sont conservés.
  - La langue est un réglage **global**, enregistré dans `settings.json` comme le reste.
  - Au tout premier lancement, la langue du navigateur est détectée via l'en-tête
    `Accept-Language` : anglais si le navigateur est anglophone, **français dans tous les
    autres cas**. Le choix explicite de l'utilisateur prime ensuite.
  - Les installations existantes démarrent en français.
- **Tri des résultats de recherche** : pertinence, titre, artiste, date de sortie, qualité,
  nombre de pistes (albums) ou d'albums (artistes), en ordre croissant ou décroissant.

### Changed
- La page d'accueil (`/`) ouvre désormais sur la **Recherche** plutôt que sur le téléchargeur.
- « Téléchargeur » est renommé **« Téléchargement »** et accessible sur `/telechargement` ;
  l'ancienne adresse `/downloader` reste valide.
- Le **sélecteur de qualité** quitte la page Téléchargement pour rejoindre les **Paramètres**,
  aux côtés des autres réglages. La page Téléchargement n'en affiche plus qu'un rappel, avec un
  lien direct vers les paramètres.
- Le **journal de téléchargement**, jusqu'ici toujours en anglais alors que l'interface était en
  français, suit maintenant la langue choisie. Les lignes déjà affichées conservent leur langue
  d'origine après une bascule ; le journal n'est pas réécrit rétroactivement.
- **Image Docker** : ICU est installé (`icu-libs`, `icu-data-full`) et le
  *globalization-invariant mode* désactivé. Les nombres sont formatés selon la langue
  (`1,23 Mo/s` en français) et le tri alphabétique tient compte des accents. L'image gagne
  environ 30 Mo.

### Removed
- Le réglage `Language`, vestige inutilisé du portage depuis l'application desktop, remplacé par
  `UiLanguage`. L'ancienne clé est ignorée au chargement et disparaît à la première sauvegarde.

## [1.2.52] - 2026-07-17

### Fixed
- **Nettoyage des caractères interdits dans les noms de fichiers/dossiers.**
  Les caractères non autorisés sous Windows/NAS (`* : ? | " < > \ /`) présents dans les
  titres d'albums ou de pistes étaient auparavant remplacés par des homoglyphes Unicode
  obscurs (`:` → `∶`, `?` → `ʔ`, `|` → `ǀ`, `"` → `″`, `<`/`>` → `˂`/`˃`, `\` → `⧹`,
  `*` → `∗`), produisant des noms illisibles. Ils sont désormais remplacés par des
  substituts ASCII lisibles :
  - `:` → ` -` (ex. `Song: Intro` → `Song - Intro`)
  - `/` et `\` → `-` (ex. `AC/DC` → `AC-DC`)
  - `* ? | " < >` → supprimés (ex. `Who? What!` → `Who What!`)
- Un `/` provenant d'un **titre** (ex. `AC/DC`, `24/7`) créait un sous-dossier
  involontaire. Il est maintenant converti en `-`. Le `/` saisi dans les **modèles**
  de nommage reste bien un séparateur de dossier.
- Normalisation des espaces multiples et suppression des séparateurs superflus en début/fin
  de nom, y compris pour les fichiers bonus (goodies).

### Changed
- La version de l'application est désormais affichée dans la barre latérale.
