/* =====================================================================================================
 *  wwwroot/js/app.js : interface Web de « Comptage incident »
 * =====================================================================================================
 *  Application monopage (SPA) écrite en JavaScript natif, sans bibliothèque ni ressource externe :
 *  elle fonctionne sur un réseau interne coupé d'Internet et respecte la politique de sécurité du
 *  contenu (CSP) définie dans Program.cs (aucun script en ligne, aucun CDN).
 *
 *  Organisation du fichier :
 *    1. Utilitaires (création d'éléments DOM sûre, formats de date, notifications)
 *    2. Appels à l'API (ajout systématique de l'en-tête anti-CSRF)
 *    3. Popups (confirmation, formulaires) basées sur l'élément natif <dialog>
 *    4. Routage par « hash » : #/  #/corbeille  #/incident/{id}
 *    5. Écran « Incidents »
 *    6. Écran « Corbeille »
 *    7. Écran « Incident » (compteurs, recherche annuaire, listes, actions, rafraîchissement auto)
 *    8. Fonctions d'affichage des personnes (nom, tri alphabétique)
 *    9. Démarrage
 *
 *  SÉCURITÉ (anti-XSS) : tout texte provenant du serveur ou de l'utilisateur (noms, titres…) est inséré
 *  avec « textContent » via la fonction h(). Le navigateur l'affiche comme du texte brut, jamais comme du
 *  HTML : un nom contenant « <script> » s'affiche tel quel, sans être exécuté. La seule utilisation
 *  d'innerHTML concerne l'icône de loupe, dont le contenu est une constante écrite dans ce fichier.
 *
 *  L'interface n'est qu'une commodité d'affichage : TOUTES les règles (authentification, doublons,
 *  incident clôturé, délai de corbeille, longueur minimale de recherche…) sont vérifiées par le serveur.
 *  Détails : docs/02-architecture.md et docs/11-guide-utilisateur.md
 * ===================================================================================================== */
'use strict';

