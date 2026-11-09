# 08 · Installation autonome (sans IIS)

Dans ce mode, l'application contient son propre serveur Web (**Kestrel**) et gère elle-même l'authentification Windows. Il convient lorsque IIS n'est pas disponible ou pas souhaité.

Deux variantes :
- **Service Windows** (production) : démarre avec le serveur et redémarre automatiquement en cas d'échec ;
- **Exécutable lancé dans une session** (essai, poste de secours) : fonctionne tant que la session est ouverte.

## Prérequis

| Élément | Détail |
|---|---|
| Serveur | Windows 10/11 ou Windows Server 2016+, **membre du domaine** |
| .NET | **ASP.NET Core 10 Runtime**, ou le Hosting Bundle qui le contient : https://dotnet.microsoft.com/download/dotnet/10.0 |
| Droits | Administrateur local (pour le service) |
| Réseau | Port choisi (5080 par défaut) ouvert depuis les postes ; LDAP (389) vers les contrôleurs de domaine |

## Variante A : service Windows (script)

PowerShell **administrateur**, à la racine du dépôt :

```powershell
Set-ExecutionPolicy -Scope Process Bypass
# Compte LocalSystem (défaut) : l'AD est interrogé avec le compte ordinateur du serveur
.\deploy\Install-Service.ps1 -Port 5080 -OpenFirewall

# Ou avec un compte de domaine dédié
.\deploy\Install-Service.ps1 -Port 5080 -ServiceAccount 'ENTREPRISE\svc-comptage' -OpenFirewall
```

| Paramètre | Défaut | Rôle |
|---|---|---|
| `-ServiceName` | `ComptageIncident` | Nom technique du service |
| `-InstallPath` | `C:\Apps\ComptageIncident` | Dossier d'installation |
| `-Port` | `5080` | Port HTTP, écrit dans `appsettings.json` à la première installation uniquement |
| `-ServiceAccount` | *(vide = LocalSystem)* | `NetworkService`, ou `DOMAINE\compte` (mot de passe demandé de façon interactive) |
| `-OpenFirewall` | non | Règle de pare-feu entrante, **profil Domaine uniquement** |
| `-PublishPath` | `..\publish\service` | Dossier publié |

Ce que fait le script (commenté dans `deploy/Install-Service.ps1`) :
1. vérifie le runtime ASP.NET Core 10 et publie l'application si le SDK est présent ;
2. arrête le service existant (mise à jour) ;
3. copie les fichiers en conservant `App_Data` et `appsettings.json` (et ignore `web.config`, inutile ici) ;
4. écrit le port dans `appsettings.json` (première installation) ;
5. crée le service : démarrage automatique, et redémarrage après 5 s, 5 s puis 30 s en cas d'échec ;
6. donne au compte du service la lecture sur l'application et la modification sur `App_Data` ;
7. ouvre le pare-feu (option) et démarre le service.

### Choix du compte de service

| Compte | Interroge l'AD en tant que | SPN pour Kerberos | Remarque |
|---|---|---|---|
| LocalSystem (défaut) | `DOMAINE\SERVEUR$` | Rien à faire si accès par le nom du serveur | Simple ; compte très privilégié **sur le serveur local** |
| NetworkService | `DOMAINE\SERVEUR$` | Idem | Moins privilégié localement : **recommandé** |
| Compte de domaine dédié | Ce compte | `setspn -S HTTP/<serveur> <compte>` et `HTTP/<fqdn>` (le script affiche les commandes) | Le compte doit avoir le droit *Ouvrir une session en tant que service* (GPO ou `secpol.msc`) ; mot de passe à gérer, ou utiliser un gMSA |

## Variante B : exécutable dans une session

1. Publier : `dotnet publish src\ComptageIncident -c Release -r win-x64 --self-contained false -o publish\app`
2. Copier `publish\app` sur le poste, puis double-cliquer sur `ComptageIncident.exe`, ou le lancer depuis une console.
3. Le site écoute sur l'URL de `appsettings.json` (`http://0.0.0.0:5080` par défaut).

