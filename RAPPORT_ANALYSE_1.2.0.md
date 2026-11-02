# Analyse ArcaneRushTools et passage d'Arcane Rush Sync en 1.2.0 (2 novembre 2026)

## 1. Ce qui a été analysé

| Élément | Emplacement | Rôle |
|---|---|---|
| Site | `ArcaneRushTools\Site\2.14` | React + Vite, Firebase Hosting/Auth/Firestore. Les collections sont dans `users/{uid}`. Catalogue `public/data/cards.json` : 1 553 cartes, 14 marchands. |
| Prediction | `ArcaneRushTools\Prediction\ArcaneRushCompagnon-v0.23.1` | Compagnon Python perso (mitmproxy) : boutique, prédictions, recherche. Il connaît déjà les Arcanistes `SK_17`. |
| Arcane Rush Sync | GitHub `kevin19100/ArcaneRushCompagnon` (le dossier manquant sur ton PC) | Application WPF .NET 10 téléchargée depuis la page Télécharger du site. Version publiée : 1.1.0. |

Le code source du Sync n'était pas perdu : il est complet sur GitHub. La copie de ton bureau est désormais
`ArcaneRushTools\Sync\ArcaneRushCompagnon`.

## 2. Problèmes trouvés dans la 1.1.0

1. **Les Arcanistes étaient rejetés.** Le catalogue embarqué datait d'avant `SK_17` : 1 419 cartes contre 1 553 sur le site,
   et 134 cartes manquantes, dont les 100 Arcanistes. Le scanner ne garde que les identifiants connus. Ces cartes
   n'arrivaient donc jamais sur le site, et le deck d'Elyra non plus.
2. **Le site ne considérait pas la collection comme venant du jeu.** Le Sync écrivait `collectionSyncSource = "arcane-rush-sync"`,
   alors que `cloud.jsx` attend `"arcane-rush-addon"`. Résultat : le site **fusionnait** avec l'ancienne collection du navigateur.
   Des cartes non possédées pouvaient donc réapparaître, puis être renvoyées sur le compte avec la source `website`.
3. **Les decks équipés n'étaient jamais affichés.** Le Sync écrivait `equippedDecks.{SK_n}`, alors que le site lit
   `equippedCardIdsByDealer`, ou à défaut `equippedDecks.merchants`.
4. **Aucune synchronisation sans 13 decks trouvés et sans pseudo vérifié.** Un joueur qui ne possède pas tous les marchands,
   ou dont le pseudo n'est pas reconnu, ne pouvait jamais envoyer sa collection.
5. **Le jeu ne se lançait pas depuis le logiciel.** Il fallait armer le scanner puis lancer le jeu à la main au bon moment.
6. **Distribution peu favorable aux antivirus.** Le ZIP à extraire soi-même était marqué « Internet », ce qui touchait chaque DLL.
   Il y avait aussi `REPARER_RESEAU_WINDOWS.bat`, qui lance PowerShell avec `-ExecutionPolicy Bypass`, et
   la page du site annonçait un « Setup » qui n'existait pas.

## 3. Ce qui change dans la 1.2.0

- Catalogue de cartes et de héros repris du site. Les 14 marchands sont reconnus, Elyra / Arcaniste compris.
- Un seul bouton, **Lancer Arcane Rush et synchroniser** : il arme le scanner puis lance le jeu par Steam
  (`steam://rungameid/3039040`), ou directement par l'EXE trouvé dans la bibliothèque Steam. Si le jeu est déjà ouvert,
  le Sync propose de le fermer proprement, sans le tuer, puis le relance.
- Synchronisation dès que la collection exacte est lue. Les decks trouvés sont envoyés après 25 s sans nouveauté,
  ou immédiatement si les 14 sont trouvés. Chaque deck est écrit séparément : un scan partiel n'efface pas un deck connu.
- Écriture compatible avec le site : `collectionSyncSource = arcane-rush-addon`, `equippedCardIdsByDealer.SK_n`.
  Le pseudo n'est écrit que s'il est vérifié.
- Réparation réseau intégrée : bouton « Réparer le réseau » et `ArcaneRushSync.exe --repair-network`.
  Le `.bat` et le `.ps1` sont supprimés.