// Tout le code est encapsulé dans une fonction immédiatement exécutée : aucune variable globale n'est créée.
(() => {
  // ================================================================================================
  //  1. Utilitaires
  // ================================================================================================

  /** Raccourci de document.querySelector. */
  const $ = (sel, root = document) => root.querySelector(sel);

  /**
   * Crée un élément DOM.
   *   h('a', { class: 'btn', href: '#/', text: 'Retour' }, enfant1, enfant2…)
   * - attribut « text »  : inséré via textContent (JAMAIS interprété comme du HTML : protection anti-XSS) ;
   * - attributs « onxxx » avec une fonction : écouteur d'événement ;
   * - enfants : nœuds DOM ou chaînes (les chaînes deviennent des nœuds texte, donc également sûres) ;
   * - les valeurs null / undefined / false sont ignorées (pratique pour les éléments conditionnels).
   */
  function h(tag, attrs, ...children) {
    const el = document.createElement(tag);
    for (const [key, value] of Object.entries(attrs || {})) {
      if (value === null || value === undefined || value === false) continue;
      if (key === 'class') el.className = value;
      else if (key === 'text') el.textContent = value;
      else if (key.startsWith('on') && typeof value === 'function') el.addEventListener(key.slice(2), value);
      else el.setAttribute(key, value === true ? '' : value);
    }
    for (const child of children.flat()) {
      if (child === null || child === undefined || child === false) continue;
      el.append(child instanceof Node ? child : document.createTextNode(String(child)));
    }
    return el;
  }

  // Formats de date français. Le serveur envoie des dates UTC ; le navigateur les convertit en heure locale.
  const dateFmt = new Intl.DateTimeFormat('fr-FR', { dateStyle: 'short', timeStyle: 'short' });
  const timeFmt = new Intl.DateTimeFormat('fr-FR', { hour: '2-digit', minute: '2-digit' });
  const fmtDate = (iso) => (iso ? dateFmt.format(new Date(iso)) : '');
  const fmtTime = (iso) => (iso ? timeFmt.format(new Date(iso)) : '');

  // Comparateur alphabétique français : ignore la casse et les accents (« Émilie » est rangée avec « Emilie »).
  const collator = new Intl.Collator('fr', { sensitivity: 'base', numeric: true });

  /**
   * Caractères autorisés dans les noms SAISIS (recherche annuaire, nom d'un visiteur) : lettres A-Z a-z,
   * lettres accentuées (Latin-1, Latin étendu A, ș ț roumains), espace, apostrophes ' et ’, trait d'union.
   * Copie de la règle serveur Api/NameRules.cs, qui fait foi : ici, elle sert seulement à prévenir
   * l'utilisateur avant l'envoi. La saisie est d'abord recomposée en forme NFC (accents saisis en deux
   * caractères par certains claviers).
   */
  const NAME_PATTERN = /^[A-Za-zÀ-ÖØ-öø-ÿ\u0100-\u017F\u0218-\u021B'’\- ]+$/u;
  const NAME_LETTER = /[A-Za-zÀ-ÖØ-öø-ÿ\u0100-\u017F\u0218-\u021B]/u;
  const NAME_HELP = 'Lettres (accents compris), espace, apostrophe et trait d\'union uniquement.';
  const normalizeName = (v) => (v || '').normalize('NFC').trim();
  const isValidName = (v) => NAME_PATTERN.test(v) && NAME_LETTER.test(v);

  /** « 1 personne » / « 3 personnes ». */
  function plural(n, one, many) { return `${n} ${n > 1 ? many : one}`; }

  /** Temps restant avant une date, en clair (« dans 23 h 05 »). Utilisé pour la corbeille. */
  function remaining(iso) {
    const ms = new Date(iso) - Date.now();
    if (ms <= 0) return 'imminente';
    const totalMin = Math.round(ms / 60000);
    const hours = Math.floor(totalMin / 60);
    const minutes = totalMin % 60;
    return hours > 0 ? `dans ${hours} h ${String(minutes).padStart(2, '0')}` : `dans ${minutes} min`;
  }

  /** Notification temporaire en bas de l'écran (kind : '' neutre, 'ok' succès, 'error' erreur). */
  function toast(message, kind = '') {
    const el = h('div', { class: `toast ${kind}`, role: kind === 'error' ? 'alert' : 'status', text: message });
    $('#toasts').append(el);
    setTimeout(() => el.remove(), kind === 'error' ? 6000 : 3500);
  }

  // ================================================================================================
  //  2. Appels à l'API
  // ================================================================================================

  /** Erreur renvoyée par l'API, avec le message en français fourni par le serveur et le code HTTP. */
  class ApiError extends Error {
    constructor(message, status) { super(message); this.status = status; }
  }

  /**
   * Appelle l'API.
   * - credentials 'same-origin' : le navigateur joint l'authentification Windows (Negotiate) automatiquement.
   * - Pour toute requête autre que GET, ajoute l'en-tête anti-CSRF X-Requested-With exigé par le serveur
   *   (voir ApiEndpoints.UseCsrfHeaderCheck).
   * - En cas d'erreur, lève une ApiError contenant le message renvoyé par le serveur ({ error: "…" }).
   * - « signal » permet d'annuler une requête devenue inutile (recherche remplacée par une plus récente).
   */
  async function api(method, url, body, signal) {
    const options = { method, headers: { Accept: 'application/json' }, credentials: 'same-origin', signal };
    if (method !== 'GET') options.headers['X-Requested-With'] = 'ComptageIncident';
    if (body !== undefined) {
      options.headers['Content-Type'] = 'application/json';
      options.body = JSON.stringify(body);
    }
    let response;
    try {
      response = await fetch(url, options);
    } catch (e) {
      if (e.name === 'AbortError') throw e; // annulation volontaire : propagée telle quelle
      throw new ApiError('Serveur injoignable. Vérifiez la connexion réseau.', 0);
    }
    const text = await response.text();
    let data = null;
    if (text) { try { data = JSON.parse(text); } catch { data = null; } }
    if (!response.ok) {
      const message = (data && data.error) || (response.status === 401 ? 'Authentification Windows requise.' : `Erreur ${response.status}`);
      throw new ApiError(message, response.status);
    }
    return data;
  }

  // ================================================================================================
  //  3. Popups
  // ================================================================================================

  /**
   * Popup de confirmation (ajout, suppression, clôture…). Renvoie une promesse résolue à :
   *   true  si l'utilisateur clique sur le bouton de validation ;
   *   false s'il clique sur « Annuler » ou appuie sur Échap.
   * Les boutons de #confirm-dialog (index.html) sont dans un <form method="dialog"> : un clic ferme la
   * popup et place la « value » du bouton (ok / cancel) dans dialog.returnValue.
   * « body » : texte, nœud DOM, ou tableau mêlant les deux (chaque texte devient un paragraphe).
   */
  function confirmDialog({ title, body, okText = 'Valider' }) {
    const dialog = $('#confirm-dialog');
    $('#confirm-title').textContent = title;
    const msg = $('#confirm-message');
    msg.replaceChildren(...(Array.isArray(body) ? body : [body]).map((b) => (b instanceof Node ? b : h('p', { text: b }))));
    $('#confirm-ok').textContent = okText;
    dialog.returnValue = '';
    dialog.showModal();
    // Le focus initial est placé sur « Annuler » : un appui involontaire sur Entrée n'effectue aucune action.
    $('#confirm-cancel').focus();
    return new Promise((resolve) => {
      dialog.addEventListener('close', () => resolve(dialog.returnValue === 'ok'), { once: true });
    });
  }

  /**
   * Ouvre une popup contenant un formulaire (nouvel incident, nouveau visiteur).
   * Le formulaire est vidé à chaque ouverture. À la validation, les règles HTML (required, minlength,
   * maxlength) sont vérifiées par le navigateur, la popup est fermée puis onSubmit(valeurs) est appelé.
   */
  function openFormDialog(dialog, onSubmit) {
    const form = $('form', dialog);
    form.reset();
    // reset() n'efface pas les erreurs personnalisées (setCustomValidity) : on les remet à zéro.
    form.querySelectorAll('input').forEach((i) => i.setCustomValidity(''));
    form.onsubmit = async (event) => {
      event.preventDefault();
      if (!form.reportValidity()) return;
      const values = Object.fromEntries(new FormData(form).entries());
      dialog.close();
      await onSubmit(values);
    };
    dialog.querySelectorAll('[data-close]').forEach((b) => { b.onclick = () => dialog.close(); });
    dialog.showModal();
    const first = form.querySelector('input, select');
    if (first) first.focus();
  }

  /** Vrai si une popup est ouverte (le rafraîchissement automatique est alors suspendu). */
  const anyDialogOpen = () => !!document.querySelector('dialog[open]');

  // ================================================================================================
  //  4. Routage
  // ================================================================================================
  //  L'écran affiché dépend de la partie « # » de l'adresse :
  //    #/               liste des incidents
  //    #/corbeille      corbeille
  //    #/incident/12    incident n°12
  //  Le bouton « Précédent » du navigateur fonctionne donc normalement, et une adresse d'incident peut
  //  être partagée entre agents.

  const app = $('#app');
  /** Paramètres reçus de /api/config au démarrage (valeurs par défaut en attendant). */
  let config = { minSearchLength: 4, trashRetentionHours: 24, incidentTypes: [] };
  /** Fonction de nettoyage de l'écran courant (arrêt des minuteurs, annulation des requêtes). */
  let pageCleanup = null;

  /** Remplace le contenu de la page, nettoie l'écran précédent et met en évidence le menu actif. */
  function setPage(nav, ...content) {
    if (pageCleanup) { pageCleanup(); pageCleanup = null; }
    document.querySelectorAll('[data-nav]').forEach((a) => a.classList.toggle('active', a.dataset.nav === nav));
    app.replaceChildren(...content);
  }

  /** Affiche l'écran correspondant à l'adresse courante. */
  async function route() {
    const hash = location.hash || '#/';
    const incidentMatch = hash.match(/^#\/incident\/(\d+)$/);
    try {
      if (incidentMatch) await renderIncident(Number(incidentMatch[1]));
      else if (hash === '#/corbeille') await renderTrash();
      else await renderIncidents();
    } catch (e) {
      setPage('', h('div', { class: 'card empty' },
        h('p', { text: e.message || 'Erreur inattendue.' }),
        h('a', { class: 'btn', href: '#/', text: 'Retour aux incidents' })));
    }
  }

  // ================================================================================================
  //  5. Écran « Incidents »
  // ================================================================================================

  /** Liste des incidents : « En cours » puis « Clôturés », avec bouton de création. */
  async function renderIncidents() {
    const incidents = await api('GET', '/api/incidents');
    const open = incidents.filter((i) => !i.isClosed);
    const closed = incidents.filter((i) => i.isClosed);

    const newButton = h('button', { class: 'btn btn-primary btn-lg', type: 'button', onclick: createIncident }, '+ Nouvel incident');

    setPage('incidents',
      h('div', { class: 'page-head' },
        h('div', {},
          h('h1', { text: 'Incidents' }),
          h('p', { class: 'sub', text: 'Sélectionnez un incident pour compter les personnes présentes.' })),
        newButton),
      h('h2', { class: 'section', text: `En cours (${open.length})` }),
      incidentList(open, 'Aucun incident en cours.'),
      closed.length ? h('h2', { class: 'section', text: `Clôturés (${closed.length})` }) : null,
      closed.length ? incidentList(closed) : null);
  }

  /** Cartes cliquables des incidents (titre, statut, type, lieu, créateur, compteurs). */
  function incidentList(items, emptyText) {
    if (!items.length) return h('div', { class: 'card empty', text: emptyText });
    return h('div', { class: 'incident-list' }, items.map((i) =>
      h('a', { class: 'card incident-item', href: `#/incident/${i.id}` },
        h('div', {},
          h('h3', {},
            i.title,
            h('span', { class: `badge ${i.isClosed ? 'badge-closed' : 'badge-open'}`, text: i.isClosed ? 'Clôturé' : 'En cours' }),
            h('span', { class: 'badge', text: i.type })),
          h('div', { class: 'meta', text: [
            i.location ? `📍 ${i.location}` : null,
            `Créé le ${fmtDate(i.createdAt)} par ${i.createdBy}`,
          ].filter(Boolean).join(' · ') })),
        counts(i))));
  }

  /** Bloc « internes / visiteurs / total » d'une carte d'incident. */
  function counts(i) {
    return h('div', { class: 'incident-counts' },
      h('div', {}, h('b', { text: i.internalCount }), h('span', { text: 'internes' })),
      h('div', {}, h('b', { text: i.visitorCount }), h('span', { text: 'visiteurs' })),
      h('div', {}, h('b', { text: i.totalCount }), h('span', { text: 'total' })));
  }

  /** Popup « Nouvel incident » puis ouverture de l'incident créé. */
  function createIncident() {
    const dialog = $('#incident-dialog');
    const select = $('select[name=type]', dialog);
    select.replaceChildren(...config.incidentTypes.map((t) => h('option', { value: t, text: t })));
    openFormDialog(dialog, async (values) => {
      try {
        const incident = await api('POST', '/api/incidents', values);
        toast(`Incident « ${incident.title} » créé.`, 'ok');
        location.hash = `#/incident/${incident.id}`;
      } catch (e) { toast(e.message, 'error'); }
    });
  }

  // ================================================================================================
  //  6. Écran « Corbeille »
  // ================================================================================================

  /** Incidents supprimés, avec le temps restant avant suppression définitive et un bouton Restaurer. */
  async function renderTrash() {
    const items = await api('GET', '/api/trash');
    setPage('trash',
      h('div', { class: 'page-head' },
        h('div', {},
          h('h1', { text: 'Corbeille' }),
          h('p', { class: 'sub', text: `Les incidents supprimés restent restaurables pendant ${config.trashRetentionHours} h, puis sont effacés définitivement.` }))),
      items.length
        ? h('div', { class: 'incident-list' }, items.map(({ incident: i, purgeAt }) =>
          h('div', { class: 'card incident-item' },
            h('div', {},
              h('h3', {}, i.title, h('span', { class: 'badge', text: i.type }),
                h('span', { class: 'badge badge-warn', text: `Suppression définitive ${remaining(purgeAt)}` })),
              h('div', { class: 'meta', text: `Supprimé le ${fmtDate(i.deletedAt)} par ${i.deletedBy} · ${plural(i.internalCount, 'interne', 'internes')}, ${plural(i.visitorCount, 'visiteur', 'visiteurs')}` })),
            h('div', { class: 'incident-actions' },
              h('button', { class: 'btn btn-ok', type: 'button', onclick: () => restoreIncident(i) }, '↺ Restaurer')))))
        : h('div', { class: 'card empty', text: 'La corbeille est vide.' }));
  }

  /** Restauration d'un incident après confirmation, puis ouverture de celui-ci. */
  async function restoreIncident(i) {
    const ok = await confirmDialog({
      title: 'Restaurer l\'incident',
      body: [h('p', { class: 'highlight', text: i.title }), 'L\'incident et ses listes seront de nouveau accessibles.'],
      okText: 'Restaurer',
    });
    if (!ok) return;
    try {
      await api('POST', `/api/trash/${i.id}/restore`);
      toast('Incident restauré.', 'ok');
      location.hash = `#/incident/${i.id}`;
    } catch (e) { toast(e.message, 'error'); renderTrash(); }
  }

  // ================================================================================================
  //  7. Écran « Incident »
  // ================================================================================================

  /**
   * Écran principal de comptage.
   * Le squelette (titre, compteurs, barre de recherche, listes) est construit UNE fois ; seules les zones de
   * données sont redessinées à chaque rafraîchissement. Le champ de recherche n'est donc jamais réinitialisé
   * pendant la saisie, même quand les listes se mettent à jour toutes les 5 secondes.
   */
  async function renderIncident(id) {
    let data = await api('GET', `/api/incidents/${id}`); // { incident, persons, removed }
    let lastSnapshot = '';  // dernière version affichée (évite de redessiner si rien n'a changé)
    let flashId = null;     // personne venant d'être ajoutée (brièvement surlignée)

    // ---- Squelette ---------------------------------------------------------------------------------
    const head = h('div', { class: 'page-head' });
    const banner = h('div', { class: 'status-banner', hidden: true });
    const totals = h('div', { class: 'totals' });
    const listsEl = h('div', { class: 'lists' });
    const removedEl = h('div');
    const printHead = h('div', { class: 'print-only' }); // en-tête visible uniquement à l'impression

    // Champ de recherche, avec attributs d'accessibilité (combobox + liste de suggestions).
    const searchInput = h('input', {
      type: 'search', id: 'search', autocomplete: 'off', spellcheck: 'false',
      placeholder: `Nom ou prénom (${config.minSearchLength} caractères min.)`,
      'aria-label': 'Rechercher une personne dans l\'annuaire',
      'aria-autocomplete': 'list', 'aria-controls': 'suggestions', role: 'combobox', 'aria-expanded': 'false',
    });
    const suggestions = h('ul', { class: 'suggestions', id: 'suggestions', role: 'listbox', hidden: true });
    const hint = h('p', { class: 'search-hint' });
    // Icône loupe (SVG constant, aucune donnée externe).
    const searchIcon = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    searchIcon.setAttribute('viewBox', '0 0 24 24');
    searchIcon.setAttribute('class', 'search-icon');
    searchIcon.setAttribute('aria-hidden', 'true');
    searchIcon.innerHTML = '<circle cx="11" cy="11" r="7" fill="none" stroke="currentColor" stroke-width="2"/><path d="M20 20l-4-4" stroke="currentColor" stroke-width="2" stroke-linecap="round"/>';

    const addBar = h('div', { class: 'add-bar' },
      h('div', { class: 'search' }, searchIcon, searchInput, suggestions, hint),
      h('button', { class: 'btn btn-lg', type: 'button', onclick: () => addVisitor() }, '+ Ajouter un visiteur'));

    setPage('incidents', printHead, head, banner, totals, addBar, listsEl, removedEl);

    // ---- Affichage des données ---------------------------------------------------------------------
    /** Redessine en-tête, compteurs et listes à partir de « data » (seulement si les données ont changé). */
    function render() {
      const snapshot = JSON.stringify(data);
      if (snapshot === lastSnapshot) return;
      lastSnapshot = snapshot;

      const i = data.incident;
      // Deux listes distinctes, chacune triée par ordre alphabétique (voir byName).
      const internals = data.persons.filter((p) => p.kind === 'internal').sort(byName);
      const visitors = data.persons.filter((p) => p.kind === 'visitor').sort(byName);
      document.title = `${i.title} — Comptage incident`;

      printHead.replaceChildren(
        h('h1', { text: `${i.type} — ${i.title}` }),
        h('p', { text: `${i.location ? `Lieu : ${i.location} · ` : ''}Liste imprimée le ${dateFmt.format(new Date())}` }));

      head.replaceChildren(
        h('div', { class: 'no-print' },
          h('h1', {}, i.title, ' ', h('span', { class: `badge ${i.isClosed ? 'badge-closed' : 'badge-open'}`, text: i.isClosed ? 'Clôturé' : 'En cours' })),
          h('p', { class: 'sub', text: [i.type, i.location ? `📍 ${i.location}` : null, `créé le ${fmtDate(i.createdAt)} par ${i.createdBy}`].filter(Boolean).join(' · ') })),
        h('div', { class: 'toolbar' },
          h('button', { class: 'btn', type: 'button', onclick: () => window.print() }, '🖨 Imprimer'),
          h('a', { class: 'btn', href: `/api/incidents/${i.id}/export.csv`, download: '' }, '⤓ Export CSV'),
          h('button', { class: 'btn', type: 'button', onclick: () => showAudit(i.id) }, 'Journal'),
          i.isClosed
            ? h('button', { class: 'btn', type: 'button', onclick: () => setClosed(false) }, 'Rouvrir')
            : h('button', { class: 'btn btn-ok', type: 'button', onclick: () => setClosed(true) }, '✓ Clôturer'),
          h('button', { class: 'btn btn-danger', type: 'button', onclick: trashIncident }, 'Supprimer l\'incident')));

      // Incident clôturé : bandeau d'information, barre d'ajout masquée, boutons Supprimer masqués.
      // (Le serveur refuse de toute façon les modifications : ceci n'est qu'un confort d'affichage.)
      banner.hidden = !i.isClosed;
      banner.textContent = i.isClosed ? `Incident clôturé le ${fmtDate(i.closedAt)} par ${i.closedBy} — listes en lecture seule.` : '';
      addBar.hidden = i.isClosed;

      totals.replaceChildren(
        h('div', { class: 'card total total-internal' }, h('div', { class: 'num', text: internals.length }), h('div', { class: 'lbl', text: 'Internes' })),
        h('div', { class: 'card total total-visitor' }, h('div', { class: 'num', text: visitors.length }), h('div', { class: 'lbl', text: 'Visiteurs' })),
        h('div', { class: 'card total total-all' }, h('div', { class: 'num', text: internals.length + visitors.length }), h('div', { class: 'lbl', text: 'Total présents' })));

      listsEl.replaceChildren(
        personList('Internes', 'list-internal', internals, i.isClosed, 'Aucune personne interne comptée.'),
        personList('Visiteurs', 'list-visitor', visitors, i.isClosed, 'Aucun visiteur compté.'));

      // Section repliable des personnes retirées, avec bouton « Réintégrer ».
      removedEl.replaceChildren();
      if (data.removed.length) {
        removedEl.append(h('details', { class: 'removed' },
          h('summary', { text: `Personnes retirées (${data.removed.length}) — réintégrables en cas d'erreur` }),
          h('div', { class: 'card' }, h('ul', { class: 'people' }, data.removed.map((p) =>
            h('li', {},
              h('div', {},
                h('div', { class: 'name', text: `${personName(p)}${p.kind === 'visitor' ? ' (visiteur)' : ''}` }),
                h('div', { class: 'detail', text: `Retiré le ${fmtDate(p.removedAt)} par ${p.removedBy}` })),
              i.isClosed ? h('span') : h('button', { class: 'btn btn-sm btn-ok', type: 'button', onclick: () => restorePerson(p) }, '↺ Réintégrer')))))));
      }
      flashId = null;
      // Si des suggestions sont ouvertes, une personne comptée entre-temps par un autre agent y est grisée.
      syncSuggestionState();
    }

    /** Carte d'une liste (Internes ou Visiteurs) : titre, total, une ligne par personne avec bouton Supprimer. */
    function personList(title, cls, people, readOnly, emptyText) {
      return h('section', { class: `card list-card ${cls}` },
        h('header', {}, h('h2', { text: title }), h('span', { class: 'count', title: `Total ${title.toLowerCase()}`, text: people.length })),
        people.length
          ? h('ul', { class: 'people' }, people.map((p) =>
            h('li', { class: p.id === flashId ? 'flash' : null },
              h('div', {},
                h('div', { class: 'name', text: personName(p) }),
                h('div', { class: 'detail', text: `ajouté à ${fmtTime(p.addedAt)} par ${p.addedBy}` })),
              readOnly ? h('span') : h('button', { class: 'btn btn-sm btn-danger', type: 'button', onclick: () => removePerson(p) }, 'Supprimer'))))
          : h('div', { class: 'empty', text: emptyText }));
    }

    /**
     * Recharge les données depuis le serveur et redessine. Si l'incident a été supprimé par un autre agent,
     * retour à l'accueil.
     * Le rafraîchissement automatique et celui qui suit une action peuvent se chevaucher : chaque appel est
     * numéroté, et une réponse plus ancienne que la dernière affichée est ignorée. L'écran ne peut donc
     * jamais revenir en arrière vers un état périmé.
     */
    let refreshSeq = 0;
    let appliedSeq = 0;
    async function refresh() {
      const seq = ++refreshSeq;
      try {
        const fresh = await api('GET', `/api/incidents/${id}`);
        if (seq < appliedSeq) return; // réponse dépassée par une plus récente
        appliedSeq = seq;
        data = fresh;
        render();
      } catch (e) {
        if (e.status === 404) { toast('Cet incident a été supprimé.', 'error'); location.hash = '#/'; }
      }
    }

    // ---- Actions -----------------------------------------------------------------------------------

    /** Ajout d'une personne interne choisie dans les suggestions : popup de confirmation, puis appel API. */
    async function addInternal(person) {
      hideSuggestions();
      results = []; // évite de réafficher des suggestions périmées quand le focus revient au champ
      const ok = await confirmDialog({
        title: 'Ajouter cette personne ?',
        body: [h('p', { class: 'highlight', text: personName(person) }),
          h('p', { class: 'muted', text: person.login }),
          'Confirmer la présence de cette personne ?'],
        okText: 'Ajouter',
      });
      if (!ok) {
        // Annulation : on relance la recherche pour réafficher les suggestions à jour.
        searchInput.focus();
        const q = normalizeName(searchInput.value);
        if (q.length >= config.minSearchLength && isValidName(q)) runSearch(q);
        return;
      }
      try {
        const added = await api('POST', `/api/incidents/${id}/persons/internal`, {
          login: person.login, firstName: person.firstName, lastName: person.lastName,
          displayName: person.displayName,
        });
        toast(`${personName(added)} ajouté(e).`, 'ok');
        flashId = added.id;
        searchInput.value = '';
        hint.textContent = '';
        await refresh();
      } catch (e) { toast(e.message, 'error'); } // ex. « déjà comptée » (409) si un autre agent l'a ajoutée entre-temps
      searchInput.focus(); // prêt pour la personne suivante
    }

    /** Ajout d'un visiteur : formulaire (nom complet), popup de confirmation, puis appel API. */
    function addVisitor() {
      // Contrôle du nom pendant la frappe : un nom invalide bloque la validation du formulaire
      // (message natif du navigateur sous le champ), la popup reste ouverte pour corriger.
      const nameInput = $('#visitor-dialog input[name=fullName]');
      nameInput.oninput = () => {
        const v = normalizeName(nameInput.value);
        nameInput.setCustomValidity(v && !isValidName(v) ? `Caractère non autorisé. ${NAME_HELP}` : '');
      };
      openFormDialog($('#visitor-dialog'), async (values) => {
        const fullName = normalizeName(values.fullName).replace(/\s+/g, ' ');
        const ok = await confirmDialog({
          title: 'Ajouter ce visiteur ?',
          body: [h('p', { class: 'highlight', text: fullName })],
          okText: 'Ajouter',
        });
        if (!ok) return;
        try {
          const added = await api('POST', `/api/incidents/${id}/persons/visitor`, { fullName });
          toast(`${added.displayName} ajouté(e) aux visiteurs.`, 'ok');
          flashId = added.id;
          await refresh();
        } catch (e) { toast(e.message, 'error'); }
      });
    }

    /** « Supprimer » une personne (retrait logique, réintégrable) après confirmation. */
    async function removePerson(p) {
      const ok = await confirmDialog({
        title: 'Retirer cette personne ?',
        body: [h('p', { class: 'highlight', text: personName(p) }),
          `Elle sera retirée de la liste des ${p.kind === 'internal' ? 'internes' : 'visiteurs'}. Vous pourrez la réintégrer depuis « Personnes retirées ».`],
        okText: 'Retirer',
      });
      if (!ok) return;
      try {
        await api('DELETE', `/api/incidents/${id}/persons/${p.id}`);
        toast(`${personName(p)} retiré(e).`);
        await refresh();
      } catch (e) { toast(e.message, 'error'); }
    }

    /** Réintègre une personne retirée (action de correction : pas de popup, l'action est elle-même réversible). */
    async function restorePerson(p) {
      try {
        const restored = await api('POST', `/api/incidents/${id}/persons/${p.id}/restore`);
        toast(`${personName(restored)} réintégré(e).`, 'ok');
        flashId = restored.id;
        await refresh();
      } catch (e) { toast(e.message, 'error'); }
    }

    /** Clôture (close = true) ou réouverture (close = false) après confirmation. */
    async function setClosed(close) {
      const ok = await confirmDialog(close
        ? { title: 'Clôturer l\'incident ?', body: 'Les listes passeront en lecture seule. L\'incident pourra être rouvert.', okText: 'Clôturer' }
        : { title: 'Rouvrir l\'incident ?', body: 'Les listes pourront de nouveau être modifiées.', okText: 'Rouvrir' });
      if (!ok) return;
      try {
        await api('POST', `/api/incidents/${id}/${close ? 'close' : 'reopen'}`);
        toast(close ? 'Incident clôturé.' : 'Incident rouvert.', 'ok');
        await refresh();
      } catch (e) { toast(e.message, 'error'); }
    }

    /** Mise en corbeille de l'incident après confirmation (rappel du nombre de personnes et du délai de restauration). */
    async function trashIncident() {
      const i = data.incident;
      const ok = await confirmDialog({
        title: 'Supprimer l\'incident ?',
        body: [h('p', { class: 'highlight', text: i.title }),
          `${plural(i.totalCount, 'personne comptée', 'personnes comptées')}.`,
          `L'incident sera placé dans la corbeille et restera restaurable pendant ${config.trashRetentionHours} h.`],
        okText: 'Mettre à la corbeille',
      });
      if (!ok) return;
      try {
        await api('DELETE', `/api/incidents/${id}`);
        toast('Incident placé dans la corbeille.');
        location.hash = '#/';
      } catch (e) { toast(e.message, 'error'); }
    }

    // ---- Recherche dans l'annuaire -----------------------------------------------------------------
    //  Fonctionnement :
    //   1. À chaque frappe, si moins de N caractères (4 par défaut) : rien n'est envoyé, un indice s'affiche.
    //   2. Sinon, on attend 250 ms sans nouvelle frappe (« debounce ») avant d'interroger le serveur, pour ne
    //      pas solliciter l'annuaire à chaque lettre.
    //   3. Une recherche en cours est annulée si une nouvelle commence (AbortController) : seuls les
    //      résultats de la dernière saisie s'affichent.
    //   4. Les personnes déjà comptées apparaissent grisées « déjà comptée » et ne sont pas cliquables.
    //   5. Clavier : ↑ ↓ pour naviguer, Entrée pour choisir, Échap pour fermer / vider.
    let debounce = null;
    let controller = null;
    let results = [];
    let active = -1; // index de la suggestion sélectionnée au clavier (-1 = aucune)

    /** Identifiants (en minuscules) des personnes internes actuellement comptées. */
    const activeLogins = () => new Set(data.persons.filter((p) => p.kind === 'internal' && p.login).map((p) => p.login.toLowerCase()));

    function hideSuggestions() {
      suggestions.hidden = true;
      searchInput.setAttribute('aria-expanded', 'false');
      active = -1;
    }

    /** Affiche un message (aucun résultat, erreur…) à la place des suggestions. */
    function showInfo(text) {
      suggestions.replaceChildren(h('li', { class: 'info', text }));
      suggestions.hidden = false;
      searchInput.setAttribute('aria-expanded', 'true');
    }

    function renderSuggestions() {
      const present = activeLogins();
      if (!results.length) { showInfo('Aucune personne trouvée. Pour une personne hors annuaire, utilisez « Ajouter un visiteur ».'); return; }
      suggestions.replaceChildren(...results.map((p, index) => {
        const already = present.has(p.login.toLowerCase());
        const li = h('li', {
          role: 'option', id: `sugg-${index}`, 'data-login': p.login, class: [already ? 'disabled' : '', index === active ? 'active' : ''].join(' ').trim() || null,
          'aria-disabled': already ? 'true' : null, 'aria-selected': index === active ? 'true' : 'false',
        },
        h('span', { class: 'who' }, h('b', { text: personName(p) }), h('small', { text: p.login })),
        already ? h('span', { class: 'badge badge-closed', text: 'déjà comptée' }) : null);
        li.addEventListener('mousedown', (e) => e.preventDefault()); // garde le focus dans le champ pendant le clic
        // L'état « déjà comptée » est vérifié AU MOMENT du clic (il a pu changer depuis l'affichage).
        li.addEventListener('click', () => { if (!activeLogins().has(p.login.toLowerCase())) addInternal(p); });
        return li;
      }));
      suggestions.hidden = false;
      searchInput.setAttribute('aria-expanded', 'true');
      if (active >= 0) searchInput.setAttribute('aria-activedescendant', `sugg-${active}`);
      else searchInput.removeAttribute('aria-activedescendant');
    }

    /**
     * Met à jour, SANS reconstruire la liste, l'état « déjà comptée » des suggestions affichées.
     * Appelée après chaque rafraîchissement. Les lignes ne sont pas recréées : un clic en cours n'est pas
     * perdu, et la sélection au clavier est conservée.
     */
    function syncSuggestionState() {
      if (suggestions.hidden) return;
      const present = activeLogins();
      suggestions.querySelectorAll('li[data-login]').forEach((li) => {
        const already = present.has(li.dataset.login.toLowerCase());
        li.classList.toggle('disabled', already);
        if (already) li.setAttribute('aria-disabled', 'true'); else li.removeAttribute('aria-disabled');
        const badge = li.querySelector('.badge');
        if (already && !badge) li.append(h('span', { class: 'badge badge-closed', text: 'déjà comptée' }));
        else if (!already && badge) badge.remove();
      });
    }

    /** Interroge /api/directory/search et affiche les suggestions (si la saisie n'a pas changé entre-temps). */
    async function runSearch(q) {
      q = normalizeName(q);
      if (controller) controller.abort();
      controller = new AbortController();
      hint.textContent = 'Recherche…';
      try {
        results = await api('GET', `/api/directory/search?q=${encodeURIComponent(q)}`, undefined, controller.signal);
        active = -1;
        hint.textContent = results.length ? `${plural(results.length, 'résultat', 'résultats')} — cliquez pour ajouter` : '';
        if (normalizeName(searchInput.value) === q) renderSuggestions();
      } catch (e) {
        if (e.name === 'AbortError') return;
        hint.textContent = '';
        showInfo(e.message); // ex. « Annuaire indisponible… »
      }
    }

    searchInput.addEventListener('input', () => {
      clearTimeout(debounce);
      const q = normalizeName(searchInput.value);
      if (q && !isValidName(q)) {
        // Caractère hors liste blanche : aucune requête n'est envoyée, l'utilisateur est prévenu.
        if (controller) controller.abort();
        results = [];
        hideSuggestions();
        hint.textContent = `Caractère non autorisé. ${NAME_HELP}`;
        return;
      }
      if (q.length < config.minSearchLength) {
        if (controller) controller.abort();
        results = [];
        hideSuggestions();
        hint.textContent = q.length ? `Encore ${config.minSearchLength - q.length} caractère(s)…` : '';
        return;
      }
      debounce = setTimeout(() => runSearch(q), 250);
    });

    searchInput.addEventListener('keydown', (e) => {
      if (suggestions.hidden || !results.length) {
        if (e.key === 'Escape') { searchInput.value = ''; hint.textContent = ''; hideSuggestions(); }
        return;
      }
      if (e.key === 'ArrowDown') { e.preventDefault(); active = (active + 1) % results.length; renderSuggestions(); }
      else if (e.key === 'ArrowUp') { e.preventDefault(); active = (active - 1 + results.length) % results.length; renderSuggestions(); }
      else if (e.key === 'Enter') {
        // Entrée choisit la suggestion sélectionnée, ou l'unique résultat s'il n'y en a qu'un.
        e.preventDefault();
        const choice = results[active >= 0 ? active : (results.length === 1 ? 0 : -1)];
        if (choice && !activeLogins().has(choice.login.toLowerCase())) addInternal(choice);
      } else if (e.key === 'Escape') { hideSuggestions(); }
    });

    searchInput.addEventListener('focus', () => { if (results.length && searchInput.value.trim().length >= config.minSearchLength) renderSuggestions(); });
    // Délai avant fermeture : laisse le temps au clic sur une suggestion d'être pris en compte.
    searchInput.addEventListener('blur', () => setTimeout(hideSuggestions, 150));

    // ---- Rafraîchissement automatique --------------------------------------------------------------
    //  Plusieurs agents peuvent compter simultanément sur le même incident (points de rassemblement
    //  différents) : toutes les 5 s, l'écran se met à jour avec les ajouts des autres.
    //  Le rafraîchissement est suspendu quand l'onglet est masqué ou qu'une popup est ouverte (saisie d'un
    //  visiteur, confirmation…), et relancé immédiatement quand l'onglet redevient visible.
    //  Il ne redessine QUE l'en-tête, les compteurs et les listes : le champ de recherche (texte, focus,
    //  curseur) et la liste de suggestions ne sont jamais recréés. Une saisie en cours n'est donc pas perturbée.
    //  Rien n'est redessiné si les données n'ont pas changé (comparaison avec lastSnapshot).
    render();
    searchInput.focus();
    const timer = setInterval(() => { if (!document.hidden && !anyDialogOpen()) refresh(); }, 5000);
    const onVisible = () => { if (!document.hidden) refresh(); };
    document.addEventListener('visibilitychange', onVisible);
    pageCleanup = () => {
      clearInterval(timer);
      clearTimeout(debounce);
      if (controller) controller.abort();
      document.removeEventListener('visibilitychange', onVisible);
      document.title = 'Comptage incident';
    };
  }

  /** Popup « Journal » : historique des actions de l'incident (date, utilisateur, action, détail). */
  async function showAudit(id) {
    const dialog = $('#audit-dialog');
    const body = $('#audit-body');
    body.replaceChildren(h('p', { class: 'muted', text: 'Chargement…' }));
    dialog.showModal();
    try {
      const entries = await api('GET', `/api/incidents/${id}/audit`);
      body.replaceChildren(h('table', { class: 'audit-table' },
        h('thead', {}, h('tr', {}, h('th', { text: 'Date' }), h('th', { text: 'Utilisateur' }), h('th', { text: 'Action' }), h('th', { text: 'Détail' }))),
        h('tbody', {}, entries.map((e) => h('tr', {},
          h('td', { text: fmtDate(e.at) }), h('td', { text: e.user }), h('td', { text: e.action }), h('td', { text: e.details || '' }))))));
    } catch (e) {
      body.replaceChildren(h('p', { text: e.message }));
    }
  }

  // ================================================================================================
  //  8. Affichage des personnes
  // ================================================================================================

  /** Nom affiché : « NOM Prénom » pour les internes, nom saisi pour les visiteurs. */
  function personName(p) {
    if (p.lastName || p.firstName) return [p.lastName ? p.lastName.toUpperCase() : null, p.firstName].filter(Boolean).join(' ');
    return p.displayName;
  }

  /**
   * Ordre alphabétique : par nom de famille (ou nom saisi pour un visiteur), puis par prénom,
   * sans tenir compte de la casse ni des accents. En cas d'égalité parfaite, ordre d'ajout.
   */
  function byName(a, b) {
    return collator.compare(a.lastName || a.displayName, b.lastName || b.displayName)
      || collator.compare(a.firstName || '', b.firstName || '')
      || a.id - b.id;
  }

  // ================================================================================================
  //  9. Démarrage
  // ================================================================================================

  /** Charge l'utilisateur connecté et la configuration, puis affiche l'écran demandé par l'adresse. */
  async function start() {
    try {
      const [me, cfg] = await Promise.all([api('GET', '/api/me'), api('GET', '/api/config')]);
      config = cfg;
      $('#current-user').textContent = me.name;
    } catch (e) {
      setPage('', h('div', { class: 'card empty' }, h('p', { text: e.message })));
      return;
    }
    window.addEventListener('hashchange', route);
    route();
  }

  start();
})();
