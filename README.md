# Comptage incident

Application Web interne pour **compter les personnes présentes** lors d'un incident sur site (incendie, fuite de produit chimique, exercice d'évacuation…).

- **Authentification Windows automatique** : l'utilisateur est reconnu par sa session, sans saisie (Kerberos/NTLM).
- **Recherche dans Active Directory** par nom ou prénom : suggestions à partir de **4 caractères**, ajout après **popup de confirmation**.
- **Visiteurs** : ajout manuel du nom complet (et de la société en option).
- **Deux listes séparées**, Internes et Visiteurs, triées par **ordre alphabétique**, avec le **total** de chacune et le total général.
- **Suppression** d'une personne avec popup de confirmation. Elle reste **réintégrable** en cas d'erreur.
- **Plusieurs incidents** conservés dans une base **SQLite** locale, **aucun bouton « tout effacer »**.
- **Suppression d'incident = corbeille de 24 h**, avec restauration en un clic, puis purge automatique.
- Clôture (lecture seule), **journal d'audit**, impression, export CSV, rafraîchissement automatique (plusieurs agents en parallèle).
- Fonctionne sans Internet (aucun CDN). Interface adaptée au PC, à la tablette et au smartphone.

## Technologies

| Élément | Choix |
|---|---|
| Serveur | **ASP.NET Core 10** (LTS, supporté jusqu'en novembre 2028), C# |
| Hébergement | **IIS** *ou* **autonome** (service Windows / exe), sur un serveur Windows membre du domaine |
| Authentification | Windows (Negotiate : Kerberos / NTLM). Aucun mot de passe manipulé |
| Annuaire | Active Directory en lecture seule, avec l'**identité du processus** (pool IIS, compte du service ou session) |
| Données | SQLite (`App_Data/comptage.db`) |
| Interface | HTML / CSS / JavaScript natifs, sans dépendance |

## Démarrage rapide

```powershell
# IIS (PowerShell administrateur, à la racine du dépôt)
.\deploy\Install-IIS.ps1 -Port 80 -HostName comptage.entreprise.local
# ou mode autonome (service Windows)
.\deploy\Install-Service.ps1 -Port 5080 -OpenFirewall

# Désinstallation (sauvegarde automatique des données)
.\deploy\Uninstall-IIS.ps1        # ou .\deploy\Uninstall-Service.ps1
```

Prérequis : le **ASP.NET Core 10 Hosting Bundle** sur le serveur. Pour IIS, la fonctionnalité *Authentification Windows* est aussi nécessaire ; le script l'installe.

## Documentation

La documentation complète est dans [`docs/`](docs/README.md) :

| | |
|---|---|
| [Présentation](docs/01-presentation.md) | [Architecture](docs/02-architecture.md) |
| [Authentification Windows](docs/03-authentification.md) | [Annuaire LDAP](docs/04-annuaire-ldap.md) |
| [Données, corbeille, journal](docs/05-donnees-et-corbeille.md) | [Sécurité](docs/06-securite.md) |
| [Installation IIS](docs/07-installation-iis.md) | [Installation autonome](docs/08-installation-autonome.md) |
| [Exploitation](docs/09-exploitation.md) | [Référence API](docs/10-reference-api.md) |
| [Guide utilisateur](docs/11-guide-utilisateur.md) | [Désinstallation](docs/12-desinstallation.md) |
| [Tests et grille d'audit](docs/13-tests-et-audit.md) | |

Le code source et les scripts sont commentés en français, en tête de chaque fichier et à chaque point sensible.

## Développement

```bash
cd src/ComptageIncident
dotnet run --launch-profile "Dev (annuaire fictif)"
# http://localhost:5112 : utilisateur simulé « DEV\testeur », annuaire fictif (essayez « dupo », « mart », « jean »)

# Tests (règles métier, API, sécurité) depuis la racine
dotnet test
```

Le mode développement (utilisateur simulé, annuaire fictif) **ne peut pas** être actif en production : voir [docs/03 · Mode développement](docs/03-authentification.md#mode-développement--pourquoi-il-ne-peut-pas-être-actif-en-production).
