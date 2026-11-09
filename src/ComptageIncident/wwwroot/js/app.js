'use strict';

(() => {
  // ================================================================ Utilitaires

  const $ = (sel, root = document) => root.querySelector(sel);

  /** Crée un élément DOM. Le texte est toujours inséré via textContent (aucune injection HTML). */
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

  const dateFmt = new Intl.DateTimeFormat('fr-FR', { dateStyle: 'short', timeStyle: 'short' });
  const timeFmt = new Intl.DateTimeFormat('fr-FR', { hour: '2-digit', minute: '2-digit' });
  const fmtDate = (iso) => (iso ? dateFmt.format(new Date(iso)) : '');
  const fmtTime = (iso) => (iso ? timeFmt.format(new Date(iso)) : '');
  const collator = new Intl.Collator('fr', { sensitivity: 'base', numeric: true });

  function plural(n, one, many) { return `${n} ${n > 1 ? many : one}`; }

  function remaining(iso) {
    const ms = new Date(iso) - Date.now();
    if (ms <= 0) return 'imminente';
    const totalMin = Math.round(ms / 60000);
    const hours = Math.floor(totalMin / 60);
    const minutes = totalMin % 60;
    return hours > 0 ? `dans ${hours} h ${String(minutes).padStart(2, '0')}` : `dans ${minutes} min`;
  }

  function toast(message, kind = '') {
    const el = h('div', { class: `toast ${kind}`, role: kind === 'error' ? 'alert' : 'status', text: message });
    $('#toasts').append(el);
    setTimeout(() => el.remove(), kind === 'error' ? 6000 : 3500);
  }

  // ================================================================ API

  class ApiError extends Error {
    constructor(message, status) { super(message); this.status = status; }
  }

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
      if (e.name === 'AbortError') throw e;
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

  // ================================================================ Dialogues

  /** Popup de confirmation : renvoie une promesse résolue à true si l'utilisateur valide. */
  function confirmDialog({ title, body, okText = 'Valider' }) {
    const dialog = $('#confirm-dialog');
    $('#confirm-title').textContent = title;
    const msg = $('#confirm-message');
    msg.replaceChildren(...(Array.isArray(body) ? body : [body]).map((b) => (b instanceof Node ? b : h('p', { text: b }))));
    const ok = $('#confirm-ok');
    ok.textContent = okText;
    dialog.returnValue = '';
    dialog.showModal();
    // Le focus par défaut va sur « Annuler » pour éviter une validation accidentelle avec Entrée.
    $('#confirm-cancel').focus();
    return new Promise((resolve) => {
      dialog.addEventListener('close', () => resolve(dialog.returnValue === 'ok'), { once: true });
    });
  }

  function openFormDialog(dialog, onSubmit) {
    const form = $('form', dialog);
    form.reset();
    const submit = async (event) => {
      event.preventDefault();
      if (!form.reportValidity()) return;
      const values = Object.fromEntries(new FormData(form).entries());
      dialog.close();
      await onSubmit(values);
    };
    form.onsubmit = submit;
    dialog.querySelectorAll('[data-close]').forEach((b) => { b.onclick = () => dialog.close(); });
    dialog.showModal();
    const first = form.querySelector('input, select');
    if (first) first.focus();
  }

  const anyDialogOpen = () => !!document.querySelector('dialog[open]');

  // ================================================================ Routage

  const app = $('#app');
  let config = { minSearchLength: 4, trashRetentionHours: 24, incidentTypes: [] };
  let pageCleanup = null;

  function setPage(nav, ...content) {
    if (pageCleanup) { pageCleanup(); pageCleanup = null; }
    document.querySelectorAll('[data-nav]').forEach((a) => a.classList.toggle('active', a.dataset.nav === nav));
    app.replaceChildren(...content);
  }

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

  // ================================================================ Liste des incidents

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

  function counts(i) {
    return h('div', { class: 'incident-counts' },
      h('div', {}, h('b', { text: i.internalCount }), h('span', { text: 'internes' })),
      h('div', {}, h('b', { text: i.visitorCount }), h('span', { text: 'visiteurs' })),
      h('div', {}, h('b', { text: i.totalCount }), h('span', { text: 'total' })));
  }

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

  // ================================================================ Corbeille

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

  // ================================================================ Page incident

  async function renderIncident(id) {
    let data = await api('GET', `/api/incidents/${id}`);
    let lastSnapshot = '';
    let flashId = null;

    // ---- Squelette (construit une seule fois ; la recherche n'est jamais reconstruite)
    const head = h('div', { class: 'page-head' });
    const banner = h('div', { class: 'status-banner', hidden: true });
    const totals = h('div', { class: 'totals' });
    const listsEl = h('div', { class: 'lists' });
    const removedEl = h('div');
    const printHead = h('div', { class: 'print-only' });

    const searchInput = h('input', {
      type: 'search', id: 'search', autocomplete: 'off', spellcheck: 'false',
      placeholder: `Nom ou prénom (${config.minSearchLength} caractères min.)`,
      'aria-label': 'Rechercher une personne dans l\'annuaire',
      'aria-autocomplete': 'list', 'aria-controls': 'suggestions', role: 'combobox', 'aria-expanded': 'false',
    });
    const suggestions = h('ul', { class: 'suggestions', id: 'suggestions', role: 'listbox', hidden: true });
    const hint = h('p', { class: 'search-hint' });
    const searchIcon = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    searchIcon.setAttribute('viewBox', '0 0 24 24');
    searchIcon.setAttribute('class', 'search-icon');
    searchIcon.setAttribute('aria-hidden', 'true');
    searchIcon.innerHTML = '<circle cx="11" cy="11" r="7" fill="none" stroke="currentColor" stroke-width="2"/><path d="M20 20l-4-4" stroke="currentColor" stroke-width="2" stroke-linecap="round"/>';

    const addBar = h('div', { class: 'add-bar' },
      h('div', { class: 'search' }, searchIcon, searchInput, suggestions, hint),
      h('button', { class: 'btn btn-lg', type: 'button', onclick: () => addVisitor() }, '+ Ajouter un visiteur'));

    setPage('incidents', printHead, head, banner, totals, addBar, listsEl, removedEl);

    // ---- Rendu des données
    function render() {
      const snapshot = JSON.stringify(data);
      if (snapshot === lastSnapshot) return;
      lastSnapshot = snapshot;

      const i = data.incident;
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
    }

    function personList(title, cls, people, readOnly, emptyText) {
      return h('section', { class: `card list-card ${cls}` },
        h('header', {}, h('h2', { text: title }), h('span', { class: 'count', title: `Total ${title.toLowerCase()}`, text: people.length })),
        people.length
          ? h('ul', { class: 'people' }, people.map((p) =>
            h('li', { class: p.id === flashId ? 'flash' : null },
              h('div', {},
                h('div', { class: 'name', text: personName(p) }),
                h('div', { class: 'detail', text: [
                  p.kind === 'internal' ? p.department : p.company,
                  `ajouté à ${fmtTime(p.addedAt)} par ${p.addedBy}`,
                ].filter(Boolean).join(' · ') })),
              readOnly ? h('span') : h('button', { class: 'btn btn-sm btn-danger', type: 'button', onclick: () => removePerson(p) }, 'Supprimer'))))
          : h('div', { class: 'empty', text: emptyText }));
    }

    async function refresh() {
      try {
        data = await api('GET', `/api/incidents/${id}`);
        render();
      } catch (e) {
        if (e.status === 404) { toast('Cet incident a été supprimé.', 'error'); location.hash = '#/'; }
      }
    }

    // ---- Actions
    async function addInternal(person) {
      hideSuggestions();
      results = []; // évite de réafficher des suggestions périmées au retour du focus
      const ok = await confirmDialog({
        title: 'Ajouter cette personne ?',
        body: [h('p', { class: 'highlight', text: personName(person) }),
          person.department ? h('p', { class: 'muted', text: person.department }) : null,
          'Confirmer la présence de cette personne ?'].filter(Boolean),
        okText: 'Ajouter',
      });
      if (!ok) {
        searchInput.focus();
        const q = searchInput.value.trim();
        if (q.length >= config.minSearchLength) runSearch(q);
        return;
      }
      try {
        const added = await api('POST', `/api/incidents/${id}/persons/internal`, {
          login: person.login, firstName: person.firstName, lastName: person.lastName,
          displayName: person.displayName, department: person.department,
        });
        toast(`${personName(added)} ajouté(e).`, 'ok');
        flashId = added.id;
        searchInput.value = '';
        hint.textContent = '';
        await refresh();
      } catch (e) { toast(e.message, 'error'); }
      searchInput.focus();
    }

    function addVisitor() {
      openFormDialog($('#visitor-dialog'), async (values) => {
        const fullName = values.fullName.trim().replace(/\s+/g, ' ');
        const company = (values.company || '').trim();
        const ok = await confirmDialog({
          title: 'Ajouter ce visiteur ?',
          body: [h('p', { class: 'highlight', text: fullName }), company ? h('p', { class: 'muted', text: company }) : null].filter(Boolean),
          okText: 'Ajouter',
        });
        if (!ok) return;
        try {
          const added = await api('POST', `/api/incidents/${id}/persons/visitor`, { fullName, company });
          toast(`${added.displayName} ajouté(e) aux visiteurs.`, 'ok');
          flashId = added.id;
          await refresh();
        } catch (e) { toast(e.message, 'error'); }
      });
    }

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

    async function restorePerson(p) {
      try {
        const restored = await api('POST', `/api/incidents/${id}/persons/${p.id}/restore`);
        toast(`${personName(restored)} réintégré(e).`, 'ok');
        flashId = restored.id;
        await refresh();
      } catch (e) { toast(e.message, 'error'); }
    }

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

    // ---- Recherche annuaire
    let debounce = null;
    let controller = null;
    let results = [];
    let active = -1;

    const activeLogins = () => new Set(data.persons.filter((p) => p.kind === 'internal' && p.login).map((p) => p.login.toLowerCase()));

    function hideSuggestions() {
      suggestions.hidden = true;
      searchInput.setAttribute('aria-expanded', 'false');
      active = -1;
    }

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
          role: 'option', id: `sugg-${index}`, class: [already ? 'disabled' : '', index === active ? 'active' : ''].join(' ').trim() || null,
          'aria-disabled': already ? 'true' : null, 'aria-selected': index === active ? 'true' : 'false',
        },
        h('span', { class: 'who' }, h('b', { text: personName(p) }), h('small', { text: [p.department, p.login].filter(Boolean).join(' · ') })),
        already ? h('span', { class: 'badge badge-closed', text: 'déjà comptée' }) : null);
        li.addEventListener('mousedown', (e) => e.preventDefault()); // garde le focus sur le champ
        li.addEventListener('click', () => { if (!already) addInternal(p); });
        return li;
      }));
      suggestions.hidden = false;
      searchInput.setAttribute('aria-expanded', 'true');
      if (active >= 0) searchInput.setAttribute('aria-activedescendant', `sugg-${active}`);
      else searchInput.removeAttribute('aria-activedescendant');
    }

    async function runSearch(q) {
      if (controller) controller.abort();
      controller = new AbortController();
      hint.textContent = 'Recherche…';
      try {
        results = await api('GET', `/api/directory/search?q=${encodeURIComponent(q)}`, undefined, controller.signal);
        active = -1;
        hint.textContent = results.length ? `${plural(results.length, 'résultat', 'résultats')} — cliquez pour ajouter` : '';
        if (searchInput.value.trim() === q) renderSuggestions();
      } catch (e) {
        if (e.name === 'AbortError') return;
        hint.textContent = '';
        showInfo(e.message);
      }
    }

    searchInput.addEventListener('input', () => {
      clearTimeout(debounce);
      const q = searchInput.value.trim();
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
        e.preventDefault();
        const choice = results[active >= 0 ? active : (results.length === 1 ? 0 : -1)];
        if (choice && !activeLogins().has(choice.login.toLowerCase())) addInternal(choice);
      } else if (e.key === 'Escape') { hideSuggestions(); }
    });

    searchInput.addEventListener('focus', () => { if (results.length && searchInput.value.trim().length >= config.minSearchLength) renderSuggestions(); });
    searchInput.addEventListener('blur', () => setTimeout(hideSuggestions, 150));

    // ---- Rafraîchissement automatique (plusieurs agents peuvent compter en parallèle)
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

  // ================================================================ Personnes

  /** Affichage « NOM Prénom » pour les internes, nom saisi pour les visiteurs. */
  function personName(p) {
    if (p.lastName || p.firstName) return [p.lastName ? p.lastName.toUpperCase() : null, p.firstName].filter(Boolean).join(' ');
    return p.displayName;
  }

  function byName(a, b) {
    return collator.compare(a.lastName || a.displayName, b.lastName || b.displayName)
      || collator.compare(a.firstName || '', b.firstName || '')
      || a.id - b.id;
  }

  // ================================================================ Démarrage

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