Dans ce cas, **l'annuaire est interrogé avec le compte de l'utilisateur de la session** qui a lancé l'exe. L'application s'arrête à la fermeture de la fenêtre ou de la session. La base est créée dans `App_Data` à côté de l'exe : l'utilisateur doit avoir le droit d'y écrire.

## Comment l'exe trouve l'interface (`wwwroot`)

L'exe ne contient pas l'interface. Celle-ci est un **dossier de fichiers** livré à côté de lui par la publication :

```
C:\Apps\ComptageIncident\
  ComptageIncident.exe        lanceur (démarre le runtime .NET et ComptageIncident.dll)
  ComptageIncident.dll        l'application (API, authentification, règles métier)
  *.dll, e_sqlite3.dll        dépendances (dont le moteur SQLite)
  appsettings.json            configuration
  wwwroot\                    l'interface servie aux navigateurs
    index.html
    css\app.css
    js\app.js
    (*.gz, *.br : versions compressées générées par la publication)
  App_Data\comptage.db        la base (créée au premier démarrage)
```

Fonctionnement :
1. Au démarrage, l'application détermine son **dossier racine** (*ContentRoot*). Si le dossier de l'exe contient `wwwroot`, ce qui est le cas de toute version publiée, c'est **ce dossier** qui est utilisé, **quel que soit le répertoire courant** (`Program.cs`, section 1). Un double-clic, un raccourci, une tâche planifiée, une invite de commande placée ailleurs ou un service Windows donnent donc le même résultat.
2. Le serveur Kestrel intégré sert les fichiers de `wwwroot` (`UseStaticFiles`) après l'authentification : `http://serveur:5080/` renvoie `wwwroot/index.html`, qui charge `css/app.css` et `js/app.js`.
3. Le JavaScript appelle ensuite l'API `/api/...` du même exe.

Conséquences pratiques :
- **Copiez toujours le dossier publié en entier**. Sans `wwwroot`, l'API fonctionne mais la page renvoie 404.
- On peut modifier l'apparence (`wwwroot/css/app.css`) sans recompiler : il suffit de recharger la page, car les fichiers sont servis avec `Cache-Control: no-cache`.
- `appsettings.json` et `App_Data` sont aussi cherchés dans le dossier de l'exe, jamais dans le répertoire courant.

## Configuration du port et de l'adresse d'écoute

Dans `appsettings.json`, section `Kestrel` :
```json
"Kestrel": { "Endpoints": { "Http": { "Url": "http://0.0.0.0:5080" } } }
```
- `0.0.0.0` = toutes les interfaces réseau. Une adresse précise peut être indiquée pour limiter l'écoute.
- Redémarrer le service après modification : `Restart-Service ComptageIncident`.

## HTTPS

Recommandé ([06](06-securite.md#limites-connues-et-recommandations)). Importer le certificat dans *Ordinateur local › Personnel*, puis :

```json
"Kestrel": {
  "Endpoints": {
    "Https": {
      "Url": "https://0.0.0.0:5443",
      "Protocols": "Http1",
      "Certificate": { "Subject": "comptage.entreprise.local", "Store": "My", "Location": "LocalMachine", "AllowInvalid": false }
    }
  }
}
```

- **`"Protocols": "Http1"` est obligatoire** : l'authentification Windows ne fonctionne pas en HTTP/2, que Kestrel proposerait sinon en HTTPS.
- Le compte du service doit pouvoir lire la clé privée du certificat : console *Certificats* › clic droit › *Toutes les tâches › Gérer les clés privées*.
- Ouvrir le port 5443 dans le pare-feu et retirer l'endpoint `Http` si HTTP ne doit plus être servi.

## Vérifications

Mêmes tests de recette que pour IIS : [07 · Vérifications](07-installation-iis.md#vérifications-après-installation-recette). Commandes utiles :
```powershell
Get-Service ComptageIncident                             # état du service
Get-NetTCPConnection -LocalPort 5080 -State Listen       # écoute effective
Get-EventLog -LogName Application -Source ComptageIncident -Newest 20   # journaux de l'application
```

## Mise à jour

Relancer `Install-Service.ps1` : le service est arrêté, les fichiers remplacés (la base et la configuration sont conservées), puis le service redémarre.
