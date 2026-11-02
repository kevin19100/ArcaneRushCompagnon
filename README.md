# Arcane Rush Sync

Application Windows (WPF, .NET 10) qui lance Arcane Rush, lit pendant le chargement du jeu
ta **collection** (et tes decks équipés, et ton pseudo) puis les enregistre sur ton compte
du site [Arcane Rush Companion](https://arcane-rush-compagnon-2026.web.app).

## Utilisation (joueur)

1. Télécharger **`ArcaneRushSync-Setup.exe`** depuis la page *Télécharger* du site
   (ou `releases/latest`). L'installation se fait dans ton profil Windows, sans droits administrateur.
2. Ouvrir Arcane Rush Sync et se connecter avec **le même compte que sur le site** (e-mail ou Google).
3. Cliquer **« Lancer Arcane Rush et synchroniser »** :
   * le scanner local s'arme, puis le jeu est lancé via Steam (ou directement si Steam est introuvable) ;
   * si le jeu était déjà ouvert, Sync propose de le fermer et de le relancer (la collection n'est envoyée
     par le serveur du jeu qu'au démarrage) ;
   * dès que la collection exacte est lue, le proxy Windows est restauré et la page *Ma collection*
     du site se met à jour toute seule.
4. En cas de souci réseau : bouton **« Réparer le réseau »** (ou `ArcaneRushSync.exe --repair-network`).
   La désinstallation fait la même réparation automatiquement.

## Ce qui est écrit sur le compte (`users/{uid}` dans Firestore)

| Champ | Contenu |
|---|---|
| `ownedIds` | toutes les cartes `SK_x_y_z` possédées (bootstrap `/001003`) |
| `collectionSyncSource` | `arcane-rush-addon` → le site considère la collection comme confirmée par le jeu |
| `equippedCardIdsByDealer.SK_n` / `equippedDecks.SK_n` | 20 cartes par marchand détecté (chaque marchand est écrit séparément : un scan partiel n'efface jamais un deck déjà connu) |
| `gamePlayerName` | pseudo, seulement s'il est vérifié |

Les 14 marchands, Arcanistes (`SK_17`, Elyra) compris, sont reconnus. Les données de cartes
(`src/ArcaneRushSync/Data`) sont celles du site (`public/data/cards.json`, `heroes.json`) :
après un `UPDATE_CARDS` du site, recopier ces deux fichiers ici puis publier une version.

## Antivirus et SmartScreen

Ce qui a été fait pour limiter les alertes :

* **installeur par utilisateur** (Inno Setup) : SmartScreen ne contrôle qu'un fichier au lieu de
  chaque DLL extraite d'un ZIP « provenant d'Internet », et l'application vit dans
  `%LOCALAPPDATA%\Programs\ArcaneRushSync` au lieu de Téléchargements/Temp ;
* plus aucun `.bat` / PowerShell `-ExecutionPolicy Bypass` distribué : la réparation réseau est intégrée à l'EXE ;
* aucun droit administrateur, aucun pilote, aucune injection dans le jeu, aucune lecture mémoire ;
* le proxy n'est actif que pendant le chargement du jeu, et seul `api-overhaul.cbg.alleylabs.com` est déchiffré ;
* métadonnées de version complètes, `SHA256SUMS.txt` publié avec chaque release ;
* signature de code prête dans le workflow (voir ci-dessous).

Ce qui reste hors de portée du code : un EXE **non signé** affichera « Éditeur inconnu » tant qu'il n'a
pas de signature ou de réputation SmartScreen. Si un antivirus le détecte, soumettre le fichier
comme faux positif : <https://www.microsoft.com/wdsi/filesubmission> (Defender) et le portail de l'éditeur concerné.

### Activer la signature (optionnel)

Le workflow signe l'EXE, la DLL et le Setup avec **Azure Artifact Signing** si la variable de dépôt
`SIGNING_ENABLED=true` existe, avec :

* variables : `SIGNING_ENDPOINT` (ex. `https://weu.codesigning.azure.net`), `SIGNING_ACCOUNT`, `SIGNING_PROFILE` ;
* secrets : `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`.

Sans ces réglages les étapes de signature sont simplement ignorées.

## Développement

* `BUILD_TEST_WINDOWS.bat` : compile une version de test dans `artifacts\win-x64`.
* `installer/ArcaneRushSync.iss` : script de l'installeur.
* `.github/workflows/build-windows.yml` : build + Setup + ZIP + checksums ; Release automatique sur un tag `sync-vX.Y.Z`.

### Publier une version

```powershell
git tag sync-v1.2.0
git push origin sync-v1.2.0
```

La Release contient `ArcaneRushSync-Setup.exe` (première installation),
`ArcaneRushSync-win-x64.zip` (utilisé par le bouton **MAJ DISPONIBLE** des versions déjà installées)
et `SHA256SUMS.txt`. Le nom `ArcaneRushSync-win-x64.zip` ne doit pas changer : les versions 1.1.x le cherchent.

## Structure

- `src/ArcaneRushSync/` : application.
- `src/ArcaneRushSync/Services/GameScannerService.cs` : relais local (Titanium.Web.Proxy) limité au domaine du jeu.
- `src/ArcaneRushSync/Services/DeckDetector.cs` : extraction collection / decks / pseudo.
- `src/ArcaneRushSync/Services/FirestoreSyncService.cs` : écriture vérifiée sur Firestore.
- `src/ArcaneRushSync/Services/GameLauncherService.cs` : lancement via Steam (`steam://rungameid/3039040`).
- `src/ArcaneRushSync/Services/NetworkRepairService.cs` : restauration proxy + retrait du certificat local.
- `LICENSE-THIRD-PARTY.md` : licences des dépendances.
