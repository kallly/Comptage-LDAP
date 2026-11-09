#Requires -RunAsAdministrator
<#
=====================================================================================================
 Uninstall-IIS.ps1 : désinstallation de « Comptage incident » hébergé sous IIS
=====================================================================================================
.SYNOPSIS
    Supprime le site et le pool IIS créés par Install-IIS.ps1, en SAUVEGARDANT la base de données d'abord.

.DESCRIPTION
    Étapes :
      1. Arrête le site et le pool (libère le fichier de base).
      2. Copie App_Data (base SQLite + fichiers -wal/-shm) et appsettings.json dans un dossier de
         sauvegarde horodaté. Cette étape n'est sautée QUE si -NoBackup est indiqué explicitement.
      3. Supprime le site puis le pool d'applications.
      4. Supprime le dossier de l'application.
      5. Retire le déverrouillage des sections d'authentification posé pour ce site.

    Ne désinstalle PAS les composants partagés (rôle IIS, Hosting Bundle .NET), qui peuvent servir à
    d'autres applications. Ne supprime pas le SPN éventuellement déclaré (voir docs/12-desinstallation.md).

    -WhatIf affiche ce qui serait fait, sans rien modifier.

.PARAMETER SiteName      Nom du site IIS.
.PARAMETER AppPoolName   Nom du pool d'applications.
.PARAMETER PhysicalPath  Dossier d'installation à supprimer.
.PARAMETER BackupPath    Dossier où sauvegarder les données avant suppression.
.PARAMETER NoBackup      Ne pas sauvegarder (les données seront DÉFINITIVEMENT perdues).

.EXAMPLE
    .\Uninstall-IIS.ps1
.EXAMPLE
    .\Uninstall-IIS.ps1 -WhatIf
=====================================================================================================
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string] $SiteName     = 'ComptageIncident',
    [string] $AppPoolName  = 'ComptageIncident',
    [string] $PhysicalPath = 'C:\inetpub\ComptageIncident',
    [string] $BackupPath   = ("C:\Sauvegardes\ComptageIncident-" + (Get-Date -Format 'yyyyMMdd-HHmmss')),
    [switch] $NoBackup
)

$ErrorActionPreference = 'Stop'
Import-Module WebAdministration

# 1. Arrêt (le fichier SQLite est verrouillé tant que le pool tourne). Non exécuté en mode -WhatIf.
if ($PSCmdlet.ShouldProcess("$SiteName / $AppPoolName", 'Arrêt du site et du pool IIS')) {
    if (Test-Path "IIS:\Sites\$SiteName") { Stop-Website -Name $SiteName -ErrorAction SilentlyContinue }
    if ((Test-Path "IIS:\AppPools\$AppPoolName") -and (Get-WebAppPoolState -Name $AppPoolName).Value -ne 'Stopped') {
        Stop-WebAppPool -Name $AppPoolName
        Start-Sleep 2
    }
}

# 2. Sauvegarde des données
$dataDir = Join-Path $PhysicalPath 'App_Data'
if (-not $NoBackup -and (Test-Path $dataDir)) {
    if ($PSCmdlet.ShouldProcess($BackupPath, "Sauvegarde de $dataDir")) {
        New-Item -ItemType Directory -Force -Path $BackupPath | Out-Null
        Copy-Item -Path $dataDir -Destination $BackupPath -Recurse -Force
        $settings = Join-Path $PhysicalPath 'appsettings.json'
        if (Test-Path $settings) { Copy-Item $settings $BackupPath -Force }
        Write-Host "Données sauvegardées dans : $BackupPath" -ForegroundColor Green
    }
}

# 3. Site et pool
if ((Test-Path "IIS:\Sites\$SiteName") -and $PSCmdlet.ShouldProcess($SiteName, 'Suppression du site IIS')) {
    Remove-Website -Name $SiteName
}
if ((Test-Path "IIS:\AppPools\$AppPoolName") -and $PSCmdlet.ShouldProcess($AppPoolName, 'Suppression du pool IIS')) {
    Remove-WebAppPool -Name $AppPoolName
}

# 4. Fichiers de l'application
if ((Test-Path $PhysicalPath) -and $PSCmdlet.ShouldProcess($PhysicalPath, 'Suppression du dossier de l''application')) {
    Remove-Item -Path $PhysicalPath -Recurse -Force
}

# 5. Nettoyage de la configuration IIS propre à ce site (déverrouillage des sections d'authentification).
if ($PSCmdlet.ShouldProcess($SiteName, 'Retrait de la configuration d''authentification propre au site')) {
    foreach ($section in 'windowsAuthentication', 'anonymousAuthentication') {
        $filter = "system.webServer/security/authentication/$section"
        Clear-WebConfiguration -Filter $filter -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $SiteName -ErrorAction SilentlyContinue
    }
}

Write-Host ''
Write-Host 'Comptage incident désinstallé.' -ForegroundColor Green
Write-Host 'Composants conservés (partagés) : rôle IIS, ASP.NET Core Hosting Bundle.'
Write-Host 'Si un SPN HTTP/<alias> avait été déclaré, il peut être retiré : setspn -D HTTP/<alias> <SERVEUR>'
