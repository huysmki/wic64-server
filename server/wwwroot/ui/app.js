// WiC64 Server web UI: manage the content folders, push programs, preview pictures and tunes,
// and follow what the C64 does. Talks to the JSON API in AdminApi.cs.

const $ = (selector) => document.querySelector(selector);

const sections = {
  prg: { title: 'PROGRAMS', hint: 'Programs (.prg) and disk images (.d64). Click a .d64 to open it. RUN pushes the program: the browser on the C64 picks it up within a second while its menu is on screen. SAVE+RUN saves it to drive 8 first.' },
  img: { title: 'PICTURES', hint: 'PNG, JPG, GIF, BMP, WebP and Koala (.koa) pictures. VIEW shows exactly what the C64 will display; SHOW ON C64 puts it on the C64\'s screen (any key on the C64 returns to the menu).' },
  sid: { title: 'MUSIC', hint: 'SID tunes. PLAY ON C64 starts the tune on the C64. Put the High Voltage SID Collection\'s Songlengths.md5 anywhere in this folder for correct song lengths.' },
};

const state = { section: 'prg', path: '', activitySince: 0, unseenActivity: 0 };

// ---------------------------------------------------------------------------
// API helpers

async function api(url, options = {}) {
  const response = await fetch(url, options);
  const type = response.headers.get('content-type') || '';
  const body = type.includes('json') ? await response.json() : await response.text();
  if (!response.ok) {
    const message = typeof body === 'string' ? body : body.error || body.detail || body.title || response.statusText;
    throw new Error(message || `HTTP ${response.status}`);
  }
  return body;
}

const postJson = (url, data) => api(url, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify(data),
});

const query = (params) => new URLSearchParams(params).toString();

function toast(message, isError = false) {
  const element = document.createElement('div');
  element.className = 'toast' + (isError ? ' error' : '');
  element.textContent = message;
  $('#toasts').append(element);
  setTimeout(() => element.remove(), isError ? 6000 : 3500);
}

async function attempt(action, success) {
  try {
    const result = await action();
    if (success) toast(typeof success === 'function' ? success(result) : success);
    return result;
  } catch (error) {
    toast(error.message, true);
    return undefined;
  }
}

function formatSize(bytes) {
  if (!bytes) return '';
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

function element(tag, attributes = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(attributes)) {
    if (key === 'class') node.className = value;
    else if (key.startsWith('on')) node.addEventListener(key.slice(2), value);
    else node.setAttribute(key, value);
  }
  node.append(...children.filter((child) => child !== null && child !== undefined));
  return node;
}

// ---------------------------------------------------------------------------
// Tabs and navigation

function selectTab(section) {
  document.querySelectorAll('.tabs button').forEach((button) =>
    button.setAttribute('aria-selected', String(button.dataset.section === section)));

  const isActivity = section === 'activity';
  $('#browser').classList.toggle('hidden', isActivity);
  $('#activity').classList.toggle('hidden', !isActivity);
  closePreview();

  if (isActivity) {
    state.unseenActivity = 0;
    updateActivityBadge();
    return;
  }

  if (state.section !== section) {
    state.section = section;
    state.path = '';
  }
  $('#section-hint').textContent = sections[section].hint;
  loadFiles();
}

function openPath(path) {
  state.path = path;
  closePreview();
  loadFiles();
}

function renderBreadcrumb(path) {
  const parts = path ? path.split('/') : [];
  const crumbs = [{ name: sections[state.section].title, path: '' }];
  parts.forEach((part, index) => crumbs.push({ name: part, path: parts.slice(0, index + 1).join('/') }));

  $('#breadcrumb').replaceChildren(...crumbs.map((crumb, index) =>
    element('li', {}, element('button', {
      type: 'button',
      onclick: () => { if (index < crumbs.length - 1) openPath(crumb.path); },
    }, crumb.name))));
}

// ---------------------------------------------------------------------------
// File list

const kindLabels = { folder: 'DIR', disk: 'D64', program: 'PRG', picture: 'IMG', tune: 'SID', other: 'SEQ' };

async function loadFiles() {
  const section = state.section;
  const path = state.path;
  let data;
  try {
    data = await api('/api/files?' + query({ section, path }));
  } catch (error) {
    toast(error.message, true);
    if (path) openPath('');
    return;
  }
  if (section !== state.section || path !== state.path) return; // navigated away meanwhile

  renderBreadcrumb(data.path);
  const insideDisk = data.path.toLowerCase().endsWith('.d64');
  $('#new-folder').disabled = insideDisk;
  $('#upload').disabled = insideDisk;
  $('#upload').parentElement.classList.toggle('hidden', insideDisk);
  $('#new-folder').classList.toggle('hidden', insideDisk);

  $('#files').replaceChildren(...data.items.map((item) => renderRow(item, insideDisk)));
  $('#empty').classList.toggle('hidden', data.items.length > 0);
}

