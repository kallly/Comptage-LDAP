# 10 · Référence de l'API

Base : `/api`. Format : JSON (UTF-8), propriétés en *camelCase*. Implémentation : `src/ComptageIncident/Api/ApiEndpoints.cs`.

## Règles communes

| Règle | Détail |
|---|---|
| Authentification | **Obligatoire** sur toutes les routes (Windows / Negotiate). Sinon : `401` + `WWW-Authenticate: Negotiate` |
| Anti-CSRF | Toute requête **POST / DELETE** doit porter l'en-tête `X-Requested-With: ComptageIncident`. Sinon : `400` |
| Identité | L'auteur des actions est l'utilisateur authentifié. Aucun champ « utilisateur » n'est accepté en entrée |
| Erreurs | Corps `{ "error": "message en français" }` |
| Dates | ISO-8601 UTC (`2026-11-09T22:17:49.785Z`) |
| Cache | `Cache-Control: no-store` sur toutes les réponses |

| Code | Signification |
|---|---|
| 200 | Succès |
| 201 | Créé (incident, personne) |
| 400 | Données invalides, ou en-tête anti-CSRF absent |
| 401 | Non authentifié |
| 404 | Incident ou personne inexistant, ou incident en corbeille |
| 409 | Règle métier : doublon, incident clôturé, déjà clôturé ou ouvert, délai de restauration dépassé |
| 503 | Annuaire indisponible |

## Objets

**Incident**
```json
{
  "id": 1, "title": "Incendie bâtiment B", "type": "Incendie", "location": "Parking nord",
  "createdAt": "2026-11-09T22:17:49.785Z", "createdBy": "ENTREPRISE\\jdupont",
  "closedAt": null, "closedBy": null, "deletedAt": null, "deletedBy": null,
  "internalCount": 5, "visitorCount": 2, "totalCount": 7, "isClosed": false
}
```

**Personne**
```json
{
  "id": 12, "incidentId": 1, "kind": "internal",
  "displayName": "DUPONT Jean", "lastName": "Dupont", "firstName": "Jean",
  "login": "jdupont", "department": "Production", "company": null,
  "addedAt": "2026-11-09T22:18:02.101Z", "addedBy": "ENTREPRISE\\mmartin",
  "removedAt": null, "removedBy": null
}
```
Pour un visiteur : `kind` vaut `"visitor"` ; `lastName`, `firstName`, `login` et `department` valent `null` ; `company` est éventuellement renseigné.

## Routes

### Informations

| Méthode | Route | Réponse |
|---|---|---|
| GET | `/api/me` | `{ "name": "ENTREPRISE\\jdupont" }` |
| GET | `/api/config` | `{ "minSearchLength": 4, "trashRetentionHours": 24, "incidentTypes": ["Incendie","Fuite chimique","Exercice","Autre"] }` |

### Incidents

| Méthode | Route | Corps | Réponses |
|---|---|---|---|
| GET | `/api/incidents` | — | `200` liste d'Incident (hors corbeille) : en cours d'abord, puis du plus récent au plus ancien |
| POST | `/api/incidents` | `{ "title": "…", "type": "Incendie", "location": "…" }` | `201` Incident ; `400` si titre vide ou > 200, type hors liste, lieu > 200 |
| GET | `/api/incidents/{id}` | — | `200` `{ "incident": Incident, "persons": [Personne active…], "removed": [Personne retirée…] }` ; `404` |
| POST | `/api/incidents/{id}/close` | — | `200` Incident ; `404` ; `409` déjà clôturé |
| POST | `/api/incidents/{id}/reopen` | — | `200` Incident ; `404` ; `409` déjà ouvert |
| DELETE | `/api/incidents/{id}` | — | `200` Incident (avec `deletedAt`), **mis en corbeille** ; `404` |
| GET | `/api/incidents/{id}/audit` | — | `200` `[{ "id", "incidentId", "at", "user", "action", "details" }]`, du plus récent au plus ancien ; `404` |
| GET | `/api/incidents/{id}/export.csv` | — | `200` fichier CSV (UTF-8 avec BOM, séparateur `;`) ; `404` |

### Corbeille

| Méthode | Route | Réponses |
|---|---|---|
| GET | `/api/trash` | `200` `[{ "incident": Incident, "purgeAt": "date de suppression définitive" }]` |
| POST | `/api/trash/{id}/restore` | `200` Incident ; `404` pas dans la corbeille ; `409` délai dépassé |

### Personnes

| Méthode | Route | Corps | Réponses |
|---|---|---|---|
| POST | `/api/incidents/{id}/persons/internal` | `{ "login": "jdupont", "firstName": "Jean", "lastName": "Dupont", "displayName": "DUPONT Jean", "department": "Production" }` (seul `login` est obligatoire) | `201` Personne ; `400` login manquant ou champ > 256 ; `404` ; `409` déjà comptée ou incident clôturé |
| POST | `/api/incidents/{id}/persons/visitor` | `{ "fullName": "Paul Durand", "company": "ACME" }` | `201` Personne ; `400` nom < 2 caractères ou champ > 256 ; `404` ; `409` incident clôturé |
| DELETE | `/api/incidents/{id}/persons/{personId}` | — | `200` Personne (avec `removedAt`), **retrait logique** ; `404` (personne absente, déjà retirée ou d'un autre incident) ; `409` incident clôturé |
| POST | `/api/incidents/{id}/persons/{personId}/restore` | — | `200` Personne ; `404` ; `409` de nouveau comptée entre-temps, ou incident clôturé |

### Annuaire

| Méthode | Route | Réponses |
|---|---|---|
| GET | `/api/directory/search?q=dupo jea` | `200` `[{ "login", "firstName", "lastName", "displayName", "department", "email" }]` (20 au plus) ; `400` si moins de 4 ou plus de 100 caractères ; `503` annuaire indisponible |

## Exemple d'appel en PowerShell

```powershell
$base = 'http://comptage.entreprise.local'
# Lecture : l'identité Windows de la session est utilisée
Invoke-RestMethod "$base/api/incidents" -UseDefaultCredentials

# Écriture : en-tête anti-CSRF obligatoire
Invoke-RestMethod "$base/api/incidents" -Method Post -UseDefaultCredentials `
  -Headers @{ 'X-Requested-With' = 'ComptageIncident' } -ContentType 'application/json' `
  -Body (@{ title = 'Exercice annuel'; type = 'Exercice'; location = 'Parking nord' } | ConvertTo-Json)
```
