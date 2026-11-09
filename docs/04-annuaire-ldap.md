# 04 · Annuaire LDAP / Active Directory

## Rôle de l'annuaire

L'annuaire sert **uniquement à proposer des suggestions** pendant la saisie d'un nom. L'application :
- **lit** quelques attributs des comptes utilisateurs ;
- **n'écrit jamais** dans l'annuaire ;
- **ne dépend pas** de l'annuaire pour fonctionner : si l'AD est injoignable, la recherche affiche « Annuaire indisponible ». Les personnes peuvent alors être ajoutées avec « Ajouter un visiteur », et les listes existantes restent consultables et modifiables.

## Quel compte interroge l'annuaire ?

Aucun identifiant n'est configuré. La connexion LDAP est authentifiée avec **l'identité du processus** de l'application (`Directory/AdDirectoryService.cs`, `new DirectoryEntry()` sans nom ni mot de passe, authentification `Secure` par défaut) :

| Mode d'hébergement | Compte vu par le contrôleur de domaine |
|---|---|
| IIS, pool en *ApplicationPoolIdentity* | Compte ordinateur du serveur `DOMAINE\SERVEUR$` |
| Service Windows en LocalSystem ou NetworkService | Compte ordinateur du serveur `DOMAINE\SERVEUR$` |
| Service Windows avec un compte de domaine | Ce compte |
| `ComptageIncident.exe` lancé dans une session | L'utilisateur de cette session |

**Pas de délégation** : la recherche n'est **pas** faite avec l'identité de l'utilisateur du navigateur. Il n'y a donc aucune délégation Kerberos à configurer, et tous les agents obtiennent les mêmes résultats.

**Droits nécessaires** : par défaut dans Active Directory, tout compte authentifié du domaine, y compris un compte ordinateur, peut lire les attributs utilisés. Aucun droit supplémentaire n'est à accorder. Si l'annuaire a été durci (lecture restreinte), il faut donner le droit de lecture de ces attributs au compte du tableau ci-dessus.

## Ce qui est lu

Liste exhaustive, définie par la constante `Properties` dans `AdDirectoryService.cs` :

| Attribut AD | Utilisation | Stocké en base ? |
|---|---|---|
| `sAMAccountName` | Identifiant, clé anti-doublon | Oui (`Persons.Login`) |
| `givenName` | Prénom | Oui |
| `sn` | Nom | Oui |
| `displayName` | Nom affiché | Oui |
| `department` | Service (affiché pour distinguer les homonymes) | Oui |
| `mail` | Renvoyé à l'interface, non affiché | **Non** |

Aucun autre attribut n'est demandé : ni groupes, ni téléphone, ni mot de passe, ni attribut sensible.

## Construction de la recherche

### Côté interface (`wwwroot/js/app.js`, section « Recherche dans l'annuaire »)
1. Moins de **4 caractères** (`Directory:MinSearchLength`) : aucune requête, un indice « Encore N caractère(s)… » s'affiche. Un caractère hors liste blanche (voir ci-dessous) affiche « Caractère non autorisé » et aucune requête n'est envoyée.
2. À partir de 4 caractères, attente de **250 ms** sans nouvelle frappe, puis appel de `GET /api/directory/search?q=…`.
3. Une nouvelle frappe **annule** la requête précédente (`AbortController`).