function renderRow(item, insideDisk) {
  const opens = item.kind === 'folder' || item.kind === 'disk';
  const name = opens
    ? element('button', { type: 'button', onclick: () => openPath(item.path) }, item.name + '/')
    : item.name;

  const actions = element('div', { class: 'row-actions' });
  if (item.kind === 'program') {
    actions.append(
      element('button', { type: 'button', class: 'key small primary', title: 'Run on the C64', onclick: () => push(item, false) }, 'RUN'),
      element('button', { type: 'button', class: 'key small', title: 'Save to disk (drive 8) on the C64, then run it', onclick: () => push(item, true) }, 'SAVE+RUN'));
  }
  if (item.kind === 'picture' || item.kind === 'tune') {
    actions.append(
      element('button', {
        type: 'button', class: 'key small primary',
        title: item.kind === 'picture' ? 'Show this picture on the C64' : 'Play this tune on the C64',
        onclick: () => pushMedia(item),
      }, item.kind === 'picture' ? 'SHOW ON C64' : 'PLAY ON C64'),
      element('button', { type: 'button', class: 'key small', onclick: () => preview(item) }, 'VIEW'));
  }
  if (!insideDisk) {
    if (item.kind !== 'folder') {
      actions.append(element('button', {
        type: 'button', class: 'key small', title: 'Download to this computer',
        onclick: () => { location.href = '/api/download?' + query({ section: state.section, path: item.path }); },
      }, 'GET'));
    }
    actions.append(
      element('button', { type: 'button', class: 'key small', onclick: () => rename(item) }, 'RENAME'),
      element('button', { type: 'button', class: 'key small', onclick: () => remove(item) }, 'DELETE'));
  }

  return element('tr', {},
    element('td', {}, element('span', { class: `kind ${item.kind}` }, kindLabels[item.kind] || '---')),
    element('td', { class: 'name' }, name),
    element('td', { class: 'details' }, item.details),
    element('td', { class: 'size' }, item.kind === 'folder' ? '' : formatSize(item.size)),
    element('td', {}, actions));
}

// ---------------------------------------------------------------------------
// Actions

async function push(item, save) {
  await attempt(
    () => postJson('/api/push', { path: item.path, index: item.index ?? null, save }),
    (result) => `${result.name} is waiting for the C64${save ? ' (save to disk, then run)' : ''}`);
  refreshStatus();
}

// Show a picture or play a tune on the C64: the browser picks it up like a pushed program
async function pushMedia(item) {
  const what = item.kind === 'picture' ? 'shown' : 'played';
  await attempt(
    () => postJson('/api/push', { section: state.section, path: item.path, save: false }),
    (result) => `${result.name} will be ${what} on the C64 within a second`);
  refreshStatus();
}

async function uploadFiles(files) {
  if (!files.length) return;
  const form = new FormData();
  for (const file of files) form.append('files', file, file.name);
  await attempt(
    () => api('/api/upload?' + query({ section: state.section, path: state.path }), { method: 'POST', body: form }),
    (result) => `Uploaded ${result.saved.length} file${result.saved.length === 1 ? '' : 's'}`);
  loadFiles();
}

function askName(title, value = '') {
  const dialog = $('#name-dialog');
  $('#name-dialog-title').textContent = title;
  const input = $('#name-dialog-input');
  input.value = value;
  dialog.returnValue = '';
  dialog.showModal();
  const dot = value.lastIndexOf('.');
  input.setSelectionRange(0, dot > 0 ? dot : value.length);
  return new Promise((resolve) => {
    dialog.addEventListener('close', () => resolve(dialog.returnValue === 'ok' ? input.value.trim() : null), { once: true });
  });
}

function confirmDelete(title, text) {
  const dialog = $('#confirm-dialog');
  $('#confirm-dialog-title').textContent = title;
  $('#confirm-dialog-text').textContent = text;
  dialog.returnValue = '';
  dialog.showModal();
  return new Promise((resolve) => {
    dialog.addEventListener('close', () => resolve(dialog.returnValue === 'ok'), { once: true });
  });
}

