; Arcane Rush Sync — installeur Windows par utilisateur (aucun droit administrateur).
;
; Pourquoi un installeur plutôt qu'un ZIP à extraire soi-même :
;  * Windows SmartScreen ne vérifie qu'UN fichier (le Setup) au lieu de chaque DLL extraite
;    d'un ZIP marqué « téléchargé depuis Internet » ;
;  * l'application vit dans %LOCALAPPDATA%\Programs (emplacement standard, pas Téléchargements/Temp,
;    deux dossiers que les antivirus surveillent plus agressivement) ;
;  * raccourci Menu Démarrer + désinstallation propre depuis « Applications installées » ;
;  * la désinstallation restaure le proxy Windows et retire le certificat local.
;
; Compilé par GitHub Actions :  ISCC.exe /DAppVersion=1.2.0 /DSourceDir=..\artifacts\win-x64 installer\ArcaneRushSync.iss

#ifndef AppVersion
  #define AppVersion "1.2.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

[Setup]
AppId={{ABF0FEC0-7D1E-4273-BAD3-5777ABC817C0}
AppName=Arcane Rush Sync
AppVersion={#AppVersion}
AppVerName=Arcane Rush Sync {#AppVersion}
AppPublisher=Arcane Rush Companion
AppPublisherURL=https://arcane-rush-compagnon-2026.web.app
AppSupportURL=https://github.com/kevin19100/ArcaneRushCompagnon
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany=Arcane Rush Companion
VersionInfoDescription=Installation d'Arcane Rush Sync
VersionInfoProductName=Arcane Rush Sync
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
DefaultDirName={localappdata}\Programs\ArcaneRushSync
DisableDirPage=yes
DisableProgramGroupPage=yes
DefaultGroupName=Arcane Rush Sync
UninstallDisplayIcon={app}\ArcaneRushSync.exe
UninstallDisplayName=Arcane Rush Sync
OutputDir={#OutputDir}
OutputBaseFilename=ArcaneRushSync-Setup
SetupIconFile=..\src\ArcaneRushSync\Assets\ArcaneRushSync.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Arcane Rush Sync"; Filename: "{app}\ArcaneRushSync.exe"
Name: "{autodesktop}\Arcane Rush Sync"; Filename: "{app}\ArcaneRushSync.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ArcaneRushSync.exe"; Description: "{cm:LaunchProgram,Arcane Rush Sync}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Restaure le proxy Windows et retire le certificat local avant de supprimer les fichiers.
; Désinstallation normale : Windows demande de confirmer le retrait du certificat (répondre Oui).
Filename: "{app}\ArcaneRushSync.exe"; Parameters: "--repair-network --quiet"; Flags: waituntilterminated skipifdoesntexist; RunOnceId: "ArcaneRushSyncRepair"; Check: not UninstallSilent
; Désinstallation silencieuse : aucune fenêtre ne doit bloquer, on restaure seulement le proxy.
Filename: "{app}\ArcaneRushSync.exe"; Parameters: "--repair-network --quiet --keep-certificate"; Flags: waituntilterminated skipifdoesntexist; RunOnceId: "ArcaneRushSyncRepairSilent"; Check: UninstallSilent

[UninstallDelete]
; Les mises à jour automatiques peuvent ajouter des fichiers inconnus de l'installeur.
Type: filesandordirs; Name: "{app}"
