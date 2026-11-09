# Comptage incident

Application Web interne pour **compter les personnes présentes** lors d'un incident sur site (incendie, fuite de produit chimique, exercice d'évacuation…).

- **Authentification Windows automatique** : l'utilisateur est reconnu par sa session, sans saisie (Kerberos/NTLM).
- **Recherche dans l'annuaire Active Directory** par nom ou prénom :
  - Les suggestions apparaissent à partir de **4 caractères**.
  - Un clic ouvre une **popup de confirmation**, puis la personne est ajoutée.
- **Visiteurs / personnes hors annuaire** : saisie manuelle du nom complet, et de la société en option.
- **Deux listes séparées** (Internes / Visiteurs) :
  - triées par **ordre alphabétique**
  - chacune avec son **total**, plus le total général
- **Suppression d'une personne** avec popup de confirmation. Elle reste dans « Personnes retirées » et peut être **réintégrée** en un clic.
- **Plusieurs incidents** conservés dans une base **SQLite locale**. Il n'y a **aucun bouton « tout effacer »**.
- **Suppression d'un incident = mise en corbeille 24 h**. Pendant ce délai, il est restaurable en un clic. Ensuite, il est purgé automatiquement.
- **Clôture d'un incident** : les listes passent en lecture seule. L'incident peut être rouvert.
- **Journal d'audit** : qui a ajouté ou retiré qui, et quand.
- **Impression** et **export CSV** (compatible Excel), utiles en cas de coupure réseau.
- **Rafraîchissement automatique** toutes les 5 s : plusieurs agents peuvent compter en même temps sur le même incident.
- Interface utilisable sur PC, tablette et smartphone. Elle n'a aucune dépendance Internet (pas de CDN).

## Architecture

