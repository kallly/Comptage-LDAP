# 07 · Installation sous IIS

Choisissez ce mode si le serveur dispose d'IIS (ou peut en disposer). C'est le mode le plus courant en entreprise : il bénéficie de la supervision et de la gestion des certificats d'IIS. Sinon, voir [08 · Installation autonome](08-installation-autonome.md).

## Prérequis

| Élément | Détail |
|---|---|
| Serveur | Windows Server 2016 ou plus récent, **membre du domaine** |
| Rôle IIS | Serveur Web (IIS) + fonctionnalité **Sécurité › Authentification Windows**. Le script les installe automatiquement sur Windows Server |
| .NET | **ASP.NET Core 10 Hosting Bundle** : https://dotnet.microsoft.com/download/dotnet/10.0 → *Hosting Bundle*. Il installe le runtime **et** le module IIS `AspNetCoreModuleV2`. À installer **après** IIS, ou à réparer si IIS est ajouté ensuite |
| Droits | Session administrateur local du serveur |
| Réseau | Port 80 (et/ou 443) ouvert depuis les postes ; accès LDAP (389) du serveur vers les contrôleurs de domaine |
| Fichiers | Le dépôt (avec le SDK .NET 10 sur le serveur, le script publie lui-même), **ou** un dossier déjà publié (voir *Publier sur un autre poste*) |

## Installation par script (recommandée)

Dans une console **PowerShell administrateur**, à la racine du dépôt :

```powershell
Set-ExecutionPolicy -Scope Process Bypass        # autorise l'exécution du script pour cette console uniquement
.\deploy\Install-IIS.ps1 -SiteName ComptageIncident -Port 80 -HostName comptage.entreprise.local
```

| Paramètre | Défaut | Rôle |
|---|---|---|
| `-SiteName` | `ComptageIncident` | Nom du site IIS |
| `-AppPoolName` | `ComptageIncident` | Nom du pool (identité `IIS AppPool\ComptageIncident`) |
| `-PhysicalPath` | `C:\inetpub\ComptageIncident` | Dossier d'installation |
| `-Port` | `80` | Port HTTP |
| `-HostName` | *(vide)* | Nom d'hôte du site. Vide = toutes les requêtes reçues sur ce port |
| `-PublishPath` | `..\publish\iis` | Dossier publié à utiliser ou à produire |

Ce que fait le script (détaillé et commenté dans `deploy/Install-IIS.ps1`) :
1. vérifie et installe IIS et l'authentification Windows ; vérifie le module ASP.NET Core et le runtime 10 ;
2. publie l'application (`dotnet publish -c Release -r win-x64`) si le SDK est présent ;
3. copie les fichiers **sans toucher** à `App_Data` (la base) ni à un `appsettings.json` existant ;
4. crée le pool : *No Managed Code*, `ApplicationPoolIdentity`, toujours démarré, sans arrêt pour inactivité ;
5. crée le site, **active l'authentification Windows et désactive l'anonyme**, en déverrouillant ces sections pour ce site uniquement ;
6. donne au pool la **lecture** sur l'application et la **modification** sur `App_Data` uniquement ;
7. démarre le site et affiche l'URL ainsi que la commande `setspn` à lancer en cas d'alias DNS.

## Publier sur un autre poste (serveur sans SDK)

Sur un poste de développement disposant du SDK .NET 10 :
```powershell
dotnet publish src\ComptageIncident -c Release -r win-x64 --self-contained false -o publish\iis
```
Copiez ensuite **tout le dépôt**, avec le dossier `publish\iis`, sur le serveur, puis lancez `Install-IIS.ps1` : sans SDK, il utilise le dossier publié tel quel.

## Installation manuelle (sans script)

