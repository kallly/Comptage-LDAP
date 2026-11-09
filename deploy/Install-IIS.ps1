#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installe (ou met à jour) l'application Comptage incident sous IIS avec authentification Windows.

.DESCRIPTION
    - Publie l'application si le SDK .NET est présent (sinon utilise le dossier -PublishPath déjà publié)
    - Copie les fichiers en conservant App_Data (base SQLite) et appsettings.json existants
    - Crée le pool d'applications (No Managed Code, ApplicationPoolIdentity, toujours démarré)
    - Crée le site, active l'authentification Windows et désactive l'anonyme
    - Donne au pool les droits d'écriture sur App_Data

    L'annuaire est interrogé avec l'identité du pool : sur le réseau, c'est le COMPTE ORDINATEUR
    du serveur (DOMAINE\SERVEUR$) qui lit l'AD. Aucun mot de passe n'est stocké.

    Prérequis : rôle IIS + "Authentification Windows" + ASP.NET Core 8 Hosting Bundle.

.EXAMPLE
    .\Install-IIS.ps1 -SiteName ComptageIncident -Port 80 -HostName comptage.entreprise.local
#>
param(
    [string] $SiteName     = 'ComptageIncident',
    [string] $AppPoolName  = 'ComptageIncident',
    [string] $PhysicalPath = 'C:\inetpub\ComptageIncident',
    [int]    $Port         = 80,
    [string] $HostName     = '',
    [string] $PublishPath  = (Join-Path $PSScriptRoot '..\publish\iis')
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\ComptageIncident\ComptageIncident.csproj'

# ------------------------------------------------------------------ Prérequis
if (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue) {
    $features = 'Web-Server', 'Web-Windows-Auth'
    $missing = $features | Where-Object { -not (Get-WindowsFeature $_).Installed }
    if ($missing) {
        Write-Host "Installation des fonctionnalités IIS : $($missing -join ', ')" -ForegroundColor Cyan
        Install-WindowsFeature $missing -IncludeManagementTools | Out-Null
    }
}
Import-Module WebAdministration

$ancm = Get-WebGlobalModule -Name 'AspNetCoreModuleV2' -ErrorAction SilentlyContinue
if (-not $ancm) {
    throw "Le module ASP.NET Core (AspNetCoreModuleV2) est absent. Installez le 'ASP.NET Core 8 Hosting Bundle' puis relancez ce script."
}

# ------------------------------------------------------------------ Publication
if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    $sdks = & dotnet --list-sdks 2>$null
    if ($sdks -and (Test-Path $project)) {
        Write-Host 'Publication de l''application...' -ForegroundColor Cyan
        & dotnet publish $project -c Release -r win-x64 --self-contained false -o $PublishPath
        if ($LASTEXITCODE -ne 0) { throw 'Échec de dotnet publish.' }
    }
}
if (-not (Test-Path (Join-Path $PublishPath 'ComptageIncident.dll'))) {
    throw "Dossier publié introuvable : $PublishPath"
}

# ------------------------------------------------------------------ Copie des fichiers
$isUpdate = Test-Path (Join-Path $PhysicalPath 'ComptageIncident.dll')
New-Item -ItemType Directory -Force -Path $PhysicalPath | Out-Null
if ($isUpdate -and (Test-Path "IIS:\AppPools\$AppPoolName")) { Stop-WebAppPool -Name $AppPoolName -ErrorAction SilentlyContinue; Start-Sleep 2 }

$exclude = @('App_Data')
$excludeFiles = @()
if (Test-Path (Join-Path $PhysicalPath 'appsettings.json')) { $excludeFiles += 'appsettings.json' }   # garde la configuration locale
robocopy $PublishPath $PhysicalPath /E /NFL /NDL /NJH /NJS /XD $exclude /XF $excludeFiles | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Échec de la copie (robocopy code $LASTEXITCODE)." }

$dataDir = Join-Path $PhysicalPath 'App_Data'
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null

# ------------------------------------------------------------------ Pool d'applications
if (-not (Test-Path "IIS:\AppPools\$AppPoolName")) {
    New-WebAppPool -Name $AppPoolName | Out-Null
}
$pool = "IIS:\AppPools\$AppPoolName"
Set-ItemProperty $pool -Name managedRuntimeVersion -Value ''
Set-ItemProperty $pool -Name processModel.identityType -Value 'ApplicationPoolIdentity'
Set-ItemProperty $pool -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)   # garde la purge de corbeille active
Set-ItemProperty $pool -Name startMode -Value 'AlwaysRunning'

# ------------------------------------------------------------------ Site
if (-not (Test-Path "IIS:\Sites\$SiteName")) {
    New-Website -Name $SiteName -PhysicalPath $PhysicalPath -ApplicationPool $AppPoolName -Port $Port -HostHeader $HostName | Out-Null
} else {
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name physicalPath -Value $PhysicalPath
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationPool -Value $AppPoolName
}

# Authentification : Windows ON, anonyme OFF (sections déverrouillées pour ce site afin que le web.config soit accepté)
foreach ($section in 'windowsAuthentication', 'anonymousAuthentication') {
    $filter = "system.webServer/security/authentication/$section"
    Set-WebConfiguration -Filter $filter -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $SiteName -Metadata overrideMode -Value Allow
}
Set-WebConfigurationProperty -Filter 'system.webServer/security/authentication/windowsAuthentication' -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $SiteName -Name enabled -Value $true
Set-WebConfigurationProperty -Filter 'system.webServer/security/authentication/anonymousAuthentication' -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $SiteName -Name enabled -Value $false

# ------------------------------------------------------------------ Droits
$identity = "IIS AppPool\$AppPoolName"
& icacls $PhysicalPath /grant "${identity}:(OI)(CI)RX" /T /Q | Out-Null
& icacls $dataDir /grant "${identity}:(OI)(CI)M" /T /Q | Out-Null

Start-WebAppPool -Name $AppPoolName -ErrorAction SilentlyContinue
Start-Website -Name $SiteName -ErrorAction SilentlyContinue

$url = if ($HostName) { "http://$HostName" + $(if ($Port -ne 80) { ":$Port" }) } else { "http://$env:COMPUTERNAME" + $(if ($Port -ne 80) { ":$Port" }) }
Write-Host ''
Write-Host "Comptage incident installé : $url" -ForegroundColor Green
Write-Host "Base de données : $dataDir\comptage.db (à inclure dans les sauvegardes)"
if ($HostName) {
    Write-Host "Si '$HostName' est un alias DNS, déclarez le SPN pour Kerberos :" -ForegroundColor Yellow
    Write-Host "    setspn -S HTTP/$HostName $env:COMPUTERNAME"
}
