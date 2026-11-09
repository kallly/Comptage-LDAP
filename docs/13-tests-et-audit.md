# 13 · Tests automatisés et grille d'audit

Ce document permet de **vérifier par soi-même** que l'application est conforme à sa description, de deux façons complémentaires :
1. en **exécutant** les tests automatisés ;
2. en **lisant** le code, point par point, à l'aide de la grille d'audit.

## 1. Exécuter les tests

Prérequis : SDK .NET 10 (https://dotnet.microsoft.com/download/dotnet/10.0). Fonctionne sous Windows, Linux et macOS.

```bash
# À la racine du dépôt
dotnet test
# Résultat attendu :  Passed!  - Failed: 0, Passed: 30
```

Vérifier aussi l'absence de vulnérabilité connue dans les dépendances :
```bash
dotnet list package --vulnerable --include-transitive
# Résultat attendu : "has no vulnerable packages"
```

### Ce que couvrent les tests (30 cas)

| Fichier | Test | Ce qui est démontré |
|---|---|---|
| `SecurityTests.cs` › `ProductionSecurityTests` *(démarre la vraie application en **Production** sur Kestrel, avec l'authentification Windows réelle)* | `Sans_authentification_Windows_tout_est_refuse_avec_un_defi_Negotiate` (7 URL : `/`, `/index.html`, `/js/app.js`, `/api/me`, `/api/incidents`, `/api/trash`, `/api/directory/search`) | **S1** : rien n'est accessible sans authentification, pas même la page ou le JavaScript. **S3** : `Auth:DevUser` et `X-Dev-User` sont sans effet en production |
| | `Une_modification_sans_authentification_est_refusee` | S1 sur une écriture |
| | `Les_entetes_de_securite_sont_presents_meme_sur_un_refus` | S10 : CSP, `X-Frame-Options`, `nosniff` |
| `SecurityTests.cs` › `DataSecurityTests` | `Une_tentative_d_injection_SQL_est_stockee_comme_du_texte` | S6 |
| | `Le_HTML_saisi_est_renvoye_tel_quel_et_jamais_interprete_par_le_serveur` | S8 (côté serveur) |
| | `L_export_CSV_neutralise_les_formules_Excel` | S9 |
| | `Les_champs_trop_longs_sont_refuses` | S14 |
| | `Une_personne_ne_peut_pas_etre_retiree_via_un_autre_incident` | S13 |
| `ApiTests.cs` *(API en mémoire, utilisateur simulé)* | `Requete_de_modification_sans_entete_csrf_est_refusee` | S5 : anti-CSRF |
| | `Utilisateur_courant_est_renvoye` | L'identité vient de l'authentification |
| | `Recherche_annuaire_exige_4_caracteres` (3 → 400, 4 → 200) | Règle des 4 caractères **côté serveur** |
| | `Recherche_sur_nom_et_prenom_combines` | « dupo jea » trouve DUPONT Jean et pas DUPOND Marie |
| | `Filtre_ldap_echappe_les_caracteres_speciaux` | S7 : filtre exact produit pour `du*(pont)` |
| | `Scenario_complet_ajout_doublon_retrait_et_corbeille` | Ajout, doublon (409), visiteur, nom vide (400), retrait, compteurs, CSV, corbeille (404 puis restauration) |
| | `Incident_cloture_refuse_les_ajouts` | R4 via l'API (409) |
| | `Type_incident_inconnu_est_refuse` | Liste fermée des types |
| `RepositoryTests.cs` *(règles métier sur une vraie base SQLite, avec une horloge simulée)* | `Ajout_interne_en_double_est_refuse_meme_avec_une_casse_differente` | R3 (`jdupont` = `JDUPONT`) |
| | `Une_personne_retiree_peut_etre_reintegree_et_rajoutee` | R1 (retrait logique), normalisation des espaces |
| | `Reintegration_refusee_si_la_personne_a_ete_recomptee_entre_temps` | R3 lors d'une réintégration |
| | `Incident_cloture_est_en_lecture_seule_jusqu_a_reouverture` | R4 |
| | `Incident_supprime_va_en_corbeille_et_se_restaure_pendant_24h` | R1 : corbeille ; à 23 h, purge sans effet et restauration possible |
| | `Apres_24h_la_restauration_est_refusee_et_la_purge_supprime_definitivement` | R2 : à 24 h 01, restauration refusée, purge effective, personnes supprimées, autres incidents intacts |
| | `Chaque_action_est_journalisee` | R5 : ordre et auteur des entrées du journal |

Ce que les tests **ne couvrent pas**, faute d'environnement Windows / AD : la recherche réelle dans Active Directory, Kerberos de bout en bout et IIS. Ces points se vérifient par la **recette sur site** ([07](07-installation-iis.md#vérifications-après-installation-recette)).

## 2. Grille d'audit du code

Pour chaque point : le fichier à ouvrir, ce qu'il faut y trouver et la conclusion attendue. Les commandes `rg` (ripgrep) ou `Select-String` permettent les recherches globales.

### A. Authentification et autorisation

| # | Point de contrôle | Où regarder | Attendu |
|---|---|---|---|
| A1 | Toute requête exige un utilisateur authentifié | `Program.cs` : `AddAuthorization(o => o.FallbackPolicy = o.DefaultPolicy)` | Présent |
| A2 | Aucune exception anonyme | `rg -n "AllowAnonymous" src/` | Seulement un commentaire dans `ApiEndpoints.cs` |
| A3 | L'autorisation s'applique aux fichiers statiques | `Program.cs` : `app.UseAuthorization()` **avant** `app.UseStaticFiles()` | Ordre respecté |
| A4 | Authentification de production = Windows | `Program.cs` : branche `else` → `AddNegotiate()` | Présent |
| A5 | Mode développement inactivable en production | `Program.cs` : condition `IsDevelopment() && DevUser` ; `web.config` : `ASPNETCORE_ENVIRONMENT=Production` ; `.csproj` : `appsettings.Development.json` `CopyToPublishDirectory="Never"` | Les trois verrous sont présents. Dans un dossier publié, `appsettings.Development.json` est **absent** |
| A6 | IIS : anonyme désactivé, Windows activé | `web.config` › `<security><authentication>` ; `Install-IIS.ps1` étape 5 | `anonymousAuthentication enabled="false"`, `windowsAuthentication enabled="true"` |
| A7 | L'auteur des actions vient de l'authentification | `ApiEndpoints.cs` : `UserName(ClaimsPrincipal)` ; les records `*Request` ne contiennent aucun champ utilisateur | Conforme |

### B. Protection des entrées et des sorties

| # | Point de contrôle | Où regarder | Attendu |
|---|---|---|---|
| B1 | SQL paramétré uniquement | `IncidentRepository.cs` : toutes les requêtes passent par `Command()` avec `AddWithValue` ; `rg -n '\$"\s*(SELECT|INSERT|UPDATE|DELETE)' src/` | Aucune requête SQL construite par interpolation de données saisies. Seules des **constantes** (`IncidentSelect`, `PersonSelect`) et des fragments fixes sont concaténés |
| B2 | Échappement LDAP | `IDirectoryService.cs` : `EscapeLdap` traite `\ * ( ) NUL` ; `BuildFilter` l'applique à chaque mot | Conforme RFC 4515 |
| B3 | Longueur minimale de recherche côté serveur | `ApiEndpoints.cs` : route `/directory/search`, test `query.Length < MinSearchLength` | Présent |
| B4 | Validation des saisies | `ApiEndpoints.cs` : titre (obligatoire, ≤ 200), type (liste fermée), lieu (≤ 200), login (obligatoire, ≤ 256), nom du visiteur (≥ 2), champs (≤ 256) | Présent |
| B5 | Pas d'injection HTML dans l'interface | `app.js` : fonction `h()` (`textContent`, `createTextNode`) ; `rg -n "innerHTML" src/` | Un seul `innerHTML`, sur une constante (icône) |
| B6 | CSP stricte, sans ressource externe | `Program.cs` 6.a ; `index.html` (aucune URL `http`) ; `rg -n "<script>|style=" src/ComptageIncident/wwwroot` | `default-src 'self'` ; aucun script en ligne ni attribut `style` (la seule occurrence de `<script>` est dans un commentaire de `app.js`) |
| B7 | CSV protégé | `ApiEndpoints.cs` : `Csv()` | Préfixe `'` devant `= + - @ \t \r` |
| B8 | Anti-CSRF | `ApiEndpoints.cs` : `UseCsrfHeaderCheck` ; `Program.cs` : appelé après l'autorisation ; `rg -n "AddCors|UseCors" src/` | Présent ; aucune configuration CORS |

### C. Intégrité et traçabilité des données

| # | Point de contrôle | Où regarder | Attendu |
|---|---|---|---|
| C1 | Une seule suppression physique : la purge | `rg -n "DELETE FROM" src/` | Une seule occurrence : `PurgeExpired` |
| C2 | La purge ne touche que les incidents expirés | `PurgeExpired` : `WHERE i.DeletedAt IS NOT NULL AND i.DeletedAt <= $limit` avec `limit = maintenant - rétention` | Conforme |
| C3 | La purge n'est appelée que par la tâche de fond | `rg -n "PurgeExpired" src/` | Appelée uniquement dans `TrashPurgeService.cs` |
| C4 | Les suppressions utilisateur sont logiques | `TrashIncident` / `RemovePerson` : `UPDATE … SET DeletedAt` / `RemovedAt` | Conforme |
| C5 | Restauration limitée par le délai côté serveur | `RestoreIncident` : `DeletedAt + _retention <= Now` → Conflict | Conforme |
| C6 | Rétention minimale | `IncidentRepository` constructeur : `Math.Max(1, RetentionHours)` | ≥ 1 h |
| C7 | Chaque écriture est journalisée dans sa transaction | Chaque méthode d'écriture : `Audit(conn, …)` avant `tx.Commit()` | 8 méthodes d'écriture (`CreateIncident`, `SetClosed` pour la clôture et la réouverture, `TrashIncident`, `RestoreIncident`, `AddInternal`, `AddVisitor`, `RemovePerson`, `RestorePerson`), chacune avec son appel à `Audit`, plus 1 dans `PurgeExpired` : 9 appels au total (`rg -c "Audit\(conn" src/`) |
| C8 | Le journal n'est jamais modifié ni supprimé | `rg -n "AuditLog" src/` | Uniquement `CREATE TABLE`, `INSERT` (`Audit`) et `SELECT` (`ListAudit`) |
| C9 | Le journal survit à la purge | `Database.cs` : `AuditLog.IncidentId … ON DELETE SET NULL` | Conforme |
| C10 | Unicité des internes garantie par la base | `Database.cs` : index `UX_Persons_ActiveLogin` | Présent |
| C11 | Incident clôturé en lecture seule | `CheckWritable` appelé par `AddInternal`, `AddVisitor`, `RemovePerson`, `RestorePerson` | 4 appels |
| C12 | Pas de modification croisée entre incidents | `RemovePerson` / `RestorePerson` : `person.IncidentId != incidentId` → NotFound | Conforme |
| C13 | Aucune fonction « tout effacer » | `ApiEndpoints.cs` : liste des routes ([10](10-reference-api.md)) | Aucune route de suppression en masse |

### D. Annuaire

| # | Point de contrôle | Où regarder | Attendu |
|---|---|---|---|
| D1 | Aucun identifiant stocké | `AdDirectoryService.cs` : `new DirectoryEntry()` ou `new DirectoryEntry(SearchRoot)`, sans nom ni mot de passe ; `rg -ni "password" src/` | Aucune occurrence |
| D2 | Lecture seule | `AdDirectoryService.cs` : seulement `FindAll()` ; aucun `CommitChanges`, `Properties[…] =`, `Invoke` | Conforme |
| D3 | Attributs lus limités | Constante `Properties` | 6 attributs listés en [04](04-annuaire-ldap.md#ce-qui-est-lu) |
| D4 | Recherche bornée | `SizeLimit`, `ClientTimeout`, `ServerTimeLimit` | Présents |
| D5 | Annuaire fictif impossible en production par défaut | `appsettings.json` : `"Provider": "ActiveDirectory"` ; `Program.cs` : refus de démarrer hors Windows | Conforme |

### E. Déploiement

| # | Point de contrôle | Où regarder | Attendu |
|---|---|---|---|
| E1 | Moindre privilège NTFS | `Install-IIS.ps1` / `Install-Service.ps1` étape 6 | Lecture sur l'application ; modification sur `App_Data` uniquement |
| E2 | Identité du pool sans mot de passe | `Install-IIS.ps1` : `identityType = ApplicationPoolIdentity` | Conforme |
| E3 | Base jamais écrasée lors d'une mise à jour | `robocopy … /XD App_Data` dans les deux scripts | Conforme |
| E4 | Pare-feu limité au domaine | `Install-Service.ps1` : `-Profile Domain` | Conforme |
| E5 | Désinstallation sans perte involontaire | `Uninstall-*.ps1` : sauvegarde par défaut, `-NoBackup` explicite, confirmation par étape, `-WhatIf` | Conforme |
| E6 | Dépendances | `ComptageIncident.csproj` | 4 paquets Microsoft, versions figées |

## 3. Vérification sur le serveur installé

| Contrôle | Commande | Attendu |
|---|---|---|
| Accès anonyme refusé | `Invoke-WebRequest http://<site>/ -UseBasicParsing` (sans `-UseDefaultCredentials`) | Erreur **401** |
| Accès authentifié | `Invoke-WebRequest http://<site>/api/me -UseDefaultCredentials` | `{"name":"DOMAINE\\vous"}` |
| Anti-CSRF | `Invoke-WebRequest http://<site>/api/incidents -Method Post -UseDefaultCredentials -Body '{}' -ContentType application/json` | Erreur **400** « en-tête de sécurité manquant » |
| Fichier de développement absent | `Test-Path <dossier>\appsettings.Development.json` | `False` |
| Environnement | IIS : `web.config` › `ASPNETCORE_ENVIRONMENT` | `Production` |
| Droits | `icacls <dossier>` et `icacls <dossier>\App_Data` | Compte d'exécution : `(RX)` sur le dossier, `(M)` sur `App_Data` |
| Kerberos | `klist` sur un poste après connexion | Ticket `HTTP/<site>` |
| En-têtes | `(Invoke-WebRequest http://<site>/ -UseDefaultCredentials).Headers` | `Content-Security-Policy`, `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff` |
