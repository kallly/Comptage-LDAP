#Requires -RunAsAdministrator
<#
=====================================================================================================
 Install-Service.ps1 : installation OU mise à jour de « Comptage incident » en mode AUTONOME (sans IIS)
=====================================================================================================
.SYNOPSIS
    Installe l'application comme service Windows, qui écoute directement sur un port HTTP (serveur Kestrel).

.DESCRIPTION
    Étapes réalisées :
      1. Publie l'application si le SDK .NET est présent (sinon utilise un dossier déjà publié).
      2. Arrête le service s'il existe (mise à jour).
      3. Copie les fichiers EN CONSERVANT la base (App_Data) et la configuration (appsettings.json).
      4. Première installation uniquement : écrit le port d'écoute dans appsettings.json.
      5. Crée le service Windows (démarrage automatique, redémarrage automatique en cas d'échec).
      6. Applique les droits NTFS : modification sur App_Data uniquement pour le compte du service.
      7. Ouvre le port dans le pare-feu Windows (profil Domaine uniquement) si -OpenFirewall est indiqué.
      8. Démarre le service.

    L'application gère elle-même l'authentification Windows (Kerberos/NTLM) et interroge l'annuaire avec
    le compte du service :
      - LocalSystem (défaut) ou NetworkService → compte ordinateur DOMAINE\SERVEUR$ ;
      - compte de domaine (-ServiceAccount DOMAINE\compte) → ce compte (déclarer le SPN, voir la doc).

    Pour un simple essai SANS service : lancer ComptageIncident.exe depuis le dossier publié, dans une
    session ouverte. L'annuaire est alors interrogé avec le compte de l'utilisateur connecté.

    Documentation : docs/08-installation-autonome.md   Désinstallation : deploy/Uninstall-Service.ps1

.PARAMETER ServiceName     Nom technique du service Windows.
.PARAMETER InstallPath     Dossier d'installation.
.PARAMETER Port            Port HTTP d'écoute (première installation ; ensuite, modifier appsettings.json).
.PARAMETER ServiceAccount  Vide = LocalSystem ; 'NetworkService' ; ou compte de domaine 'DOMAINE\compte'.
.PARAMETER OpenFirewall    Crée une règle de pare-feu entrante pour le port, profil Domaine uniquement.
.PARAMETER PublishPath     Dossier contenant l'application publiée.

.EXAMPLE
    .\Install-Service.ps1 -Port 5080 -OpenFirewall
.EXAMPLE
    .\Install-Service.ps1 -Port 5080 -ServiceAccount 'ENTREPRISE\svc-comptage' -OpenFirewall
=====================================================================================================
#>
param(
    [string] $ServiceName    = 'ComptageIncident',
    [string] $InstallPath    = 'C:\Apps\ComptageIncident',
    [int]    $Port           = 5080,
    [string] $ServiceAccount = '',
    [switch] $OpenFirewall,
    [string] $PublishPath    = (Join-Path $PSScriptRoot '..\publish\service')
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\ComptageIncident\ComptageIncident.csproj'

# Le runtime ASP.NET Core 10 doit être installé (inclus dans le Hosting Bundle).
$runtimes = & "$env:ProgramFiles\dotnet\dotnet.exe" --list-runtimes 2>$null
if (-not ($runtimes -match '^Microsoft\.AspNetCore\.App 10\.')) {
    throw "Le runtime ASP.NET Core 10 est absent. Installez le 'ASP.NET Core 10 Hosting Bundle' (https://dotnet.microsoft.com/download/dotnet/10.0) puis relancez ce script."
}

# -------------------------------------------------------------------------------------------------
# 1. Publication (voir Install-IIS.ps1 pour le détail des options)
# -------------------------------------------------------------------------------------------------
if ((Get-Command dotnet -ErrorAction SilentlyContinue) -and (& dotnet --list-sdks 2>$null) -and (Test-Path $project)) {
    Write-Host 'Publication de l''application...' -ForegroundColor Cyan
    & dotnet publish $project -c Release -r win-x64 --self-contained false -o $PublishPath
    if ($LASTEXITCODE -ne 0) { throw 'Échec de dotnet publish.' }
}
$exe = Join-Path $PublishPath 'ComptageIncident.exe'
if (-not (Test-Path $exe)) { throw "Dossier publié introuvable : $PublishPath" }

# -------------------------------------------------------------------------------------------------
# 2. Arrêt du service existant (mise à jour) pour libérer les fichiers
# -------------------------------------------------------------------------------------------------
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') { Stop-Service $ServiceName -Force; Start-Sleep 2 }

# -------------------------------------------------------------------------------------------------
# 3. Copie des fichiers
# -------------------------------------------------------------------------------------------------
#   /XD App_Data          : la base de données n'est jamais écrasée ;
#   /XF web.config        : inutile hors IIS ;
#   /XF appsettings.json  : configuration conservée si elle existe déjà.
New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
$excludeFiles = @('web.config')
$keepSettings = Test-Path (Join-Path $InstallPath 'appsettings.json')
if ($keepSettings) { $excludeFiles += 'appsettings.json' }
robocopy $PublishPath $InstallPath /E /NFL /NDL /NJH /NJS /XD App_Data /XF $excludeFiles | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Échec de la copie (robocopy code $LASTEXITCODE)." }
$dataDir = Join-Path $InstallPath 'App_Data'
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null

# -------------------------------------------------------------------------------------------------
# 4. Port d'écoute (première installation uniquement)
# -------------------------------------------------------------------------------------------------
# appsettings.json contient des commentaires (acceptés par ASP.NET Core mais pas par ConvertFrom-Json de
# Windows PowerShell 5.1) : on remplace donc simplement le port par une expression régulière.
$settingsFile = Join-Path $InstallPath 'appsettings.json'
if (-not $keepSettings) {
    $content = Get-Content $settingsFile -Raw -Encoding UTF8
    $content = $content -replace '"Url":\s*"http://0\.0\.0\.0:\d+"', "`"Url`": `"http://0.0.0.0:$Port`""
    [System.IO.File]::WriteAllText($settingsFile, $content, (New-Object System.Text.UTF8Encoding($false)))
}

# -------------------------------------------------------------------------------------------------
# 5. Service Windows
# -------------------------------------------------------------------------------------------------
$binPath = "`"$(Join-Path $InstallPath 'ComptageIncident.exe')`""
# Compte qui recevra les droits sur App_Data (nom exact attendu par icacls).
$aclAccount = 'NT AUTHORITY\SYSTEM'
if (-not $existing) {
    $params = @{
        Name           = $ServiceName
        BinaryPathName = $binPath
        DisplayName    = 'Comptage incident'
        Description    = 'Comptage des personnes présentes lors d''un incident (authentification Windows, annuaire AD).'
        StartupType    = 'Automatic'
    }
    if ($ServiceAccount -and $ServiceAccount -ne 'NetworkService') {
        # Compte de domaine : le mot de passe est demandé de façon interactive et confié au gestionnaire de
        # services Windows (il n'est écrit nulle part par ce script).
        $params.Credential = Get-Credential -UserName $ServiceAccount -Message "Mot de passe du compte de service $ServiceAccount"
    }
    New-Service @params | Out-Null
    if ($ServiceAccount -eq 'NetworkService') {
        & sc.exe config $ServiceName obj= 'NT AUTHORITY\NetworkService' password= '' | Out-Null
    }
    # En cas d'arrêt inattendu : redémarrage après 5 s, puis 5 s, puis 30 s ; compteur remis à zéro après 24 h.
    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
}
if ($ServiceAccount -eq 'NetworkService') { $aclAccount = 'NT AUTHORITY\NETWORK SERVICE' }
elseif ($ServiceAccount) { $aclAccount = $ServiceAccount }

# -------------------------------------------------------------------------------------------------
# 6. Droits NTFS
# -------------------------------------------------------------------------------------------------
# Lecture/exécution sur l'application (déjà acquise pour SYSTEM), modification sur App_Data uniquement.
& icacls $InstallPath /grant "${aclAccount}:(OI)(CI)RX" /T /Q | Out-Null
& icacls $dataDir /grant "${aclAccount}:(OI)(CI)M" /T /Q | Out-Null

# -------------------------------------------------------------------------------------------------
# 7. Pare-feu (facultatif) : profil Domaine uniquement (pas d'ouverture sur les réseaux publics/privés)
# -------------------------------------------------------------------------------------------------
if ($OpenFirewall) {
    if (-not (Get-NetFirewallRule -DisplayName 'Comptage incident' -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName 'Comptage incident' -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow -Profile Domain | Out-Null
    }
}

# -------------------------------------------------------------------------------------------------
# 8. Démarrage et récapitulatif
# -------------------------------------------------------------------------------------------------
Start-Service $ServiceName
Write-Host ''
Write-Host "Service '$ServiceName' démarré : http://$env:COMPUTERNAME`:$Port" -ForegroundColor Green
Write-Host "Base de données : $dataDir\comptage.db (à inclure dans les sauvegardes)"
if ($ServiceAccount -and $ServiceAccount -ne 'NetworkService') {
    # Avec un compte de domaine, Kerberos a besoin que le SPN HTTP/<nom du serveur> soit associé à CE compte
    # (et non au compte ordinateur). Sans cela, l'authentification passe en NTLM.
    $cs = Get-CimInstance Win32_ComputerSystem
    Write-Host 'Compte de domaine dédié : déclarez les SPN pour Kerberos (administrateur du domaine) :' -ForegroundColor Yellow
    Write-Host "    setspn -S HTTP/$env:COMPUTERNAME $ServiceAccount"
    Write-Host "    setspn -S HTTP/$($cs.DNSHostName).$($cs.Domain) $ServiceAccount"
    Write-Host 'Le compte doit aussi disposer du droit « Ouvrir une session en tant que service ».' -ForegroundColor Yellow
}