### Côté serveur
1. `ApiEndpoints` : la longueur minimale est **revérifiée** (400 si la saisie est trop courte), ainsi que la longueur maximale (100 caractères).
2. `NameRules` : la saisie est recomposée en forme NFC, puis comparée à une **liste blanche** : lettres A-Z a-z, lettres accentuées, espace, apostrophes `'` et `’`, trait d'union. Tout autre caractère (`*`, `(`, `)`, `\`, chiffres, `<`, `=`…) est refusé avec une erreur **400**, avant toute interrogation de l'annuaire (voir [06 · S20](06-securite.md#s20--liste-blanche-de-caractères-dans-les-noms-saisis)).
3. `DirectorySearch.Terms` : la saisie est découpée en mots, 4 au maximum.
4. `DirectorySearch.EscapeLdap` : chaque mot est **échappé** (voir ci-dessous).
5. `DirectorySearch.BuildFilter` : le filtre final est construit.

Exemple pour la saisie `dupo jea` :

```
(&
  (&(objectCategory=person)(objectClass=user)(!(userAccountControl:1.2.840.113556.1.4.803:=2)))   ← Directory:BaseFilter
  (|(sn=dupo*)(givenName=dupo*)(displayName=dupo*)(cn=dupo*))                                     ← mot 1
  (|(sn=jea*)(givenName=jea*)(displayName=jea*)(cn=jea*))                                         ← mot 2
)
```

Lecture : *utilisateur actif* **ET** *« dupo » commence le nom, le prénom, le nom affiché ou le CN* **ET** *« jea » commence l'un d'eux*. Cette saisie trouve « DUPONT Jean » mais pas « DUPOND Marie ».

Le filtre de base exclut les **comptes désactivés** : le bit 2 (`ACCOUNTDISABLE`) de `userAccountControl` est testé avec la règle « ET bit à bit » `1.2.840.113556.1.4.803`.

### Paramètres de la recherche (`AdDirectoryService.Search`)

| Paramètre | Valeur | Effet |
|---|---|---|
| `SizeLimit` | `Directory:MaxResults` (20) | Au plus 20 résultats |
| `ClientTimeout`, `ServerTimeLimit` | `Directory:TimeoutSeconds` (10 s) | La recherche abandonne au-delà de ce délai |
| `SearchScope` | `Subtree` | Toute l'arborescence sous la racine |
| `ReferralChasing` | `None` | Pas de suivi des renvois vers d'autres domaines |
| `Sort` | `sn` croissant | Tri par nom |

## Protection contre l'injection LDAP

Sans précaution, un utilisateur pourrait saisir `*` (tout l'annuaire) ou `)(…` pour modifier la logique du filtre. `DirectorySearch.EscapeLdap` applique la RFC 4515 :

| Caractère saisi | Remplacé par |
|---|---|
| `\` | `\5c` |
| `*` | `\2a` |
| `(` | `\28` |
| `)` | `\29` |
| NUL | `\00` |

Avec la liste blanche (`NameRules`), ces caractères ne peuvent d'ailleurs **plus atteindre** cette étape : l'échappement reste en place en **défense en profondeur**, au cas où la liste blanche serait un jour élargie. Le seul `*` du filtre est celui que **le code** ajoute en fin de mot (« commence par »). Le test `ApiTests.Filtre_ldap_echappe_les_caracteres_speciaux` vérifie le filtre produit pour la saisie `du*(pont)`.

À noter aussi :
- le **filtre de base** vient de la configuration (administrateur), jamais de l'utilisateur ;
- la longueur minimale de 4 caractères, appliquée côté serveur, empêche les recherches trop larges ;
- `SizeLimit` borne la quantité de données renvoyées.

## Ajout d'une personne interne

Quand l'agent clique sur une suggestion, l'interface envoie au serveur les informations **de la suggestion affichée** : identifiant, nom, prénom, nom affiché, service. Le serveur **ne réinterroge pas** l'annuaire. C'est un choix volontaire : l'ajout reste instantané même si l'AD ralentit pendant un incident.

Conséquence pour l'audit : un utilisateur authentifié qui appellerait l'API directement pourrait enregistrer un « interne » avec un nom arbitraire. C'est sans impact de sécurité, car il pourrait aussi bien saisir un visiteur. L'action reste **tracée à son nom** dans le journal.

## Personnaliser la recherche

| Besoin | Réglage dans `appsettings.json` |
|---|---|
| Limiter à une OU | `"SearchRoot": "LDAP://OU=Personnel,DC=entreprise,DC=local"` |
| Viser un contrôleur précis | `"SearchRoot": "LDAP://dc01.entreprise.local/DC=entreprise,DC=local"` |
| Exclure les comptes sans prénom (comptes techniques) | `"BaseFilter": "(&(objectCategory=person)(objectClass=user)(givenName=*)(!(userAccountControl:1.2.840.113556.1.4.803:=2)))"` |
| Exclure une OU de comptes de service | Utiliser `SearchRoot` sur l'OU du personnel |
| Plus de suggestions | `"MaxResults": 30` |

Redémarrer l'application après toute modification.

## Dépannage

| Symptôme | Cause probable | Vérification |
|---|---|---|
| « Annuaire indisponible » | Serveur hors domaine, contrôleur injoignable, port 389 filtré, `SearchRoot` erroné | Journal d'événements *Application* (source ASP.NET Core / .NET Runtime) : le message « Échec de la recherche LDAP » donne l'exception exacte |
| Aucun résultat pour une personne existante | Compte désactivé, hors de `SearchRoot`, ou nom ne commençant pas par la saisie | `Get-ADUser -Filter "sn -like 'dupo*'"` lancé sous le même compte |
| Fonctionne en exe dans une session, pas en service | En session, c'est l'utilisateur qui interroge l'AD ; en service, c'est le compte ordinateur, qui peut avoir moins de droits | Tester avec `PsExec -s powershell` puis `([adsisearcher]"(sn=dupo*)").FindAll()` |
