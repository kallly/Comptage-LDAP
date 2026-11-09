# 01 · Présentation

## Objectif

Lors d'un incident sur site (incendie, fuite de produit chimique, exercice d'évacuation…), les responsables de l'évacuation doivent savoir **rapidement et de façon fiable qui est présent** au point de rassemblement. Le but est de pouvoir signaler les personnes manquantes aux secours.

*Comptage incident* est une application Web interne qui permet à un ou plusieurs agents, depuis un PC, une tablette ou un smartphone, de :
- pointer les **personnes internes** en les cherchant dans l'annuaire de l'entreprise (Active Directory) ;
- ajouter **manuellement les visiteurs** et toute personne absente de l'annuaire ;
- suivre en temps réel les **totaux** par catégorie et le total général ;
- conserver une **trace** de chaque incident (historique, journal, export, impression).

## Fonctionnalités

| Besoin exprimé | Réalisation |
|---|---|
| Accès interne avec authentification Windows automatique | Authentification « Negotiate » (Kerberos / NTLM) : l'utilisateur est reconnu par sa session, sans saisie ([03](03-authentification.md)) |
| Recherche des personnes dans le LDAP par nom ou prénom | Suggestions à partir de **4 caractères**, chaque mot devant commencer le nom ou le prénom ([04](04-annuaire-ldap.md)) |
| Un clic ajoute la personne, avec popup de validation | Popup « Ajouter cette personne ? » avant tout ajout |
| Bouton Supprimer avec popup de validation | Popup « Retirer cette personne ? ». La personne reste réintégrable en cas d'erreur |
| Bouton d'ajout supplémentaire pour les visiteurs (nom complet saisi) | Bouton « + Ajouter un visiteur » : nom complet uniquement, puis confirmation |
| Listes triées par ordre alphabétique, internes et visiteurs séparés | Deux listes distinctes, triées par nom puis prénom, sans tenir compte des accents ni de la casse |
| Nombre total de chaque liste affiché | Total des internes, total des visiteurs et total général |
| Pas de bouton qui efface tout par erreur | **Aucune** fonction « tout effacer ». Plusieurs incidents sont conservés dans une base SQLite locale |
| Suppression vers une corbeille d'un jour | « Supprimer l'incident » le place en corbeille, **restaurable pendant 24 h**, puis il est purgé automatiquement ([05](05-donnees-et-corbeille.md)) |

Fonctions complémentaires ajoutées pour l'usage en situation réelle :
- **Clôture** d'un incident : les listes passent en lecture seule. L'incident peut être rouvert.
- **Journal d'audit** : qui a fait quoi, et quand.
- **Impression** et **export CSV** (Excel), utiles si le réseau devient indisponible.
- **Rafraîchissement automatique** toutes les 5 s, pour que plusieurs agents puissent compter sur le même incident.
- **Indication « déjà comptée »** dans les suggestions, et refus des doublons par le serveur.
- **Fonctionnement hors Internet** : aucune ressource externe (CDN, police, etc.).

## Ce que l'application ne fait pas

- Elle **ne déduit pas** qui est sur site (pas de lien avec le contrôle d'accès ou la badgeuse). Elle compte uniquement les personnes **pointées** par les agents.
- Elle **n'écrit rien** dans l'annuaire.
- Elle **ne gère pas de rôles** : tout utilisateur authentifié du domaine peut consulter et modifier (choix validé ; voir les limites en [06](06-securite.md#limites-connues-et-recommandations)).

## Vocabulaire

| Terme | Définition |
|---|---|
| Incident | Événement pour lequel on compte les personnes (titre, type, lieu de rassemblement) |
| Interne | Personne présente dans l'annuaire Active Directory, identifiée par son identifiant Windows (`sAMAccountName`) |
| Visiteur | Personne saisie manuellement (nom complet), sans lien avec l'annuaire |
| Retirer / réintégrer | Enlever une personne de la liste (suppression **logique**), puis la remettre |
| Corbeille | Liste des incidents supprimés depuis moins de 24 h, restaurables |
| Purge | Suppression **définitive** automatique d'un incident resté plus de 24 h en corbeille |
| Clôture | Passage d'un incident en lecture seule |
| Journal d'audit | Historique horodaté de toutes les actions, avec l'identité Windows de leur auteur |
| Negotiate | Mécanisme d'authentification Windows intégrée (Kerberos, ou NTLM en repli) |
| SPN | *Service Principal Name* : nom sous lequel un service est connu de Kerberos (ex. `HTTP/comptage.entreprise.local`) |
