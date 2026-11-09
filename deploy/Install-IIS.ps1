#Requires -RunAsAdministrator
<#
=====================================================================================================
 Install-IIS.ps1 : installation OU mise à jour de « Comptage incident » sous IIS
=====================================================================================================
.SYNOPSIS
    Installe (ou met à jour) l'application sous IIS avec authentification Windows obligatoire.

.DESCRIPTION
    Étapes réalisées (chacune est détaillée dans le corps du script) :
      1. Vérifie / installe les prérequis IIS (serveur Web + Authentification Windows) et vérifie
         la présence du module ASP.NET Core (Hosting Bundle .NET 10).
      2. Publie l'application si le SDK .NET est présent (sinon utilise un dossier déjà publié).
      3. Copie les fichiers dans le dossier du site EN CONSERVANT la base (App_Data) et la configuration
         locale (appsettings.json) lors d'une mise à jour.
      4. Crée / configure le pool d'applications : « No Managed Code », identité ApplicationPoolIdentity,
         toujours démarré, sans arrêt pour inactivité.
      5. Crée / configure le site : authentification Windows ACTIVÉE, anonyme DÉSACTIVÉE.
      6. Applique les droits NTFS minimaux : lecture sur l'application, modification sur App_Data uniquement.

    Le script est idempotent : il peut être relancé pour une mise à jour sans perte de données.

    Annuaire : l'application interroge l'AD avec l'identité du pool. Sur le réseau, c'est le COMPTE
    ORDINATEUR du serveur (DOMAINE\SERVEUR$) qui lit l'annuaire. Aucun mot de passe n'est stocké.

    Documentation : docs/07-installation-iis.md   Désinstallation : deploy/Uninstall-IIS.ps1

.PARAMETER SiteName      Nom du site IIS à créer/mettre à jour.
.PARAMETER AppPoolName   Nom du pool d'applications (aussi utilisé pour les droits NTFS : IIS AppPool\<nom>).
.PARAMETER PhysicalPath  Dossier d'installation sur le serveur.
.PARAMETER Port          Port HTTP du site (80 par défaut).
.PARAMETER HostName      Nom d'hôte (en-tête Host) du site, ex. comptage.entreprise.local. Vide = toutes les requêtes du port.
.PARAMETER PublishPath   Dossier contenant l'application publiée (créé par le script si le SDK est présent).

.EXAMPLE
    .\Install-IIS.ps1 -SiteName ComptageIncident -Port 80 -HostName comptage.entreprise.local
=====================================================================================================
#>
param(
    [string] $SiteName     = 'ComptageIncident',
    [string] $AppPoolName  = 'ComptageIncident',
    [string] $PhysicalPath = 'C:\inetpub\ComptageIncident',
    [int]    $Port         = 80,
    [string] $HostName     = '',
    [string] $PublishPath  = (Join-Path $PSScriptRoot '..\publish\iis')
)

# Toute erreur non gérée arrête le script immédiatement (pas d'installation à moitié faite en silence).
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\ComptageIncident\ComptageIncident.csproj'

# -------------------------------------------------------------------------------------------------
# 1. Prérequis
# -------------------------------------------------------------------------------------------------
# Sur Windows Server, installe si besoin le rôle IIS et la fonctionnalité « Authentification Windows ».
# (Sur un poste Windows client, ces fonctionnalités s'activent via « Fonctionnalités de Windows ».)
if (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue) {
    $features = 'Web-Server', 'Web-Windows-Auth'
    $missing = $features | Where-Object { -not (Get-WindowsFeature $_).Installed }
    if ($missing) {
        Write-Host "Installation des fonctionnalités IIS : $($missing -join ', ')" -ForegroundColor Cyan
        Install-WindowsFeature $missing -IncludeManagementTools | Out-Null
    }
}
# Module PowerShell d'administration d'IIS (lecteur IIS:\, New-WebAppPool, Set-WebConfiguration…).
Import-Module WebAdministration

