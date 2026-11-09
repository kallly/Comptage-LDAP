#Requires -RunAsAdministrator
<#
=====================================================================================================
 Uninstall-Service.ps1 : désinstallation de « Comptage incident » en mode autonome (service Windows)
=====================================================================================================
.SYNOPSIS
    Supprime le service Windows et les fichiers installés par Install-Service.ps1, en SAUVEGARDANT la base.

.DESCRIPTION
    Étapes :
      1. Arrête le service.
      2. Copie App_Data (base SQLite + fichiers -wal/-shm) et appsettings.json dans un dossier de
         sauvegarde horodaté (sauf si -NoBackup est indiqué explicitement).
      3. Supprime le service Windows.
      4. Supprime la règle de pare-feu « Comptage incident » si elle existe.
      5. Supprime le dossier d'installation.

    Ne désinstalle PAS le runtime .NET (composant partagé). Ne supprime pas les SPN éventuellement
    déclarés pour un compte de service (voir docs/12-desinstallation.md).

    -WhatIf affiche ce qui serait fait, sans rien modifier.

.EXAMPLE
    .\Uninstall-Service.ps1
=====================================================================================================
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string] $ServiceName = 'ComptageIncident',
    [string] $InstallPath = 'C:\Apps\ComptageIncident',
    [string] $BackupPath  = ("C:\Sauvegardes\ComptageIncident-" + (Get-Date -Format 'yyyyMMdd-HHmmss')),
    [switch] $NoBackup
)

$ErrorActionPreference = 'Stop'

# 1. Arrêt du service (libère le fichier de base). Non exécuté en mode -WhatIf.
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne 'Stopped' -and $PSCmdlet.ShouldProcess($ServiceName, 'Arrêt du service')) {
    Stop-Service $ServiceName -Force
    Start-Sleep 2
}

# 2. Sauvegarde des données
$dataDir = Join-Path $InstallPath 'App_Data'
if (-not $NoBackup -and (Test-Path $dataDir)) {
    if ($PSCmdlet.ShouldProcess($BackupPath, "Sauvegarde de $dataDir")) {
        New-Item -ItemType Directory -Force -Path $BackupPath | Out-Null
        Copy-Item -Path $dataDir -Destination $BackupPath -Recurse -Force
        $settings = Join-Path $InstallPath 'appsettings.json'
        if (Test-Path $settings) { Copy-Item $settings $BackupPath -Force }
        Write-Host "Données sauvegardées dans : $BackupPath" -ForegroundColor Green
    }
}

# 3. Suppression du service (sc.exe : compatible avec toutes les versions de PowerShell)
if ($service -and $PSCmdlet.ShouldProcess($ServiceName, 'Suppression du service Windows')) {
    & sc.exe delete $ServiceName | Out-Null
}

# 4. Règle de pare-feu créée par -OpenFirewall
$rule = Get-NetFirewallRule -DisplayName 'Comptage incident' -ErrorAction SilentlyContinue
if ($rule -and $PSCmdlet.ShouldProcess('Comptage incident', 'Suppression de la règle de pare-feu')) {
    $rule | Remove-NetFirewallRule
}

# 5. Fichiers de l'application
if ((Test-Path $InstallPath) -and $PSCmdlet.ShouldProcess($InstallPath, 'Suppression du dossier de l''application')) {
    Remove-Item -Path $InstallPath -Recurse -Force
}

Write-Host ''
Write-Host 'Comptage incident désinstallé.' -ForegroundColor Green
Write-Host 'Composant conservé (partagé) : runtime ASP.NET Core 10.'
Write-Host 'Si des SPN avaient été déclarés pour un compte de service : setspn -D HTTP/<serveur> <compte>'
