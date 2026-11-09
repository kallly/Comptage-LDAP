# Documentation : Comptage incident

Cette documentation explique **en détail** le fonctionnement de l'application. Elle doit permettre à un lecteur de :
- comprendre chaque mécanisme (authentification, annuaire, données, corbeille, sécurité) ;
- installer, exploiter et désinstaller l'application ;
- **vérifier par lui-même, en lisant le code**, que l'application se comporte comme décrit.

Chaque affirmation importante renvoie au fichier et à la fonction qui l'implémente. Le code source est lui-même commenté en français, en tête de chaque fichier et à chaque point sensible.

## Sommaire

| # | Document | Pour qui | Contenu |
|---|---|---|---|
| 01 | [Présentation](01-presentation.md) | Tous | Objectif, fonctionnalités, vocabulaire |
| 02 | [Architecture](02-architecture.md) | Technique | Composants, rôle de chaque fichier, parcours d'une requête |
| 03 | [Authentification Windows](03-authentification.md) | Technique / sécurité | Kerberos, NTLM, IIS ou Kestrel, SPN, navigateurs, mode développement |
| 04 | [Annuaire LDAP / Active Directory](04-annuaire-ldap.md) | Technique / sécurité | Compte utilisé, filtre, attributs lus, protection contre l'injection |
| 05 | [Données, corbeille et journal](05-donnees-et-corbeille.md) | Technique / fonctionnel | Schéma SQLite, règles métier, cycle de vie, purge, concurrence |
| 06 | [Sécurité](06-securite.md) | Sécurité / audit | Menaces, protections, preuves dans le code, limites connues |
| 07 | [Installation sous IIS](07-installation-iis.md) | Exploitation | Prérequis, script, installation manuelle, vérifications |
| 08 | [Installation autonome (sans IIS)](08-installation-autonome.md) | Exploitation | Service Windows, exe en session, HTTPS, SPN |
| 09 | [Exploitation](09-exploitation.md) | Exploitation | Configuration, journaux, sauvegarde, mise à jour, dépannage |
| 10 | [Référence de l'API](10-reference-api.md) | Technique | Toutes les routes, paramètres, réponses, codes d'erreur |
| 11 | [Guide utilisateur](11-guide-utilisateur.md) | Agents / secouristes | Utilisation pendant un incident |
| 12 | [Désinstallation](12-desinstallation.md) | Exploitation | Scripts et procédure manuelle, nettoyage complet |
| 13 | [Tests et grille d'audit](13-tests-et-audit.md) | Audit / qualité | Tests automatisés, grille de vérification du code point par point |

## Lecture conseillée

- **Pour décider d'un déploiement** : 01 → 06 → 07 ou 08.
- **Pour auditer le code** : 02 → 03 → 04 → 05 → 06 → 13. La grille du document 13 liste chaque point à contrôler, avec le fichier à ouvrir.
- **Pour les utilisateurs** : 11 uniquement.

## Arborescence du dépôt

```
ComptageIncident.sln                 Solution Visual Studio (application + tests)
README.md                            Présentation rapide
docs/                                Cette documentation
deploy/
  Install-IIS.ps1                    Installation / mise à jour sous IIS
  Uninstall-IIS.ps1                  Désinstallation IIS (avec sauvegarde des données)
  Install-Service.ps1                Installation / mise à jour en service Windows
  Uninstall-Service.ps1              Désinstallation du service (avec sauvegarde des données)
src/ComptageIncident/                Application
  Program.cs                         Démarrage : authentification, sécurité, enregistrement des services
  appsettings.json                   Configuration de production (commentée)
  appsettings.Development.json       Configuration de développement (jamais publiée)
  web.config                         Configuration IIS
  Api/ApiEndpoints.cs                API REST + protection anti-CSRF
  Auth/DevAuthenticationHandler.cs   Utilisateur simulé (développement uniquement)
  Data/Database.cs                   Fichier SQLite et schéma
  Data/IncidentRepository.cs         Règles métier, corbeille, journal d'audit
  Data/Models.cs                     Objets métier
  Directory/IDirectoryService.cs     Contrat d'annuaire + construction / échappement du filtre LDAP
  Directory/AdDirectoryService.cs    Recherche Active Directory
  Directory/FakeDirectoryService.cs  Annuaire fictif (développement / tests)
  Options/AppOptions.cs              Paramètres et valeurs par défaut
  Services/TrashPurgeService.cs      Purge automatique de la corbeille
  wwwroot/                           Interface (index.html, css/app.css, js/app.js)
tests/ComptageIncident.Tests/        Tests automatisés (règles métier, API, sécurité)
```