# Le module ASP.NET Core (AspNetCoreModuleV2) est installé par le « ASP.NET Core 10 Hosting Bundle ».
# Sans lui, IIS ne sait pas exécuter l'application.
$ancm = Get-WebGlobalModule -Name 'AspNetCoreModuleV2' -ErrorAction SilentlyContinue
if (-not $ancm) {
    throw "Le module ASP.NET Core (AspNetCoreModuleV2) est absent. Installez le 'ASP.NET Core 10 Hosting Bundle' puis relancez ce script."
}

# Le runtime ASP.NET Core 10 doit être installé (inclus dans le Hosting Bundle).
$runtimes = & "$env:ProgramFiles\dotnet\dotnet.exe" --list-runtimes 2>$null
if (-not ($runtimes -match '^Microsoft\.AspNetCore\.App 10\.')) {
    throw "Le runtime ASP.NET Core 10 est absent. Installez le 'ASP.NET Core 10 Hosting Bundle' (https://dotnet.microsoft.com/download/dotnet/10.0) puis relancez ce script."
}

# -------------------------------------------------------------------------------------------------
# 2. Publication
# -------------------------------------------------------------------------------------------------
# Si le SDK .NET et le code source sont présents, on (re)publie l'application :
#   -c Release            : version optimisée ;
#   -r win-x64            : produit ComptageIncident.exe et les bibliothèques natives Windows (SQLite) ;
#   --self-contained false: utilise le runtime .NET installé par le Hosting Bundle (mises à jour de
#                           sécurité via Windows Update / Microsoft Update).
# Sinon, on suppose que $PublishPath contient déjà une version publiée (copiée depuis un autre poste).
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

# -------------------------------------------------------------------------------------------------
# 3. Copie des fichiers
# -------------------------------------------------------------------------------------------------
$isUpdate = Test-Path (Join-Path $PhysicalPath 'ComptageIncident.dll')
New-Item -ItemType Directory -Force -Path $PhysicalPath | Out-Null

# Mise à jour : on arrête le pool pour libérer les fichiers (DLL et base SQLite) avant de les remplacer.
if ($isUpdate -and (Test-Path "IIS:\AppPools\$AppPoolName")) { Stop-WebAppPool -Name $AppPoolName -ErrorAction SilentlyContinue; Start-Sleep 2 }

# robocopy /E copie l'arborescence. Exclusions :
#   /XD App_Data          : la base de données en place n'est JAMAIS écrasée ;
#   /XF appsettings.json  : la configuration locale est conservée si elle existe déjà (mise à jour).
$robocopyArgs = @($PublishPath, $PhysicalPath, '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/XD', 'App_Data')
if (Test-Path (Join-Path $PhysicalPath 'appsettings.json')) { $robocopyArgs += @('/XF', 'appsettings.json') }
robocopy @robocopyArgs | Out-Null
# Codes robocopy : 0 à 7 = succès (avec ou sans fichiers copiés), 8 et plus = erreur.
if ($LASTEXITCODE -ge 8) { throw "Échec de la copie (robocopy code $LASTEXITCODE)." }

$dataDir = Join-Path $PhysicalPath 'App_Data'
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null

# -------------------------------------------------------------------------------------------------
# 4. Pool d'applications
# -------------------------------------------------------------------------------------------------
if (-not (Test-Path "IIS:\AppPools\$AppPoolName")) {
    New-WebAppPool -Name $AppPoolName | Out-Null
}
$pool = "IIS:\AppPools\$AppPoolName"
# « No Managed Code » : ASP.NET Core n'utilise pas le CLR .NET Framework d'IIS.
Set-ItemProperty $pool -Name managedRuntimeVersion -Value ''
# Identité virtuelle propre au pool (IIS AppPool\<nom>) : compte sans mot de passe, sans droits particuliers,
# qui se présente sur le réseau (donc à l'AD) comme le compte ordinateur du serveur.
Set-ItemProperty $pool -Name processModel.identityType -Value 'ApplicationPoolIdentity'
# Pas d'arrêt après 20 min d'inactivité : l'application reste prête à l'emploi en cas d'incident et la purge
# automatique de la corbeille continue de s'exécuter.
Set-ItemProperty $pool -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
Set-ItemProperty $pool -Name startMode -Value 'AlwaysRunning'