| Élément | Choix |
|---|---|
| Serveur | ASP.NET Core 8 (C#), API minimale + page HTML/JS native |
| Authentification | `Negotiate` : délègue à IIS sous IIS, Kerberos/NTLM natif sous Kestrel |
| Annuaire | `System.DirectoryServices` avec **l'identité du processus**, sans mot de passe stocké |
| Données | SQLite (`App_Data/comptage.db`), mode WAL |
| Hébergement | **IIS** *ou* **autonome** (service Windows / exe), sur un serveur Windows membre du domaine |

Identité utilisée pour interroger l'AD :

| Mode | Compte qui lit l'annuaire |
|---|---|
| IIS (ApplicationPoolIdentity) | compte ordinateur du serveur `DOMAINE\SERVEUR$` |
| Service Windows LocalSystem / NetworkService | compte ordinateur du serveur `DOMAINE\SERVEUR$` |
| Service Windows avec compte de domaine | ce compte |
| `ComptageIncident.exe` lancé dans une session | l'utilisateur connecté |

Par défaut, tout compte du domaine peut lire les utilisateurs de l'AD : aucune délégation n'est nécessaire.

```
src/ComptageIncident/
  Program.cs                 hébergement IIS / service, authentification, sécurité
  Api/ApiEndpoints.cs        API REST (/api/...)
  Data/                      SQLite : schéma, incidents, personnes, journal
  Directory/                 recherche AD (+ annuaire fictif pour le développement)
  Services/TrashPurgeService purge de la corbeille
  wwwroot/                   interface (index.html, css/app.css, js/app.js)
deploy/
  Install-IIS.ps1            installation / mise à jour sous IIS
  Install-Service.ps1        installation / mise à jour en service Windows
tests/ComptageIncident.Tests tests automatisés (xUnit)
```

## Installation

L'infrastructure du client n'étant pas connue à l'avance, les deux modes sont fournis. Choisissez-en **un seul**.

### Option A : IIS

Prérequis sur le serveur :
1. Rôle **IIS** avec la fonctionnalité **Authentification Windows**. Le script l'installe sur Windows Server.
2. **ASP.NET Core 8 Hosting Bundle** (dotnet.microsoft.com → .NET 8 → *Hosting Bundle*).

Installation, depuis une console PowerShell **administrateur**, à la racine du dépôt :

```powershell
.\deploy\Install-IIS.ps1 -SiteName ComptageIncident -Port 80 -HostName comptage.entreprise.local
```

Le script :
- publie l'application
- copie les fichiers dans `C:\inetpub\ComptageIncident`, en conservant `App_Data` et `appsettings.json` lors d'une mise à jour
- crée le pool d'applications (*No Managed Code*, toujours démarré)
- crée le site, active l'**authentification Windows** et désactive l'anonyme
- donne au pool les droits d'écriture sur `App_Data`

Installation manuelle (sans script) :

```powershell
dotnet publish src/ComptageIncident -c Release -r win-x64 --self-contained false -o publish
```

Ensuite, dans IIS :
1. Créez un site pointant vers ce dossier, avec un pool en *No Managed Code*.
2. Dans *Authentification* : activez **Windows**, désactivez **Anonyme**.
3. Donnez `Modifier` à `IIS AppPool\<pool>` sur `App_Data`.

> Erreur **500.19** sur `windowsAuthentication` : la section est verrouillée au niveau serveur. Lancez une fois :
> `%windir%\system32\inetsrv\appcmd unlock config -section:system.webServer/security/authentication/windowsAuthentication`
> (et la même commande pour `anonymousAuthentication`), ou utilisez `Install-IIS.ps1`.

### Option B : autonome (sans IIS)

Prérequis : **ASP.NET Core 8 Runtime** (ou le Hosting Bundle).

En **service Windows** (démarrage automatique), depuis PowerShell administrateur :

```powershell
.\deploy\Install-Service.ps1 -Port 5080 -OpenFirewall
# avec un compte de domaine dédié :
.\deploy\Install-Service.ps1 -Port 5080 -ServiceAccount 'DOMAINE\svc-comptage' -OpenFirewall
```

**Exécutable lancé dans une session** (essai, poste de secours) : lancez `ComptageIncident.exe` depuis le dossier publié. L'annuaire est alors interrogé avec le compte de l'utilisateur connecté.

Le port se règle dans `appsettings.json`, section `Kestrel:Endpoints`. En HTTPS, utilisez :

```json
"Kestrel": { "Endpoints": { "Https": {
  "Url": "https://0.0.0.0:5443", "Protocols": "Http1",
  "Certificate": { "Subject": "comptage.entreprise.local", "Store": "My", "Location": "LocalMachine" } } } }
```

`"Protocols": "Http1"` est requis : l'authentification Windows ne fonctionne pas en HTTP/2.

### Kerberos / SPN

- Avec IIS, ou avec le service en LocalSystem / NetworkService, et un accès par le **nom du serveur** : rien à faire.
- Avec un **alias DNS** (ex. `comptage.entreprise.local`) ou un **compte de service de domaine**, déclarez le SPN, sinon l'authentification bascule en NTLM (fonctionnel, mais moins sûr) :
  ```
  setspn -S HTTP/comptage.entreprise.local SERVEUR        (IIS / LocalSystem)
  setspn -S HTTP/comptage.entreprise.local svc-comptage   (compte dédié)
  ```

### Navigateurs (connexion automatique)

Edge et Chrome envoient automatiquement l'identité Windows aux sites de la **zone Intranet local**. Si une fenêtre de connexion apparaît, ajoutez l'URL à cette zone, idéalement par GPO :
- *Composants Windows › Internet Explorer › Panneau de configuration Internet › Page Sécurité › Liste des attributions de sites aux zones* → valeur `1`
- ou la stratégie Edge/Chrome `AuthServerAllowlist`

Pour Firefox, renseignez `network.negotiate-auth.trusted-uris`.

## Configuration (`appsettings.json`)

| Clé | Défaut | Rôle |
|---|---|---|
| `Database:Path` | `App_Data/comptage.db` | fichier SQLite, relatif au dossier de l'application ou absolu |
| `Directory:Provider` | `ActiveDirectory` | `Fake` = annuaire fictif, pour le développement uniquement |
| `Directory:SearchRoot` | *(vide)* | ex. `LDAP://OU=Personnel,DC=entreprise,DC=local` ; vide = tout le domaine |
| `Directory:BaseFilter` | utilisateurs actifs | filtre LDAP de base (exclut les comptes désactivés) |
| `Directory:MinSearchLength` | `4` | nombre de caractères avant de lancer la recherche |
| `Directory:MaxResults` | `20` | nombre maximum de suggestions |
| `Trash:RetentionHours` | `24` | durée de restauration possible d'un incident supprimé |
| `Trash:PurgeIntervalMinutes` | `15` | fréquence de la purge de la corbeille |

Exemple pour exclure les comptes de service et ne garder que les comptes ayant un prénom :

```json
"BaseFilter": "(&(objectCategory=person)(objectClass=user)(givenName=*)(!(userAccountControl:1.2.840.113556.1.4.803:=2)))"
```

## Sauvegarde

Toutes les données sont dans `App_Data\comptage.db`. Sauvegardez ce dossier entier : `comptage.db`, `-wal` et `-shm`. Pour une copie à chaud cohérente :

```
sqlite3 App_Data\comptage.db ".backup 'D:\Sauvegardes\comptage.db'"
```

## Recette sur le serveur du client

1. Ouvrez l'URL depuis un poste du domaine : votre `DOMAINE\login` doit apparaître en haut à droite, sans saisie.
2. Créez un incident de type *Exercice*.
3. Tapez 4 lettres d'un nom : des collègues doivent apparaître. Si « Annuaire indisponible » s'affiche, consultez le journal d'événements Windows (source .NET Runtime / ASP.NET Core) et vérifiez que le serveur est bien membre du domaine.
4. Ajoutez un visiteur, retirez une personne puis réintégrez-la.
5. Supprimez l'incident, puis restaurez-le depuis la Corbeille.

## Développement

```bash
cd src/ComptageIncident
dotnet run --launch-profile "Dev (annuaire fictif)"
# http://localhost:5112 : utilisateur simulé "DEV\testeur", annuaire fictif (essayez "dupo", "mart", "jean")
dotnet test   # depuis la racine
```

En environnement `Development`, `Auth:DevUser` simule un utilisateur et `Directory:Provider=Fake` fournit un annuaire fictif. **Ces réglages ne sont jamais actifs en production.**

## Sécurité

- Toutes les pages et l'API exigent un utilisateur Windows authentifié (politique d'autorisation par défaut).
- Protection CSRF : les requêtes de modification exigent l'en-tête `X-Requested-With`.
- En-têtes de sécurité : CSP stricte, `X-Frame-Options`, `nosniff`.
- Requêtes SQL paramétrées, filtres LDAP échappés (RFC 4515), export CSV protégé contre l'injection de formules.
