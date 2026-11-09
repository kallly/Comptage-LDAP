# 06 · Sécurité

Ce document recense les menaces prises en compte, les protections mises en place, **l'endroit exact du code** où chacune est implémentée et **le test** qui la vérifie. Il se termine par les limites connues et les recommandations de déploiement.

## Périmètre et hypothèses

- Application **interne**, accessible depuis le réseau de l'entreprise uniquement. Elle n'est pas conçue pour être exposée sur Internet.
- Serveur Windows **membre du domaine**, administré par l'équipe informatique.
- Utilisateurs : comptes du domaine. Tout utilisateur authentifié a les mêmes droits (choix fonctionnel validé).

## Synthèse : menaces et protections

| # | Menace | Protection | Où dans le code | Vérifié par |
|---|---|---|---|---|
| S1 | Accès sans être authentifié | Authentification Windows obligatoire sur **tout** (page, JS, CSS, API) | `Program.cs` : `AddNegotiate()`, `FallbackPolicy = DefaultPolicy`, `UseAuthorization()` avant `UseStaticFiles()` / `MapApi()` | `ProductionSecurityTests` (vraie application, Kestrel, environnement Production) |
| S2 | Usurpation d'identité dans les données (« ajouté par ») | Identité prise uniquement dans le contexte authentifié, jamais dans la requête | `ApiEndpoints.UserName` | Lecture du code : aucune route ne lit un nom d'utilisateur dans le corps |
| S3 | Activation du mode développement (faux utilisateur) en production | Enregistré seulement si `Development` ; `web.config` force `Production` ; fichier de développement non publié | `Program.cs` section 3 ; `web.config` ; `ComptageIncident.csproj` (`CopyToPublishDirectory="Never"`) | `ProductionSecurityTests` (avec `Auth:DevUser` et `X-Dev-User` renseignés : refus) |
| S4 | Vol ou rejeu de mot de passe | Aucun mot de passe saisi, transmis ou stocké : Kerberos / NTLM via SSPI ; LDAP avec l'identité du processus | `Program.cs` ; `AdDirectoryService` (`new DirectoryEntry()` sans identifiants) | Recherche de `password` / `Password` dans `src/` : aucune occurrence |
| S5 | CSRF : un site malveillant déclenche une action avec l'identité Windows de l'agent | En-tête personnalisé obligatoire sur toute requête de modification ; aucune configuration CORS | `ApiEndpoints.UseCsrfHeaderCheck` ; absence de `AddCors` | `ApiTests.Requete_de_modification_sans_entete_csrf_est_refusee` |
| S6 | Injection SQL | Requêtes 100 % paramétrées (`$param`), aucune concaténation de saisie | `IncidentRepository.Command` (`AddWithValue`) | `DataSecurityTests.Une_tentative_d_injection_SQL_est_stockee_comme_du_texte` |
| S7 | Injection LDAP | Échappement RFC 4515 de chaque mot ; filtre de base issu de la configuration | `DirectorySearch.EscapeLdap`, `BuildFilter` | `ApiTests.Filtre_ldap_echappe_les_caracteres_speciaux` |
| S8 | XSS (script injecté via un nom ou un titre) | Tout texte inséré avec `textContent` ; CSP `script-src 'self'` sans script en ligne | `app.js` : fonction `h()` ; `Program.cs` : en-tête CSP | `DataSecurityTests.Le_HTML_saisi_est_renvoye_tel_quel…` + lecture de `app.js` (seul `innerHTML` : icône constante) |
| S9 | Injection de formules dans l'export CSV (Excel) | Préfixe `'` devant `= + - @` (tabulation, CR) ; guillemets | `ApiEndpoints.Csv` | `DataSecurityTests.L_export_CSV_neutralise_les_formules_Excel` |
| S10 | Clickjacking (application intégrée dans un cadre piégé) | `X-Frame-Options: DENY`, CSP `frame-ancestors 'none'` | `Program.cs` 6.a | `ProductionSecurityTests.Les_entetes_de_securite_sont_presents…` |
| S11 | Perte de données par erreur de manipulation | Aucune fonction « tout effacer » ; popups de confirmation ; suppression logique ; corbeille 24 h ; réintégration | `IncidentRepository` (R1, R2) ; `app.js` `confirmDialog` | `RepositoryTests` (corbeille, purge, réintégration) |
| S12 | Modification sans trace | Journal écrit dans la même transaction que la modification ; journal en ajout seul ; conservé après purge | `IncidentRepository.Audit` ; `AuditLog ON DELETE SET NULL` | `RepositoryTests.Chaque_action_est_journalisee` |
| S13 | Modification croisée (personne d'un incident modifiée via un autre) | Vérification `person.IncidentId == incidentId` | `RemovePerson`, `RestorePerson` | `DataSecurityTests.Une_personne_ne_peut_pas_etre_retiree_via_un_autre_incident` |
| S14 | Saisie de données démesurées / déni de service | Longueurs maximales (titre 200, champs 256, recherche 100) ; 4 caractères minimum pour la recherche ; 20 résultats max ; délai LDAP 10 s ; requêtes limitées à 1 Mo sous IIS | `ApiEndpoints` ; `AppOptions` ; `web.config` (`requestLimits`) | `DataSecurityTests.Les_champs_trop_longs_sont_refuses`, `ApiTests.Recherche_annuaire_exige_4_caracteres` |
| S15 | Fuite d'informations techniques | Messages d'erreur génériques en français pour l'utilisateur ; détails dans le journal du serveur uniquement ; en-tête `X-Powered-By` retiré (IIS) ; page d'exception développeur seulement en Development | `ApiEndpoints` (annuaire : 503 générique) ; `web.config` | Lecture du code |
| S16 | Mise en cache de listes nominatives | `Cache-Control: no-store` sur l'API | `Program.cs` 6.a | Lecture du code |
| S17 | Dépendances vulnérables | 4 paquets Microsoft, versions figées et à jour ; runtime .NET 10 LTS ; aucune bibliothèque JavaScript | `ComptageIncident.csproj` | `dotnet list package --vulnerable --include-transitive` (aucune vulnérabilité à la date de livraison) |
| S18 | Ressources externes compromises (CDN) | Aucune ressource externe ; CSP `default-src 'self'` | `index.html`, `Program.cs` | Lecture de `index.html` |
| S19 | Droits excessifs du compte d'exécution | Pool en ApplicationPoolIdentity (compte virtuel) ; écriture limitée à `App_Data` ; aucun droit d'écriture dans l'AD | `Install-IIS.ps1` étape 6 ; `Install-Service.ps1` étape 6 | Lecture des scripts / `icacls` sur le serveur |

## Détail de quelques protections

### S5 : pourquoi l'en-tête `X-Requested-With` suffit contre le CSRF

Le navigateur joint **automatiquement** l'authentification Windows à toute requête vers le site, y compris une requête déclenchée par une autre page (formulaire caché, image, `fetch`). Sans protection, un site malveillant visité par un agent pourrait donc supprimer un incident à son insu.

Or :
- un formulaire HTML ou une image ne peuvent pas ajouter d'en-tête personnalisé ;
- un `fetch` d'un autre site qui ajoute un en-tête personnalisé déclenche d'abord une requête de contrôle CORS (*preflight*). L'application n'ayant **aucune politique CORS**, le navigateur bloque la requête.

Toute requête de modification qui arrive **sans** l'en-tête est rejetée (400) par `UseCsrfHeaderCheck`. Les requêtes `GET` ne modifient rien et ne sont donc pas concernées. Un autre site ne peut d'ailleurs pas lire leur réponse (politique de même origine).

### S8 : affichage sûr dans l'interface

Dans `wwwroot/js/app.js`, tout le contenu dynamique passe par `h(tag, attrs, ...children)` :
- l'attribut `text` utilise `el.textContent = value` ;
- les enfants de type chaîne deviennent des `document.createTextNode(...)`.

Aucune donnée n'est donc interprétée comme du HTML. **Vérification** : `innerHTML` n'apparaît qu'une fois, pour l'icône de loupe, dont le contenu est une constante du fichier. La CSP (`script-src 'self'`) bloquerait de toute façon l'exécution d'un script injecté.

### S11 / S12 : intégrité des listes

- Un clic accidentel sur « Supprimer » est intercepté par une popup dont le **bouton par défaut est « Annuler »** (`confirmDialog` place le focus sur Annuler).
- Même confirmée, la suppression d'une personne est **réversible** (Personnes retirées › Réintégrer), et celle d'un incident l'est pendant 24 h.
- Chaque action est horodatée et attribuée dans le journal.

## Limites connues et recommandations

| Sujet | Situation | Recommandation |
|---|---|---|
| **Chiffrement du transport** | Par défaut, le site est en **HTTP**. Kerberos / NTLM protègent l'**authentification** (aucun mot de passe en clair), mais le **contenu** (listes nominatives) circule en clair sur le réseau interne | **Activer HTTPS** avec un certificat de l'autorité interne : liaison HTTPS sous IIS ([07](07-installation-iis.md#https-recommandé)) ou endpoint HTTPS Kestrel ([08](08-installation-autonome.md#https)). Puis rediriger HTTP vers HTTPS ou fermer le port HTTP |
| **Autorisation** | Tout compte authentifié du domaine peut consulter et modifier (choix validé) | Si besoin, restreindre à un groupe AD : voir ci-dessous |
| **NTLM** | Accepté en repli si Kerberos échoue | Déclarer les SPN ([03](03-authentification.md#kerberos-et-spn)) ; NTLM peut être désactivé par GPO si la politique de l'entreprise l'exige |
| **Données ajoutées via l'API** | Un utilisateur authentifié peut enregistrer un « interne » avec des données arbitraires en appelant l'API directement | Impact limité (équivalent à saisir un visiteur), action tracée à son nom |
| **Conservation** | Les incidents ne sont pas effacés automatiquement (hors corbeille) | Définir une durée de conservation et supprimer les incidents anciens ([05](05-donnees-et-corbeille.md#données-personnelles)) |
| **Protection du fichier de base** | Le fichier `comptage.db` n'est pas chiffré | Accès NTFS restreint (Administrateurs, compte d'exécution) ; activer BitLocker sur le serveur ; protéger les sauvegardes |
| **Disponibilité** | Un seul serveur | Prévoir l'impression ou l'export CSV régulier pendant un incident long. Le serveur doit être alimenté (onduleur) s'il doit servir pendant un incendie |

### Restreindre l'accès à un groupe AD

Deux options, au choix :

1. **Sans modifier le code (IIS uniquement)** : *Règles d'autorisation* IIS sur le site (fonctionnalité « Autorisation URL ») → supprimer « Tous les utilisateurs », ajouter « Autoriser » pour le groupe `DOMAINE\GRP-Secours`.
2. **Dans le code (IIS et autonome)**, dans `Program.cs`, remplacer la règle d'autorisation par :
   ```csharp
   builder.Services.AddAuthorization(o => o.FallbackPolicy =
       new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireRole(@"DOMAINE\GRP-Secours").Build());
   ```
   Sur un serveur Windows, IIS comme Kestrel fournissent les groupes de l'utilisateur, issus de son jeton Windows, sous forme de rôles. `RequireRole` les vérifie donc directement, sans requête LDAP supplémentaire. Cette modification n'est pas livrée par défaut, car elle n'a pas été demandée.

## Signaler un problème de sécurité

Les tests de sécurité se relancent avec `dotnet test` ([13](13-tests-et-audit.md)). Toute modification du code doit les laisser au vert.