async function newFolder() {
  const name = await askName('New folder');
  if (!name) return;
  await attempt(() => postJson('/api/folder', { section: state.section, path: state.path, name }), `Created ${name}`);
  loadFiles();
}

async function rename(item) {
  const name = await askName(`Rename ${item.name}`, item.name);
  if (!name || name === item.name) return;
  await attempt(() => postJson('/api/rename', { section: state.section, path: item.path, name }), `Renamed to ${name}`);
  loadFiles();
}

async function remove(item) {
  const what = item.kind === 'folder' ? 'the folder and everything in it' : 'the file';
  if (!await confirmDelete(`Delete ${item.name}?`, `This permanently deletes ${what} from this computer.`)) return;
  await attempt(() => api('/api/files?' + query({ section: state.section, path: item.path }), { method: 'DELETE' }), `Deleted ${item.name}`);
  loadFiles();
}

// ---------------------------------------------------------------------------
// Preview panel

function closePreview() {
  $('#preview').classList.add('hidden');
  $('#preview-body').replaceChildren();
}

async function preview(item) {
  $('#preview-title').textContent = item.name.toUpperCase();
  $('#preview').classList.remove('hidden');
  const body = $('#preview-body');
  body.replaceChildren(element('p', { class: 'note' }, 'LOADING...'));

  if (item.kind === 'picture') {
    const stamp = Date.now();
    const figures = [];
    const isKoala = /\.(koa|kla)$/i.test(item.name);
    if (!isKoala) {
      figures.push(element('figure', {},
        element('img', { src: '/api/original?' + query({ path: item.path, t: stamp }), alt: 'Original picture' }),
        element('figcaption', {}, 'Original')));
    }
    const c64 = element('img', { class: 'c64', src: '/api/picture?' + query({ path: item.path, t: stamp }), alt: 'The picture as the C64 shows it' });
    c64.addEventListener('error', () => toast('Could not convert this picture', true));
    figures.push(element('figure', {}, c64,
      element('figcaption', {}, 'On the C64: 160 × 200 multicolor, 16 colors, max. 4 per 4 × 8 block')));
    body.replaceChildren(
      element('div', { class: 'preview-actions' },
        element('button', { type: 'button', class: 'key primary', onclick: () => pushMedia(item) }, 'SHOW ON C64')),
      element('div', { class: 'pictures' }, ...figures));
    return;
  }

  if (item.kind === 'tune') {
    const info = await attempt(() => api('/api/sid?' + query({ path: item.path })));
    if (!info) { closePreview(); return; }

    const modeNote = info.problem
      ? element('div', { class: 'note error' }, `Can not be played: ${info.problem}`)
      : info.mode === 'standalone'
        ? element('div', { class: 'note warn' }, 'Plays standalone: the tune needs the browser\'s memory or runs its own interrupt. Reset the C64 to return to the browser.')
        : element('div', { class: 'note' }, 'Plays in the background: you can keep browsing and look at pictures while it plays.');

    body.replaceChildren(
      info.problem ? '' : element('div', { class: 'preview-actions' },
        element('button', { type: 'button', class: 'key primary', onclick: () => pushMedia(item) }, 'PLAY ON C64')),
      element('dl', { class: 'facts' },
        element('dt', {}, 'Title'), element('dd', {}, info.name),
        element('dt', {}, 'Author'), element('dd', {}, info.author),
        element('dt', {}, 'Released'), element('dd', {}, info.released),
        element('dt', {}, 'Type'), element('dd', {}, `${info.type}, ${info.timing}`),
        element('dt', {}, 'Songs'), element('dd', {}, `${info.songs}, starts with ${info.startSong}`),
        element('dt', {}, 'Loads at'), element('dd', { class: 'mono' }, info.load),
        element('dt', {}, 'Init / play'), element('dd', { class: 'mono' }, `${info.init} / ${info.play}`),
        element('dt', {}, 'Really uses'), element('dd', { class: 'mono' }, info.uses || '–')),
      modeNote,
      element('h3', {}, info.knownLengths ? 'Song lengths' : 'Song lengths (not in Songlengths.md5, default length)'),
      element('div', { class: 'chips' }, ...info.lengths.map((length, index) =>
        element('span', { class: 'chip' }, `${index + 1}  ${length}`))));
  }
}

// ---------------------------------------------------------------------------
// Status and activity (polled)

function ago(date) {
  const seconds = Math.max(0, Math.round((Date.now() - date.getTime()) / 1000));
  if (seconds < 60) return `${seconds} s ago`;
  if (seconds < 3600) return `${Math.round(seconds / 60)} min ago`;
  return date.toLocaleTimeString();
}

