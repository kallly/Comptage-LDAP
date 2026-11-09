#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installe (ou met à jour) Comptage incident en mode autonome, comme service Windows (sans IIS).

.DESCRIPTION
    L'application écoute directement via Kestrel et gère l'authentification Windows (Kerberos/NTLM).
    L'annuaire est interrogé avec le compte du service :
      - LocalSystem (défaut) ou NetworkService : compte ordinateur DOMAINE\SERVEUR$
      - un compte de domaine (-ServiceAccount) : ce compte (pensez au SPN, voir README)

    Prérequis : ASP.NET Core 8 Runtime (ou Hosting Bundle) sur le serveur.

    Pour un simple essai sans service, lancez directement ComptageIncident.exe dans une session :
    l'annuaire est alors interrogé avec le compte de l'utilisateur connecté.

.EXAMPLE
    .\Install-Service.ps1 -Port 5080 -OpenFirewall
#>
param(
    [string] $ServiceName    = 'ComptageIncident',
    [string] $InstallPath    = 'C:\Apps\ComptageIncident',
    [int]    $Port           = 5080,
    [string] $ServiceAccount = '',          # vide = LocalSystem ; ex. 'NT AUTHORITY\NetworkService' ou 'DOMAINE\svc-comptage'
    [switch] $OpenFirewall,
    [string] $PublishPath    = (Join-Path $PSScriptRoot '..\publish\service')
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\ComptageIncident\ComptageIncident.csproj'

# ------------------------------------------------------------------ Publication
if ((Get-Command dotnet -ErrorAction SilentlyContinue) -and (& dotnet --list-sdks 2>$null) -and (Test-Path $project)) {
    Write-Host 'Publication de l''application...' -ForegroundColor Cyan
    & dotnet publish $project -c Release -r win-x64 --self-contained false -o $PublishPath
    if ($LASTEXITCODE -ne 0) { throw 'Échec de dotnet publish.' }
}
$exe = Join-Path $PublishPath 'ComptageIncident.exe'
if (-not (Test-Path $exe)) { throw "Dossier publié introuvable : $PublishPath" }

# ------------------------------------------------------------------ Arrêt du service existant
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') { Stop-Service $ServiceName -Force; Start-Sleep 2 }

# ------------------------------------------------------------------ Copie (App_Data et appsettings.json conservés)
New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
$excludeFiles = @('web.config')
$keepSettings = Test-Path (Join-Path $InstallPath 'appsettings.json')
if ($keepSettings) { $excludeFiles += 'appsettings.json' }
robocopy $PublishPath $InstallPath /E /NFL /NDL /NJH /NJS /XD App_Data /XF $excludeFiles | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Échec de la copie (robocopy code $LASTEXITCODE)." }
$dataDir = Join-Path $InstallPath 'App_Data'
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null

# Port d'écoute dans appsettings.json (première installation uniquement)
$settingsFile = Join-Path $InstallPath 'appsettings.json'
if (-not $keepSettings) {
    $settings = Get-Content $settingsFile -Raw | ConvertFrom-Json
    $settings.Kestrel.Endpoints.Http.Url = "http://0.0.0.0:$Port"
    $settings | ConvertTo-Json -Depth 10 | Set-Content $settingsFile -Encoding UTF8
}

# ------------------------------------------------------------------ Service
$binPath = "`"$(Join-Path $InstallPath 'ComptageIncident.exe')`""
if (-not $existing) {
    $params = @{
        Name           = $ServiceName
        BinaryPathName = $binPath
        DisplayName    = 'Comptage incident'
        Description    = 'Comptage des personnes présentes lors d''un incident (authentification Windows, annuaire AD).'
        StartupType    = 'Automatic'
    }
    if ($ServiceAccount) { $params.Credential = Get-Credential -UserName $ServiceAccount -Message "Mot de passe du compte de service $ServiceAccount" }
    New-Service @params | Out-Null
    # Redémarrage automatique en cas d'arrêt inattendu
    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
}

# Droits d'écriture sur App_Data pour le compte du service
$account = if ($ServiceAccount) { $ServiceAccount } else { 'NT AUTHORITY\SYSTEM' }
& icacls $dataDir /grant "${account}:(OI)(CI)M" /T /Q | Out-Null

if ($OpenFirewall) {
    if (-not (Get-NetFirewallRule -DisplayName 'Comptage incident' -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName 'Comptage incident' -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow -Profile Domain | Out-Null
    }
}

Start-Service $ServiceName
Write-Host ''
Write-Host "Service '$ServiceName' démarré : http://$env:COMPUTERNAME`:$Port" -ForegroundColor Green
Write-Host "Base de données : $dataDir\comptage.db (à inclure dans les sauvegardes)"
if ($ServiceAccount -and $ServiceAccount -notlike 'NT AUTHORITY\*') {
    Write-Host 'Compte de domaine dédié : déclarez le SPN pour Kerberos :' -ForegroundColor Yellow
    Write-Host "    setspn -S HTTP/$env:COMPUTERNAME $ServiceAccount"
    Write-Host "    setspn -S HTTP/$((Get-CimInstance Win32_ComputerSystem).DNSHostName).$((Get-CimInstance Win32_ComputerSystem).Domain) $ServiceAccount"
}