# -------------------------------------------------------------------------------------------------
# 5. Site et authentification
# -------------------------------------------------------------------------------------------------
if (-not (Test-Path "IIS:\Sites\$SiteName")) {
    New-Website -Name $SiteName -PhysicalPath $PhysicalPath -ApplicationPool $AppPoolName -Port $Port -HostHeader $HostName | Out-Null
} else {
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name physicalPath -Value $PhysicalPath
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationPool -Value $AppPoolName
}

# Par défaut, IIS verrouille les sections d'authentification au niveau du serveur, ce qui provoquerait une
# erreur 500.19 à cause du web.config de l'application. On les déverrouille UNIQUEMENT pour ce site
# (overrideMode=Allow sur <location path="SiteName">), sans modifier les autres sites du serveur.
foreach ($section in 'windowsAuthentication', 'anonymousAuthentication') {
    $filter = "system.webServer/security/authentication/$section"
    Set-WebConfiguration -Filter $filter -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $SiteName -Metadata overrideMode -Value Allow
}
# Authentification Windows activée, anonyme désactivée (même réglage que dans web.config : double garantie).
Set-WebConfigurationProperty -Filter 'system.webServer/security/authentication/windowsAuthentication' -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $SiteName -Name enabled -Value $true
Set-WebConfigurationProperty -Filter 'system.webServer/security/authentication/anonymousAuthentication' -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $SiteName -Name enabled -Value $false

# -------------------------------------------------------------------------------------------------
# 6. Droits NTFS (principe du moindre privilège)
# -------------------------------------------------------------------------------------------------
#   (OI)(CI) = héritage sur les fichiers et sous-dossiers ; RX = lecture + exécution ; M = modification.
#   Le pool peut LIRE l'application mais ne peut ÉCRIRE que dans App_Data (base SQLite et ses fichiers -wal/-shm).
$identity = "IIS AppPool\$AppPoolName"
& icacls $PhysicalPath /grant "${identity}:(OI)(CI)RX" /T /Q | Out-Null
& icacls $dataDir /grant "${identity}:(OI)(CI)M" /T /Q | Out-Null

Start-WebAppPool -Name $AppPoolName -ErrorAction SilentlyContinue
Start-Website -Name $SiteName -ErrorAction SilentlyContinue

# -------------------------------------------------------------------------------------------------
# Récapitulatif
# -------------------------------------------------------------------------------------------------
$url = if ($HostName) { "http://$HostName" + $(if ($Port -ne 80) { ":$Port" }) } else { "http://$env:COMPUTERNAME" + $(if ($Port -ne 80) { ":$Port" }) }
Write-Host ''
Write-Host "Comptage incident installé : $url" -ForegroundColor Green
Write-Host "Base de données : $dataDir\comptage.db (à inclure dans les sauvegardes)"
if ($HostName) {
    # Kerberos identifie le service par son SPN « HTTP/<nom utilisé dans le navigateur> ». Pour le nom du
    # serveur, le SPN HOST/<serveur> existant suffit ; pour un alias DNS, il faut le déclarer explicitement,
    # sinon l'authentification se fait en NTLM (fonctionnelle mais moins robuste).
    Write-Host "Si '$HostName' est un alias DNS, déclarez le SPN pour Kerberos :" -ForegroundColor Yellow
    Write-Host "    setspn -S HTTP/$HostName $env:COMPUTERNAME"
}
