$ErrorActionPreference = "Stop"
$base = Join-Path $env:LOCALAPPDATA "ArcaneRushSync"
$backup = Join-Path $base "proxy-backup.json"
$internetSettings = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings"
$names = @("ProxyEnable", "ProxyServer", "ProxyOverride", "AutoConfigURL", "AutoDetect")

Write-Host "Arcane Rush Sync - restauration de securite" -ForegroundColor Cyan

if (Test-Path $backup) {
    try {
        $data = Get-Content $backup -Raw | ConvertFrom-Json
        foreach ($name in $names) {
            $property = $data.PSObject.Properties[$name]
            if ($null -eq $property -or $null -eq $property.Value) {
                Remove-ItemProperty -Path $internetSettings -Name $name -ErrorAction SilentlyContinue
                continue
            }

            $value = $property.Value
            if ($value -is [int] -or $value -is [long]) {
                New-ItemProperty -Path $internetSettings -Name $name -Value ([int]$value) -PropertyType DWord -Force | Out-Null
            } else {
                New-ItemProperty -Path $internetSettings -Name $name -Value ([string]$value) -PropertyType String -Force | Out-Null
            }
        }
        Remove-Item $backup -Force -ErrorAction SilentlyContinue
        Write-Host "Reglages proxy Windows restaures." -ForegroundColor Green
    } catch {
        Write-Host "Impossible de restaurer le fichier proxy automatiquement : $($_.Exception.Message)" -ForegroundColor Red
    }
} else {
    Write-Host "Aucune sauvegarde proxy en attente." -ForegroundColor DarkGray
}

# Remove only the temporary CA created by Arcane Rush Sync in CurrentUser.
foreach ($storePath in @("Cert:\CurrentUser\Root", "Cert:\CurrentUser\My")) {
    try {
        Get-ChildItem $storePath -ErrorAction SilentlyContinue |
            Where-Object { $_.Subject -like "*CN=Arcane Rush Sync Local CA*" -and $_.Issuer -like "*Arcane Rush Sync*" } |
            Remove-Item -Force -ErrorAction SilentlyContinue
    } catch { }
}

$pfx = Join-Path $base "proxy\rootCert.pfx"
Remove-Item $pfx -Force -ErrorAction SilentlyContinue

# Tell WinINet/Windows that Internet Settings changed.
Add-Type -Namespace ArcaneRushSyncRecovery -Name WinInet -MemberDefinition @'
[DllImport("wininet.dll", SetLastError=true)]
public static extern bool InternetSetOption(System.IntPtr hInternet, int dwOption, System.IntPtr lpBuffer, int dwBufferLength);
'@
[ArcaneRushSyncRecovery.WinInet]::InternetSetOption([IntPtr]::Zero, 39, [IntPtr]::Zero, 0) | Out-Null
[ArcaneRushSyncRecovery.WinInet]::InternetSetOption([IntPtr]::Zero, 37, [IntPtr]::Zero, 0) | Out-Null

Write-Host "Certificat temporaire nettoye. Restauration terminee." -ForegroundColor Green
Write-Host "Tu peux fermer cette fenetre." -ForegroundColor DarkGray
Read-Host | Out-Null
