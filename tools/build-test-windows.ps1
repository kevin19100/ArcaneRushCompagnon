param(
  [string]$DotNetExe = "dotnet",
  [string]$Version = "",
  [string]$GitHubRepository = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\ArcaneRushSync\ArcaneRushSync.csproj"
$nugetConfig = Join-Path $root "NuGet.Config"
$packages = Join-Path $root ".nuget\packages"
$out = Join-Path $root "artifacts\win-x64"

if ([string]::IsNullOrWhiteSpace($Version)) {
  [xml]$projectXml = Get-Content $project
  $versionNode = Select-Xml -Xml $projectXml -XPath "/Project/PropertyGroup/Version" | Select-Object -First 1
  $Version = if ($versionNode) { [string]$versionNode.Node.InnerText } else { "1.1.0" }
  if ([string]::IsNullOrWhiteSpace($Version)) { $Version = "1.1.0" }
}

if ([string]::IsNullOrWhiteSpace($GitHubRepository) -and (Get-Command git -ErrorAction SilentlyContinue)) {
  try {
    $remote = (& git -C $root config --get remote.origin.url 2>$null).Trim()
    if ($remote -match 'github\.com[:/](?<owner>[^/]+)/(?<repo>[^/]+?)(?:\.git)?$') {
      $GitHubRepository = "$($Matches.owner)/$($Matches.repo)"
    }
  } catch { }
}

Write-Host "Version : $Version" -ForegroundColor DarkGray
if (-not [string]::IsNullOrWhiteSpace($GitHubRepository)) {
  Write-Host "Depot GitHub integre : $GitHubRepository" -ForegroundColor DarkGray
} else {
  Write-Host "Depot GitHub : non integre dans ce build local (le bouton MAJ restera cache)." -ForegroundColor DarkGray
}

Write-Host "===============================================================" -ForegroundColor DarkYellow
Write-Host " ARCANE RUSH SYNC $Version - BUILD WINDOWS" -ForegroundColor Yellow
Write-Host "===============================================================" -ForegroundColor DarkYellow
Write-Host ""
Write-Host "SDK utilise : $DotNetExe"
& $DotNetExe --version
if ($LASTEXITCODE -ne 0) { throw "Impossible d'executer le SDK .NET." }

if (-not (Test-Path $nugetConfig)) {
  throw "NuGet.Config est introuvable. Re-extrais completement l’archive Arcane Rush Sync 1.1.0."
}

# Never depend on the PC's global NuGet configuration/cache. The first test build
# uses a project-local source definition and package cache so PackageSourceMapping,
# disabled feeds or stale packages on the machine cannot silently alter the build.
$env:NUGET_PACKAGES = $packages
$env:NUGET_XMLDOC_MODE = "skip"
New-Item -ItemType Directory -Force -Path $packages | Out-Null

Write-Host ""
Write-Host "Source NuGet forcee pour ce projet :" -ForegroundColor DarkGray
Write-Host "  https://api.nuget.org/v3/index.json" -ForegroundColor DarkGray

# Quick connectivity check. It is diagnostic only: dotnet restore remains the
# authority because some Windows setups can reach NuGet even if Invoke-WebRequest
# is filtered differently by a security product.
try {
  $null = Invoke-WebRequest -Uri "https://api.nuget.org/v3/index.json" -Method Head -UseBasicParsing -TimeoutSec 10
  Write-Host "Connexion NuGet : OK" -ForegroundColor Green
} catch {
  Write-Host "Connexion NuGet : verification HTTP non concluante ($($_.Exception.Message))" -ForegroundColor Yellow
  Write-Host "Le build va quand meme tenter la restauration avec dotnet." -ForegroundColor Yellow
}

if (Test-Path $out) {
  Remove-Item $out -Recurse -Force
}
New-Item -ItemType Directory -Path $out | Out-Null

Write-Host ""
Write-Host "[1/2] Restauration des dependances..." -ForegroundColor Cyan
& $DotNetExe restore $project `
  --runtime win-x64 `
  --configfile $nugetConfig `
  --packages $packages `
  --force `
  --no-cache
if ($LASTEXITCODE -ne 0) {
  Write-Host "" -ForegroundColor Red
  Write-Host "DIAGNOSTIC NUGET" -ForegroundColor Yellow
  & $DotNetExe nuget list source --configfile $nugetConfig
  throw "dotnet restore a echoue. Si l'erreur mentionne encore NU1100, copie tout ce bloc ; le probleme sera alors un acces HTTPS a api.nuget.org, pas une dependance manquante."
}

Write-Host ""
Write-Host "[2/2] Publication Windows x64 autonome..." -ForegroundColor Cyan
$publishArgs = @(
  "publish", $project,
  "--configuration", "Release",
  "--runtime", "win-x64",
  "--self-contained", "true",
  "--no-restore",
  "--output", $out,
  "-p:RestorePackagesPath=$packages",
  "-p:DebugType=None",
  "-p:DebugSymbols=false",
  "-p:Version=$Version",
  "-p:FileVersion=$Version.0",
  "-p:AssemblyVersion=$Version.0"
)
if (-not [string]::IsNullOrWhiteSpace($GitHubRepository)) {
  $publishArgs += "-p:GitHubRepository=$GitHubRepository"
}
& $DotNetExe @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish a echoue." }

$exe = Join-Path $out "ArcaneRushSync.exe"
if (-not (Test-Path $exe)) {
  throw "ArcaneRushSync.exe n'a pas ete genere."
}

$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
$info = @(
  "Arcane Rush Sync $Version",
  "Build UTC: $([DateTime]::UtcNow.ToString('o'))",
  "Runtime: win-x64 self-contained",
  "NuGet source: https://api.nuget.org/v3/index.json",
  "Executable: ArcaneRushSync.exe",
  "SHA256: $hash"
)
$info | Set-Content -Path (Join-Path $out "BUILD_INFO.txt") -Encoding UTF8

Write-Host ""
Write-Host "Build termine." -ForegroundColor Green
Write-Host "EXE : $exe"
Write-Host "SHA256 : $hash"