async function refreshStatus() {
  let status;
  try {
    status = await api('/api/status');
  } catch {
    $('#c64-status .text').textContent = '?SERVER NOT REACHABLE';
    $('#c64-status').classList.remove('online');
    return;
  }

  $('#content-folder').textContent = status.contentFolder;

  const seen = status.c64LastSeen ? new Date(status.c64LastSeen) : null;
  const online = seen && Date.now() - seen.getTime() < 5000;
  $('#c64-status').classList.toggle('online', Boolean(online));
  $('#c64-status .text').textContent = (seen
    ? `C64 ${online ? 'online' : 'last seen ' + ago(seen)} · ${status.c64Address}`
    : 'C64 not seen yet').toUpperCase();

  const addresses = status.serverAddresses;
  $('#address-status').replaceChildren(
    'C64 USES ',
    element('code', {}, addresses.length ? addresses.join(' OR ') : 'NO NETWORK'));

  const pushStatus = $('#push-status');
  pushStatus.classList.toggle('hidden', !status.pushed);
  if (status.pushed) {
    const action = { program: status.pushed.save ? 'SAVE + RUN' : 'RUN', picture: 'SHOW', tune: 'PLAY' }[status.pushed.kind];
    pushStatus.querySelector('.text').textContent =
      `WAITING FOR THE C64: ${action} ${status.pushed.name.toUpperCase()}`;
  }
}

async function refreshActivity() {
  let events;
  try {
    events = await api('/api/activity?' + query({ since: state.activitySince }));
  } catch {
    return;
  }
  if (!events.length) return;

  const list = $('#activity-list');
  for (const event of events) {
    state.activitySince = event.id;
    list.prepend(element('li', {},
      element('time', { datetime: event.time }, new Date(event.time).toLocaleTimeString()),
      element('span', { class: `kind ${event.kind}` }, event.kind),
      element('span', {}, event.text)));
  }
  while (list.children.length > 200) list.lastElementChild.remove();
  $('#activity-empty').classList.add('hidden');

  if ($('#activity').classList.contains('hidden')) {
    state.unseenActivity += events.length;
    updateActivityBadge();
  }
}

function updateActivityBadge() {
  const badge = $('#activity-badge');
  badge.textContent = state.unseenActivity > 99 ? '99+' : String(state.unseenActivity);
  badge.classList.toggle('hidden', state.unseenActivity === 0);
}

// ---------------------------------------------------------------------------
// Wiring

document.querySelectorAll('.tabs button').forEach((button) =>
  button.addEventListener('click', () => selectTab(button.dataset.section)));

$('#new-folder').addEventListener('click', newFolder);
$('#upload').addEventListener('change', (event) => { uploadFiles(event.target.files); event.target.value = ''; });
$('#preview-close').addEventListener('click', closePreview);
document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape') closePreview();

  // F1 / F3 / F5 / F7 switch sections, like in the browser on the C64
  const section = { F1: 'prg', F3: 'img', F5: 'sid', F7: 'activity' }[event.key];
  if (section && !document.querySelector('dialog[open]') && !event.metaKey && !event.ctrlKey && !event.altKey) {
    event.preventDefault();
    selectTab(section);
  }
});
$('#cancel-push').addEventListener('click', async () => {
  await attempt(() => api('/api/push', { method: 'DELETE' }), 'Push cancelled');
  refreshStatus();
});

const dropzone = $('#dropzone');
let dragDepth = 0;
dropzone.addEventListener('dragenter', (event) => {
  if (!event.dataTransfer.types.includes('Files') || state.path.toLowerCase().endsWith('.d64')) return;
  event.preventDefault();
  dragDepth++;
  dropzone.classList.add('dragging');
});
dropzone.addEventListener('dragover', (event) => {
  if (event.dataTransfer.types.includes('Files')) event.preventDefault();
});
dropzone.addEventListener('dragleave', () => {
  dragDepth = Math.max(0, dragDepth - 1);
  if (!dragDepth) dropzone.classList.remove('dragging');
});
dropzone.addEventListener('drop', (event) => {
  event.preventDefault();
  dragDepth = 0;
  dropzone.classList.remove('dragging');
  if (!state.path.toLowerCase().endsWith('.d64')) uploadFiles(event.dataTransfer.files);
});

selectTab('prg');
refreshStatus();
refreshActivity();
setInterval(refreshStatus, 2000);
setInterval(refreshActivity, 2000);
