# Arcane Rush Sync

Application Windows WPF qui détecte le pseudo, la collection et les 13 decks Arcane Rush puis les synchronise avec Arcane Rush Companion.

Cette archive est la base de développement Windows/GitHub. Elle ne contient aucun composant Microsoft Store/MSIX.

## Lancer l'addon depuis les sources

1. Sous Windows, double-cliquer sur `BUILD_TEST_WINDOWS.bat`.
2. Le script vérifie la présence du SDK .NET 10 et propose son installation s'il manque.
3. Il compile une version Windows x64 autonome dans `artifacts\win-x64`.
4. Quand le dossier s'ouvre, lancer `ArcaneRushSync.exe`.

Après une modification du code, relancer `BUILD_TEST_WINDOWS.bat` avant de tester la nouvelle version.

## Mise à jour automatique GitHub

L'addon vérifie au démarrage la dernière **GitHub Release** du dépôt depuis lequel il a été compilé. Si la Release possède une version supérieure à celle installée et contient exactement le fichier :

`ArcaneRushSync-win-x64.zip`

un bouton **MAJ DISPONIBLE** apparaît en haut de l'addon.

Au clic, l'addon :

1. télécharge le ZIP depuis GitHub ;
2. vérifie le SHA-256 fourni par GitHub lorsqu'il est disponible ;
3. prépare la nouvelle version dans `%LOCALAPPDATA%\ArcaneRushSync\updates` ;
4. ferme proprement le scanner et restaure le proxy Windows ;
5. remplace les fichiers de l'ancienne version ;
6. relance automatiquement Arcane Rush Sync ;
7. nettoie les fichiers temporaires de mise à jour.

La session Firebase, le consentement scanner, le certificat local et les données utilisateur restent dans `%LOCALAPPDATA%\ArcaneRushSync` et ne sont donc pas supprimés par une mise à jour.

### Publier une nouvelle version

Une fois le projet placé dans ton dépôt GitHub, tu n'as pas besoin d'écrire l'adresse du dépôt dans le code : GitHub Actions l'intègre automatiquement dans l'EXE.

Pour publier par exemple la version `1.2.0` :

```powershell
git add .
git commit -m "Arcane Rush Sync 1.2.0"
git push
git tag sync-v1.2.0
git push origin sync-v1.2.0
```

Le workflow GitHub compile alors la version `1.2.0`, crée `ArcaneRushSync-win-x64.zip` et crée automatiquement la Release correspondante. Les utilisateurs en `1.1.0` verront ensuite **MAJ DISPONIBLE** au prochain lancement de l'addon.

Important : un simple fichier ZIP poussé dans les sources du dépôt n'est pas considéré comme une mise à jour. Il faut une **Release GitHub publiée**. Le workflow fourni s'en charge automatiquement lors d'un tag `sync-vX.Y.Z`.

## Structure

- `src/ArcaneRushSync/` : application et code source.
- `BUILD_TEST_WINDOWS.bat` : compilation Windows en un double-clic.
- `tools/build-test-windows.ps1` : script de publication appelé par le BAT.
- `REPARER_RESEAU_WINDOWS.bat` : restauration réseau de secours.
- `tools/recover-network.ps1` : logique de réparation réseau.
- `ArcaneRushSync.sln` : solution Visual Studio.
- `NuGet.Config` : source NuGet utilisée pour restaurer les dépendances.
- `.github/workflows/build-windows.yml` : build + génération de Release lors d'un tag GitHub.
- `LICENSE-THIRD-PARTY.md` : licences des dépendances tierces.

## Scanner local

Le scanner fonctionne sans droits administrateur applicatifs, conserve son certificat TLS local dans le magasin `CurrentUser`, restaure les paramètres proxy après le scan et protège la session Firebase avec Windows DPAPI.

`REPARER_RESEAU_WINDOWS.bat` est prévu uniquement comme outil de secours pour restaurer le réseau et retirer le certificat local Arcane Rush Sync.