- Installeur `ArcaneRushSync-Setup.exe` (Inno Setup) : installation par utilisateur, sans admin, dans
  `%LOCALAPPDATA%\Programs`. La désinstallation restaure le réseau. Le ZIP reste publié pour la mise à jour automatique des 1.1.0.
- `SHA256SUMS.txt` est publié avec chaque version. La signature Azure Artifact Signing est prête mais désactivée par défaut.

## 4. Site : modifications faites

- `src/App.jsx` : le lien de téléchargement pointe vers `ArcaneRushSync-Setup.exe`, la version vers 1.2.0, et l'étape 3 parle du bouton « Lancer Arcane Rush ».
- `src/cloud.jsx` : `isGameSyncSource()` accepte `arcane-rush-addon` **et** `arcane-rush-sync`. Les collections déjà envoyées
  par la 1.1.0 sont donc aussi traitées comme venant du jeu.
- Les originaux sont sauvegardés à côté (`*.bak-avant-sync-1.2.0`). **Déploie le site après la publication de la release**,
  sinon le lien Setup renvoie une erreur 404.

## 5. Antivirus et Windows : ce qu'on peut et ne peut pas faire

Ce que le code ne peut pas supprimer :
- **SmartScreen « Éditeur inconnu »** : seule une signature de code l'enlève vraiment, ou une longue réputation de téléchargement.
  Azure Artifact Signing n'accepte pas, à ce jour, les particuliers en France : seulement les entreprises de l'UE, et les particuliers aux États-Unis et au Canada.
  Les pistes possibles : signer au nom d'une entreprise si tu en as une (micro-entreprise : à vérifier, des refus sont signalés),
  un certificat OV classique (Certum, Sectigo… sur clé matérielle ou dans le cloud), ou SignPath Foundation,
  gratuit mais réservé aux projets open source avec licence OSI et dépôt public. L'éditeur affiché serait alors « SignPath Foundation ».
- **La méthode de lecture** : un proxy local, un certificat racine dans le profil et un proxy système pendant le chargement.
  Les antivirus peuvent la juger « intrusive ». Elle reste nécessaire, car le jeu n'expose la collection nulle part ailleurs.
  La 1.2.0 réduit la fenêtre d'activité : le proxy est actif quelques secondes, de la demande de lancement jusqu'à la lecture.

À faire de ton côté si une détection apparaît : envoyer le fichier comme faux positif
(<https://www.microsoft.com/wdsi/filesubmission> pour Defender), sans désactiver l'antivirus.

## 6. Recommandations pour la suite

1. **Tester la 1.2.0 sur ton PC avant de taguer la release.** Tous les utilisateurs 1.1.0 la recevront par « MAJ DISPONIBLE ».
2. **Restreindre le certificat local à `alleylabs.com`** (extension *Name Constraints*). La clé privée du certificat racine est
   aujourd'hui un fichier `rootCert.pfx` sans mot de passe dans `%LOCALAPPDATA%`. Un programme malveillant qui la lirait pourrait
   intercepter n'importe quel site HTTPS de cet utilisateur. Ce changement n'est pas inclus dans la 1.2.0, car il doit être testé
   avec le vrai jeu : si le client Unity refuse la contrainte, la lecture ne marcherait plus du tout.
3. Après chaque `UPDATE_CARDS` du site, recopier `public/data/cards.json` → `Data/card_catalog.json`
   et `public/data/heroes.json` → `Data/heroes.json` dans le Sync, puis publier une nouvelle version.
4. Vérifier que les conditions d'utilisation d'Arcane Rush tolèrent la lecture de son trafic. Le Sync ne modifie rien,
   n'automatise rien et ne lit que le compte du joueur, mais c'est à Gear Games d'en décider.

## 7. Vérifications faites

- Couche services et modèles compilée (SDK .NET 9 avec des stubs pour Titanium et DPAPI). Le chargement du nouveau catalogue est exécuté :
  1 553 cartes valides, dont 100 Arcanistes, et 14 marchands.
- Fichiers du site analysés avec esbuild : OK.
- **Non vérifié ici** : la compilation complète WPF, Inno Setup et l'interface. Elle se fait sous Windows ou par GitHub Actions
  au premier push. Le scan réel en jeu n'a pas été testé non plus.
