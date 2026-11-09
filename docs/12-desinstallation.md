# 12 · Désinstallation

La désinstallation **sauvegarde d'abord les données**, puis retire tout ce que l'installation a créé. Les composants partagés (rôle IIS, runtime .NET), qui peuvent servir à d'autres applications, sont **conservés**.

## Ce que l'installation a créé

| Élément | Mode IIS | Mode autonome |
|---|---|---|
| Dossier de l'application (+ `App_Data` avec la base) | `C:\inetpub\ComptageIncident` | `C:\Apps\ComptageIncident` |
| Site IIS | `ComptageIncident` | — |
| Pool d'applications | `ComptageIncident` (identité `IIS AppPool\ComptageIncident`) | — |
| Déverrouillage des sections d'authentification pour ce site (`applicationHost.config`) | Oui | — |
| Service Windows | — | `ComptageIncident` |
| Règle de pare-feu « Comptage incident » | — | Si `-OpenFirewall` |
| SPN (si déclarés manuellement) | `HTTP/<alias>` sur le compte ordinateur | `HTTP/<serveur>` sur le compte de service |
| GPO / zone Intranet sur les postes (si configurées) | URL du site | URL du site |
| Rôle IIS, Hosting Bundle / runtime .NET 10 | Installés si absents | Runtime installé si absent |

## Désinstallation par script

PowerShell **administrateur**, à la racine du dépôt :

```powershell
Set-ExecutionPolicy -Scope Process Bypass

# Simulation : affiche ce qui serait fait, sans rien modifier
.\deploy\Uninstall-IIS.ps1 -WhatIf

# Désinstallation IIS : chaque étape destructrice demande confirmation (O/N)
.\deploy\Uninstall-IIS.ps1

# Désinstallation du mode autonome
.\deploy\Uninstall-Service.ps1
```

Paramètres : mêmes noms et chemins par défaut que les scripts d'installation (`-SiteName`, `-AppPoolName`, `-PhysicalPath` / `-ServiceName`, `-InstallPath`), plus :

| Paramètre | Défaut | Rôle |
|---|---|---|
| `-BackupPath` | `C:\Sauvegardes\ComptageIncident-<date-heure>` | Destination de la sauvegarde de `App_Data` et `appsettings.json` |
| `-NoBackup` | non | **Ne pas sauvegarder** : la base est définitivement perdue |
| `-Confirm:$false` | — | Ne pas demander de confirmation à chaque étape (désinstallation automatisée) |

Étapes réalisées par `Uninstall-IIS.ps1` :
1. arrêt du site et du pool ;
2. **sauvegarde** de `App_Data` et `appsettings.json` dans `-BackupPath` ;
3. suppression du site, puis du pool ;
4. suppression du dossier de l'application ;
5. retrait du déverrouillage des sections d'authentification posé pour ce site.

Étapes réalisées par `Uninstall-Service.ps1` :
1. arrêt du service ;
2. **sauvegarde** de `App_Data` et `appsettings.json` ;
3. suppression du service (`sc.exe delete`) ;
4. suppression de la règle de pare-feu « Comptage incident » ;
5. suppression du dossier d'installation.

## Désinstallation manuelle

**IIS**
1. Copier `C:\inetpub\ComptageIncident\App_Data` en lieu sûr.
2. Gestionnaire IIS : arrêter puis supprimer le site `ComptageIncident`, puis le pool `ComptageIncident`.
3. Supprimer le dossier `C:\inetpub\ComptageIncident`.

**Autonome**
1. `Stop-Service ComptageIncident`
2. Copier `C:\Apps\ComptageIncident\App_Data` en lieu sûr.
3. `sc.exe delete ComptageIncident`
4. `Remove-NetFirewallRule -DisplayName 'Comptage incident'` (si elle existe).
5. Supprimer le dossier `C:\Apps\ComptageIncident`.

## Nettoyage complémentaire (facultatif)

| Élément | Commande / action | Précaution |
|---|---|---|
| SPN | `setspn -L <compte>` pour lister, puis `setspn -D HTTP/<nom> <compte>` | Administrateur du domaine ; vérifier qu'aucune autre application n'utilise ce nom |
| Enregistrement DNS (alias) | Console DNS | — |
| GPO zone Intranet / AuthServerAllowlist | Retirer l'URL de la GPO | — |
| Compte de service dédié | Désactiver puis supprimer dans l'AD | — |
| Hosting Bundle / runtime .NET 10 | *Paramètres › Applications* | Seulement si **aucune autre** application .NET ne l'utilise |
| Rôle IIS | `Uninstall-WindowsFeature Web-Server` | Seulement si **aucun autre** site n'est hébergé |
| Sauvegardes | Supprimer `C:\Sauvegardes\ComptageIncident-*` | Selon la politique de conservation des données personnelles ([05](05-donnees-et-corbeille.md#données-personnelles)) |

## Réinstaller en conservant les données

1. Réinstaller avec `Install-IIS.ps1` ou `Install-Service.ps1`.
2. Arrêter l'application, copier le contenu de la sauvegarde `App_Data` dans le nouveau dossier `App_Data`, et éventuellement restaurer `appsettings.json`.
3. Redémarrer : tous les incidents et le journal sont de retour.