1. Installer IIS avec *Authentification Windows*, puis le Hosting Bundle .NET 10.
2. Publier l'application (commande ci-dessus) et copier le contenu dans `C:\inetpub\ComptageIncident`.
3. Créer le dossier `C:\inetpub\ComptageIncident\App_Data`.
4. **Gestionnaire IIS** › Pools d'applications › Ajouter : nom `ComptageIncident`, version CLR **Aucun code managé**. Paramètres avancés : *Mode de démarrage* = `AlwaysRunning`, *Délai d'inactivité* = `0`.
5. Sites › Ajouter un site : chemin `C:\inetpub\ComptageIncident`, pool `ComptageIncident`, liaison http/80 (+ nom d'hôte).
6. Site › **Authentification** : *Authentification Windows* = Activé, *Authentification anonyme* = Désactivé.
7. Droits NTFS, en administrateur :
   ```
   icacls C:\inetpub\ComptageIncident /grant "IIS AppPool\ComptageIncident:(OI)(CI)RX" /T
   icacls C:\inetpub\ComptageIncident\App_Data /grant "IIS AppPool\ComptageIncident:(OI)(CI)M" /T
   ```
8. Si IIS affiche une **erreur 500.19** liée à `windowsAuthentication`, déverrouiller les sections :
   ```
   %windir%\system32\inetsrv\appcmd unlock config -section:system.webServer/security/authentication/windowsAuthentication
   %windir%\system32\inetsrv\appcmd unlock config -section:system.webServer/security/authentication/anonymousAuthentication
   ```

## HTTPS (recommandé)

Les listes de personnes sont des données personnelles. Il est recommandé de chiffrer le transport ([06](06-securite.md#limites-connues-et-recommandations)) :
1. Obtenir un certificat serveur pour `comptage.entreprise.local` auprès de l'autorité de certification interne, et l'importer dans *Ordinateur local › Personnel*.
2. Site › **Liaisons** › Ajouter : type `https`, port 443, nom d'hôte, certificat.
3. Option : retirer la liaison http, ou installer le module *URL Rewrite* pour rediriger HTTP vers HTTPS.
4. Mettre à jour l'URL communiquée aux utilisateurs et la configuration des navigateurs (zone Intranet avec `https://`).

L'authentification Windows fonctionne en HTTPS sans réglage supplémentaire sous IIS.

## Kerberos : SPN en cas d'alias DNS

Si les utilisateurs accèdent au site par un nom autre que celui du serveur (ex. `comptage.entreprise.local` → `SRV01`), un administrateur du domaine doit lancer :
```
setspn -S HTTP/comptage.entreprise.local SRV01
```
Détails et vérification avec `klist` : [03](03-authentification.md#kerberos-et-spn).

## Configuration des postes

Ajouter l'URL à la zone **Intranet local** par GPO, pour que la connexion soit automatique : [03](03-authentification.md#configuration-des-navigateurs).

## Vérifications après installation (recette)

| # | Test | Résultat attendu |
|---|---|---|
| 1 | Depuis un poste du domaine, ouvrir l'URL | La page s'affiche **sans demande d'identifiants** ; `DOMAINE\login` apparaît en haut à droite |
| 2 | Depuis un navigateur en navigation privée sur un poste hors domaine | Fenêtre d'identifiants Windows, ou refus : jamais d'accès anonyme |
| 3 | Créer un incident de type *Exercice* | L'incident s'ouvre |
| 4 | Taper 3 lettres puis 4 lettres d'un nom connu | Rien à 3 lettres ; suggestions à 4 lettres |
| 5 | Ajouter la personne (popup → Ajouter) puis la rechercher à nouveau | Elle apparaît dans *Internes*, et en grisé « déjà comptée » dans les suggestions |
| 6 | Ajouter un visiteur | Il apparaît dans *Visiteurs* ; les totaux sont à jour |
| 7 | Supprimer puis réintégrer une personne | Elle passe dans *Personnes retirées* puis revient |
| 8 | Supprimer l'incident, puis Corbeille › Restaurer | L'incident revient intact |
| 9 | Bouton **Journal** | Toutes les actions précédentes, avec votre identité |
| 10 | Sur un poste client : `klist` | Présence d'un ticket `HTTP/<nom du site>` (Kerberos utilisé) |
| 11 | Supprimer l'incident d'exercice | Il sera purgé automatiquement 24 h plus tard |

En cas d'anomalie, voir [09 · Dépannage](09-exploitation.md#dépannage).

## Mise à jour

Relancer simplement `Install-IIS.ps1` avec les mêmes paramètres. La base et `appsettings.json` sont conservés. Une sauvegarde préalable de `App_Data` reste recommandée ([09](09-exploitation.md#mise-à-jour-de-lapplication)).
