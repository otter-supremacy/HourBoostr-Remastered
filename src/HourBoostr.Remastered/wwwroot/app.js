"use strict";
const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
const escapeHtml = (value) =>
  String(value ?? "").replace(
    /[&<>"']/g,
    (char) =>
      ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[
        char
      ],
  );
const hours = (seconds) =>
  (Math.max(0, Number(seconds) || 0) / 3600).toFixed(1);
const timeAgo = (value) =>
  !value
    ? "Not yet"
    : (() => {
        const n = Math.max(0, (Date.now() - new Date(value)) / 1000);
        return n < 60
          ? "Just now"
          : n < 3600
            ? `${Math.floor(n / 60)} min ago`
            : `${Math.floor(n / 3600)} hr ago`;
      })();
const stateName = (state) =>
  ({
    signed_out: "Signed out",
    connecting: "Connecting",
    authenticating: "Signing in",
    waiting_guard: "Steam Guard",
    waiting_qr: "Scan QR code",
    waiting_approval: "Mobile approval",
    online: "Online",
    idling: "Idling",
    idle: "Ready",
    paused: "Paused",
    scheduled: "Scheduled pause",
    playing_elsewhere: "Playing elsewhere",
    reconnecting: "Reconnecting",
    auth_required: "Sign-in needed",
  })[state] || state;
const isGood = (account) => account.loggedIn && account.connected;
const ui = {
  data: null,
  selected: localStorage.getItem("hbr-account"),
  tab: "overview",
  config: null,
  library: [],
  libraryTotal: 0,
  offset: 0,
  search: "",
  stats: null,
  zones: [],
  authMethod: "password",
  authId: null,
  authSignature: "",
  busy: false,
  dirty: false,
  renderGeneration: 0,
  paired: false,
};
let toastTimer, searchTimer, dialogSubmit;

async function api(path, method = "GET", body) {
  const options = {
    method,
    credentials: "same-origin",
    headers: { "X-HBR-Request": "1" },
  };
  if (body !== undefined) {
    options.headers["Content-Type"] = "application/json";
    options.body = JSON.stringify(body);
  }
  const response = await fetch(path, options);
  if (response.status === 401 && path !== "/api/session") {
    showPairing();
    throw new Error("Connect this browser to continue.");
  }
  if (!response.ok) {
    const data = await response.json().catch(() => ({}));
    throw new Error(
      data.error ||
        (response.status === 429
          ? "Too many attempts. Wait a minute before trying again."
          : `Request failed (${response.status}).`),
    );
  }
  return response.status === 204 ||
    response.headers.get("content-length") === "0"
    ? null
    : response.json().catch(() => null);
}
function toast(message) {
  clearTimeout(toastTimer);
  $("#toast").textContent = message;
  $("#toast").hidden = false;
  toastTimer = setTimeout(() => ($("#toast").hidden = true), 6000);
}
function selected() {
  return ui.data?.accounts.find((account) => account.id === ui.selected);
}
function showPairing() {
  ui.paired = false;
  $("#shell").hidden = true;
  $("#pairing").hidden = false;
}
function showShell() {
  ui.paired = true;
  $("#pairing").hidden = true;
  $("#shell").hidden = false;
}
function statusPill(account) {
  return `<span class="status-pill ${isGood(account) ? "good" : ""}"><span class="dot ${isGood(account) ? "good" : ""}"></span>${escapeHtml(stateName(account.state))}</span>`;
}
function heading(title, description, actions = "", eyebrow = "STEAM SESSION CONTROL") {
  return `<div class="page-heading"><div>${eyebrow ? `<p class="eyebrow">${eyebrow}</p>` : ""}<h1>${title}</h1><p>${description}</p></div>${actions}</div>`;
}
function button(label, action, primary = false, extra = "") {
  return `<button class="button ${primary ? "primary" : ""}" data-action="${action}" ${extra}>${label}</button>`;
}
function field(label, control, hint = "") {
  return `<div class="field">${label}${control}${hint ? `<p class="hint">${hint}</p>` : ""}</div>`;
}
function option(value, label, current) {
  return `<option value="${escapeHtml(value)}" ${String(value) === String(current) ? "selected" : ""}>${escapeHtml(label)}</option>`;
}
function noAccount() {
  return `<div class="empty-state"><span class="empty-graphic" aria-hidden="true">+</span><h2>A place for your Steam sessions.</h2><p>Add an account, sign in securely, and choose the games you want to idle. Your passwords are never saved.</p>${button("Add your first account", "add", true)}</div>`;
}
function dialog(title, html, onSubmit) {
  $("#dialog-title").textContent = title;
  $("#dialog-content").innerHTML = html;
  dialogSubmit = onSubmit;
  if (!$("#dialog").open) $("#dialog").showModal();
}
function closeDialog() {
  $("#dialog").close();
  dialogSubmit = null;
  ui.authId = null;
  ui.authSignature = "";
}
$("#dialog").addEventListener("cancel", () => {
  dialogSubmit = null;
  ui.authId = null;
});
$("#dialog").addEventListener("submit", async (event) => {
  event.preventDefault();
  const submit = event.submitter;
  if (submit) submit.disabled = true;
  try {
    await dialogSubmit?.(new FormData(event.target), event.target);
  } catch (error) {
    const target = $("#dialog-error");
    if (target && !target.closest("[hidden]"))
      target.textContent = error.message;
    else toast(error.message);
  } finally {
    if (submit) submit.disabled = false;
  }
});

async function poll() {
  if (document.hidden && !ui.authId) return;
  try {
    const previous = selected();
    ui.data = await api("/api/state");
    showShell();
    if (!ui.data.accounts.some((account) => account.id === ui.selected))
      ui.selected = ui.data.accounts[0]?.id ?? null;
    $("#version").textContent = ui.data.version;
    $("#connection-label").textContent = ui.data.remote
      ? "HTTPS connection"
      : "Local connection";
    $("#updated").textContent = "Updated just now";
    renderAccounts();
    if (!$("#main").children.length || previous?.id !== selected()?.id)
      await render();
    updateLive();
    updateAuth();
  } catch (error) {
    if (ui.paired) $("#updated").textContent = "Connection interrupted";
  }
}
function renderAccounts() {
  $("#account-count").textContent =
    `${ui.data.accounts.length} account${ui.data.accounts.length === 1 ? "" : "s"}`;
  const signature = JSON.stringify(
    ui.data.accounts.map((account) => [
      account.id,
      account.label,
      account.state,
      account.id === ui.selected,
    ]),
  );
  if ($("#accounts").dataset.signature === signature) return;
  const focused = document.activeElement?.dataset?.account;
  $("#accounts").innerHTML = ui.data.accounts
    .map(
      (account) =>
        `<button class="account-button ${account.id === ui.selected ? "selected" : ""}" data-account="${account.id}" aria-pressed="${account.id === ui.selected}"><span class="avatar">${escapeHtml(account.label.slice(0, 2).toUpperCase())}</span><span class="account-copy"><span class="account-name">${escapeHtml(account.label)}</span><span class="account-state">${escapeHtml(stateName(account.state))}</span></span><span class="dot ${isGood(account) ? "good" : account.state === "auth_required" ? "warn" : ""}"></span></button>`,
    )
    .join("");
  $("#accounts").dataset.signature = signature;
  if (focused) $(`[data-account="${focused}"]`)?.focus();
}
async function render() {
  const generation = ++ui.renderGeneration;
  const accountId = ui.selected;
  $$("[data-tab]").forEach((item) => {
    item.classList.toggle("active", item.dataset.tab === ui.tab);
    if (item.dataset.tab === ui.tab) item.setAttribute("aria-current", "page");
    else item.removeAttribute("aria-current");
  });
  $("#breadcrumb").textContent =
    { afk: "AFK REPLIES", library: "GAME LIBRARY" }[ui.tab] ||
    ui.tab.toUpperCase();
  if (!ui.selected && !["diagnostics", "settings"].includes(ui.tab)) {
    $("#main").innerHTML = noAccount();
    return;
  }
  if (accountId) {
    const config = await api(`/api/accounts/${accountId}/settings`);
    if (generation !== ui.renderGeneration || accountId !== ui.selected) return;
    ui.config = config;
  }
  if (ui.tab === "overview") renderOverview();
  if (ui.tab === "library") {
    await loadLibrary();
    if (generation !== ui.renderGeneration) return;
    renderLibrary();
  }
  if (ui.tab === "schedule") {
    await ensureZones();
    if (generation !== ui.renderGeneration) return;
    renderSchedule();
  }
  if (ui.tab === "afk") {
    await ensureZones();
    if (generation !== ui.renderGeneration) return;
    renderAfk();
  }
  if (ui.tab === "statistics") {
    const stats = await api(`/api/accounts/${accountId}/statistics`);
    if (generation !== ui.renderGeneration) return;
    ui.stats = stats;
    renderStatistics();
  }
  if (ui.tab === "diagnostics") {
    $("#main").innerHTML =
      heading(
        "A clearer view of problems.",
        "Inspect this installation without exposing your Steam credentials.",
      ) +
      `<section class="panel"><div class="panel-title"><div><h2>Diagnostic report</h2><p>Login names, Steam IDs, session tokens, QR challenges, and chat content are excluded.</p></div>${button("Download report", "download-diagnostics")}</div><pre class="report" id="report">Loading report…</pre></section>`;
    $("#report").textContent = JSON.stringify(
      await api("/api/diagnostics"),
      null,
      2,
    );
  }
  if (ui.tab === "settings") { renderSettings(); window.chrome?.webview?.postMessage({action: "desktop-state"}); }
  updateLive();
}
function renderOverview() {
  const account = selected();
  $("#main").innerHTML =
    heading(
      "Your sessions, in view.",
      "Stay informed. Choose what runs. Keep control of your account.",
      `<div class="button-row">${button("Add account", "add")}${button("Sign in", "login", true)}</div>`,
    ) +
    `<div class="stats-grid"><div class="stat-card"><p class="label">CONNECTED ACCOUNTS</p><div class="stat-value" data-live="connected">0</div><p class="stat-footnote">Steam connection confirmed</p></div><div class="stat-card"><p class="label">GAMES ACTIVE</p><div class="stat-value" data-live="games">0</div><p class="stat-footnote">Across your accounts</p></div><div class="stat-card"><p class="label">THIS SESSION</p><div class="stat-value"><span data-live="session">0.0</span><span class="muted small-text"> hr</span></div><p class="stat-footnote">Selected account, estimated</p></div><div class="stat-card"><p class="label">OWNED GAMES</p><div class="stat-value" data-live="library">0</div><p class="stat-footnote">From your Steam library</p></div></div>
    <div class="content-grid"><div class="stack"><section class="panel hero-session"><div class="panel-title"><h2>Current session</h2><div id="session-pill">${statusPill(account)}</div></div><div class="session-visual" aria-hidden="true"><svg viewBox="0 0 64 64"><path d="M19 20h26l9 24a6 6 0 0 1-10 6l-8-9h-8l-8 9a6 6 0 0 1-10-6z"/><path d="M19 30h12m-6-6v12m17-8h1m4 7h1"/></svg></div><h3 class="session-name" data-live="name">${escapeHtml(account.label)}</h3><p class="session-detail" data-live="detail">${escapeHtml(account.detail)}</p><div class="button-row">${button("Pause", "pause")}${button("Resume", "resume")}${button("Choose games", "go-library", true)}</div></section><section class="panel"><div class="panel-title"><div><h2>Games reported as playing</h2><p>Steam controls which game appears on your profile.</p></div></div><div id="playing-games" class="games-strip"></div></section><section class="panel"><div class="panel-title"><h2>Recent activity</h2>${button("Diagnostics", "go-diagnostics")}</div><div id="events"></div></section></div><div class="stack"><section class="panel"><div class="panel-title"><h2>Connection details</h2></div><div class="key-values"><div class="key-value"><span>Last Steam response</span><span data-live="last-proof">Not yet</span></div><div class="key-value"><span>Library synced</span><span data-live="last-library">Not yet</span></div><div class="key-value"><span>Reconnect attempts</span><span data-live="reconnects">0</span></div><div class="key-value"><span>Saved session</span><span data-live="saved">No</span></div></div><div class="divider"></div><p class="small-text muted">Network errors reconnect automatically. Expired or rejected credentials pause sign-in attempts.</p></section><section class="panel"><div class="panel-title"><h2>Profile presence</h2></div><p class="small-text" data-live="presence">Not checked yet.</p><p class="hint">Last checked: <span data-live="presence-at">Not yet</span></p></section><section class="panel"><div class="panel-title"><h2>AFK replies</h2></div><p class="small-text muted">${ui.config.afk.enabled ? escapeHtml(ui.config.afk.message) : "Replies are off. Turn them on when you want friends to know you are away."}</p><div class="form-actions">${button("Manage replies", "go-afk")}</div></section></div></div>`;
}
function updateLive() {
  if (!ui.data) return;
  const account = selected();
  if (!account) return;
  const values = {
    connected: ui.data.accounts.filter(isGood).length,
    games: ui.data.accounts.reduce((n, a) => n + a.playingAppIds.length, 0),
    session: hours(account.sessionSeconds),
    library: account.libraryCount,
    name: account.personaName || account.label,
    detail: account.detail,
    "last-proof": timeAgo(account.lastSteamResponse),
    "last-library": timeAgo(account.lastLibrarySync),
    reconnects: account.reconnects,
    saved: account.hasSavedSession ? "Available" : "Not saved",
    presence: account.reportedPresence || "Not checked yet.",
    "presence-at": timeAgo(account.presenceCheckedAt),
    "requested-profile": account.requestedProfileDisplay ?? "Not active",
    "friend-profile": account.friendObservedGame ?? "Unavailable",
    "next-profile": account.nextProfileDisplayChange ? new Date(account.nextProfileDisplayChange).toLocaleString() : "Not scheduled",
  };
  for (const [name, value] of Object.entries(values))
    $$(`[data-live="${name}"]`).forEach((node) => (node.textContent = value));
  if ($("#session-pill")) $("#session-pill").innerHTML = statusPill(account);
  if ($("#playing-games"))
    $("#playing-games").innerHTML = account.playingNames.length
      ? account.playingNames
          .map(
            (name, i) =>
              `<span class="game-chip ${i === 0 ? "primary-game" : ""}">${escapeHtml(name)}</span>`,
          )
          .join("")
      : `<p class="empty-inline">No games are currently being reported as playing.</p>`;
  if ($("#events")) {
    const events = ui.data.events
      .filter((event) => !event.accountId || event.accountId === ui.selected)
      .slice(0, 6);
    $("#events").innerHTML = events.length
      ? events
          .map(
            (event) =>
              `<div class="event"><time datetime="${event.at}">${new Date(event.at).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}</time><div><p>${escapeHtml(event.message)}</p><div class="event-level">${escapeHtml(event.level)}</div></div></div>`,
          )
          .join("")
      : `<p class="empty-inline">Session events will appear here.</p>`;
  }
}
async function loadLibrary() {
  const accountId = ui.selected,
    search = ui.search,
    offset = ui.offset;
  const result = await api(
    `/api/accounts/${ui.selected}/library?search=${encodeURIComponent(ui.search)}&offset=${ui.offset}&limit=60`,
  );
  if (accountId !== ui.selected || search !== ui.search || offset !== ui.offset)
    return;
  ui.library = result.games;
  ui.libraryTotal = result.total;
}
function renderLibrary() {
  ui.libraryDirty = false;
  const config = ui.config,
    selectedIds = new Set(config.selectedAppIds);
  $("#main").innerHTML =
    heading(
      "A library with a plan.",
      "Choose owned games, a random subset, or a rotating group.",
      `<div class="button-row">${button("Refresh library", "refresh-library")}${button("Save selection", "save-library", true)}</div>`,
    ) +
    `<section class="panel"><div class="library-options"><div class="field"><label for="selection-mode">Selection mode</label><select id="selection-mode">${option("manual", "My selected games", config.selectionMode)}${option("random", "Random owned games", config.selectionMode)}${option("rotate", "Rotate game groups", config.selectionMode)}</select></div><div class="field"><label for="random-count">Games per group</label><input type="number" id="random-count" min="1" max="32" value="${config.randomCount}"></div><div class="field"><label for="preferred-app">Preferred profile game</label><select id="preferred-app">${option("", "Let Steam choose", config.preferredAppId ?? "")}</select></div></div><div class="field-row"><div class="field"><label for="rotation-minutes">Rotate every (minutes)</label><input type="number" min="5" max="10080" id="rotation-minutes" value="${config.rotationMinutes}"></div><div class="field"><label for="excluded-apps">Excluded app IDs</label><input id="excluded-apps" value="${config.excludedAppIds.join(", ")}" placeholder="For example: 123, 456"><p class="hint">Excluded titles stay out of automatic selections.</p></div></div><p class="callout">Up to 32 games can be reported at once. The preferred game is sent first; Steam decides the profile display. Rotation uses your selected games, or your whole library when none are selected.</p><div class="library-toolbar"><div class="search-field"><label for="game-search">Search your library</label><input id="game-search" type="search" placeholder="Game name or app ID" value="${escapeHtml(ui.search)}"></div><span class="library-count">${ui.libraryTotal} games · <span id="selection-count">${selectedIds.size}</span> selected</span>${button("Pick a random group", "randomize")}</div><div class="game-grid">${
      ui.library
        .map(
          (game) =>
            `<div class="game-row ${selectedIds.has(game.appId) ? "selected" : ""}"><span class="app-tile" aria-hidden="true">${escapeHtml(
              game.name
                .split(/\s+/)
                .slice(0, 2)
                .map((word) => word[0])
                .join("")
                .toUpperCase(),
            )}</span><div class="game-copy"><p class="game-title" title="${escapeHtml(game.name)}">${escapeHtml(game.name)}</p><p class="game-meta">App ${game.appId} · ${game.steamHoursReported === false ? "Steam hours unavailable" : (game.steamMinutes / 60).toFixed(1) + " Steam hours"}</p></div><div class="game-actions"><button class="text-button" data-action="target" data-app="${game.appId}" title="Set hour target" aria-label="Set hour target for ${escapeHtml(game.name)}">Target</button><input type="checkbox" data-game="${game.appId}" aria-label="Select ${escapeHtml(game.name)}" ${selectedIds.has(game.appId) ? "checked" : ""}></div></div>`,
        )
        .join("") ||
      `<p class="empty-inline">${selected()?.loggedIn ? "No matching owned games. Refresh the library or clear the search." : "Sign in to load your Steam library."}</p>`
    }</div><div class="pagination"><span>${ui.libraryTotal ? ui.offset + 1 : 0}–${Math.min(ui.offset + 60, ui.libraryTotal)} of ${ui.libraryTotal}</span><div class="button-row">${button("Previous", "previous", false, ui.offset === 0 ? "disabled" : "")}${button("Next", "next", false, ui.offset + 60 >= ui.libraryTotal ? "disabled" : "")}</div></div></section>` + `<section class="panel form-panel"><form id="profile-display-form"><h2>Profile display</h2><p class="hint">Request a preferred title at a time. With multiple simultaneous games, Steam may ignore the requested order. Real-game display rotation is not reliable while idling many games; compare the reported presence on Overview. This does not edit your profile name or bio.</p>${field('<label for="profile-mode">Display mode</label>', `<select id="profile-mode" name="mode">${option("default", "Use existing preferred game", config.profileDisplay?.mode ?? "default")}${option("rotate", "Request rotating Steam title (best effort)", config.profileDisplay?.mode)}${option("custom", "Custom non-Steam status", config.profileDisplay?.mode)}${option("mixed", "Rotate games and custom status", config.profileDisplay?.mode)}</select>`)}<div id="profile-rotation-options" ${["rotate","mixed"].includes(config.profileDisplay?.mode) ? "" : "hidden"}>${field('<label for="profile-interval">Switch every (minutes)</label>', `<input id="profile-interval" name="interval" type="number" min="5" max="10080" required value="${config.profileDisplay?.intervalMinutes ?? 60}">`, '60 minutes = one hour. The saved UTC rotation clock survives restarts and continues while paused. Saving a changed display configuration starts its rotation again.')}</div><div id="profile-custom-options" ${["custom","mixed"].includes(config.profileDisplay?.mode) ? "" : "hidden"}>${field('<label for="profile-custom">Custom display text</label>', `<input id="profile-custom" name="custom" maxlength="80" value="${escapeHtml(config.profileDisplay?.customText ?? "")}" placeholder="Type your custom status">`, 'Shown as a non-Steam game when supported. Up to 80 characters / 128 UTF-8 bytes. No game hours are earned for this text.')}</div><div id="profile-games-options" ${["rotate","mixed"].includes(config.profileDisplay?.mode) ? "" : "hidden"}><div class="field"><span id="profile-games-label">Games in the display rotation</span><p class="hint">Select from your owned library below. Rotation follows the listed order, with custom text last in mixed mode. Excluded games and completed hour targets are skipped.</p><div id="profile-display-games" class="stack" role="group" aria-labelledby="profile-games-label">Loading library…</div></div></div><p class="callout">A selected display game is included among active games and sent first. At the 32-game limit, an extra display game replaces the last active game. Custom text uses one slot, leaving up to 31 Steam games active. Other selections remain saved. Pause, schedule and playing-elsewhere rules still apply.</p><p class="hint">Requested: <span data-live="requested-profile">${escapeHtml(selected()?.requestedProfileDisplay ?? "Not active")}</span><br>Friend-view title (cached): <span data-live="friend-profile">${escapeHtml(selected()?.friendObservedGame ?? "Unavailable")}</span><br>Next switch: <span data-live="next-profile">${selected()?.nextProfileDisplayChange ? escapeHtml(new Date(selected().nextProfileDisplayChange).toLocaleString()) : "Not scheduled"}</span></p><div class="form-actions"><button type="submit" class="button primary">Save profile display</button><span id="profile-display-feedback" role="status"></span></div></form></section>`;
  syncProfileDisplayFields();
  loadPreferredOptions().catch((error) => toast(error.message));
}
function syncProfileDisplayFields() {
  const mode = $("#profile-mode")?.value ?? "default";
  const rotating = mode === "rotate" || mode === "mixed";
  const custom = mode === "custom" || mode === "mixed";
  const customInput = $("#profile-custom");
  if (customInput) customInput.required = custom;
  for (const [id, enabled] of [["profile-rotation-options", rotating], ["profile-games-options", rotating], ["profile-custom-options", custom]]) {
    const area = $("#" + id);
    if (!area) continue;
    area.hidden = !enabled;
    area.querySelectorAll("input, select").forEach(input => input.disabled = !enabled);
  }
}
async function loadPreferredOptions() {
  const accountId = ui.selected;
  const stats = await api(`/api/accounts/${accountId}/statistics`);
  if (ui.selected !== accountId) return;
  const control = $("#preferred-app");
  if (!control) return;
  control.innerHTML =
    option("", "Let Steam choose", ui.config.preferredAppId ?? "") +
    Object.values(stats.games)
      .sort((a, b) => a.name.localeCompare(b.name))
      .map((game) => option(game.appId, game.name, ui.config.preferredAppId))
      .join("");
  const pool = $("#profile-display-games");
  if (pool) pool.innerHTML = Object.values(stats.games).sort((a,b)=>a.name.localeCompare(b.name)).map(game => `<label class="check-label"><input type="checkbox" name="display-game" value="${game.appId}" ${(ui.config.profileDisplay?.appIds ?? []).includes(game.appId) ? "checked" : ""}>${escapeHtml(game.name)}</label>`).join("") || '<p class="hint">Sign in and refresh your Steam library to choose display games.</p>';
  syncProfileDisplayFields();
}
async function saveConfig() {
  ui.config = await api(
    `/api/accounts/${ui.selected}/settings`,
    "PUT",
    ui.config,
  );
  ui.dirty = false;
}
async function saveLibrary() {
  ui.config.selectionMode = $("#selection-mode").value;
  ui.config.randomCount = Number($("#random-count").value);
  ui.config.rotationMinutes = Number($("#rotation-minutes").value);
  ui.config.preferredAppId = $("#preferred-app").value
    ? Number($("#preferred-app").value)
    : null;
  const ids = $("#excluded-apps")
    .value.split(",")
    .map((x) => x.trim())
    .filter(Boolean);
  if (ids.some((x) => !/^\d+$/.test(x) || Number(x) > 4294967295))
    throw new Error("Excluded app IDs must be positive whole numbers.");
  ui.config.excludedAppIds = ids.map(Number);
  await saveConfig();
  ui.libraryDirty = false;
}
async function ensureZones() {
  if (!ui.zones.length) ui.zones = await api("/api/timezones");
}
function zoneSelect(id, value) {
  return `<select id="${id}" name="${id}">${ui.zones.map((zone) => option(zone.id, zone.label, value)).join("")}</select>`;
}
function renderSchedule() {
  const schedule = ui.config.schedule;
  const days = [
    "sunday",
    "monday",
    "tuesday",
    "wednesday",
    "thursday",
    "friday",
    "saturday",
  ];
  $("#main").innerHTML =
    heading(
      "Make room for your routine.",
      "A schedule controls when this account reports games as playing.",
      '<button class="button primary" type="submit" form="schedule-form">Save schedule</button>',
    ) +
    `<form id="schedule-form" class="panel form-panel"><div class="panel-title"><div><h2>Idling schedule</h2><p>Steam remains connected outside the schedule, but games are paused.</p></div></div><div class="field"><label class="check-label"><input name="enabled" type="checkbox" ${schedule.enabled ? "checked" : ""}><strong>Use a schedule</strong></label></div>${field('<label for="time-zone">Time zone</label>', zoneSelect("time-zone", schedule.timeZone))}<div class="field-row">${field('<label for="start-time">Start</label>', `<input id="start-time" name="start" type="time" required value="${schedule.start}">`)}${field('<label for="end-time">End</label>', `<input id="end-time" name="end" type="time" required value="${schedule.end}">`)}</div><div class="field"><label>Starting days</label><div class="days">${days.map((day) => `<label class="day"><input type="checkbox" name="day" value="${day}" ${schedule.days.includes(day) ? "checked" : ""}>${day.slice(0, 3)}</label>`).join("")}</div></div><p class="callout">Overnight schedules belong to their starting day. Equal start and end times allow a full day. Daylight saving changes follow the selected time zone.</p><div class="form-actions"><button class="button primary" type="submit">Save schedule</button><span id="schedule-feedback" class="small-text muted" role="status"></span></div></form>`;
}
function renderAfk() {
  const afk = ui.config.afk;
  $("#main").innerHTML =
    heading(
      "Let friends know you’re away.",
      "Configure private AFK replies and rotating group replies.",
      '<button class="button primary" type="submit" form="afk-form">Save reply settings</button>',
    ) +
    `<form id="afk-form" class="panel form-panel"><div class="panel-title"><div><h2>Automatic replies</h2><p>Private, group and friend welcome messages wait three seconds before sending. AI generation may add time. Cooldowns still apply.</p></div></div><label class="check-label field"><input name="enabled" type="checkbox" ${afk.enabled ? "checked" : ""}><strong>Enable AFK replies</strong></label>${field('<label for="afk-message">Your reply</label>', `<textarea id="afk-message" name="message" maxlength="500" required>${escapeHtml(afk.message)}</textarea>`)}<div class="field-row">${field('<label for="afk-cooldown">Cooldown per friend (minutes)</label>', `<input id="afk-cooldown" name="cooldown" type="number" min="1" max="1440" required value="${afk.cooldownMinutes}">`)}${field('<label for="excluded-friends">Excluded Steam IDs</label>', `<input id="excluded-friends" name="excluded" value="${escapeHtml(afk.excludedFriends.join(", "))}" placeholder="Comma-separated SteamID64 values">`)}</div><div class="form-section"><h3>Quiet hours</h3><label class="check-label field"><input name="quiet" type="checkbox" ${afk.quietHours.enabled ? "checked" : ""}>Do not reply during quiet hours</label>${field('<label for="quiet-zone">Time zone</label>', zoneSelect("quiet-zone", afk.quietHours.timeZone))}<div class="field-row">${field('<label for="quiet-start">Start</label>', `<input id="quiet-start" name="quiet-start" type="time" value="${afk.quietHours.start}" required>`)}${field('<label for="quiet-end">End</label>', `<input id="quiet-end" name="quiet-end" type="time" value="${afk.quietHours.end}" required>`)}</div></div><div class="form-section"><h3>AI replies · Groq</h3><p class="hint">Selected chat text is sent to Groq when AI is enabled. The last two AI exchanges per chat may be sent as context. They stay only in server memory for 20 minutes and clear on restart. Existing friend/group enable switches, exclusions, quiet hours and cooldowns still apply. Custom replies are used when AI is unavailable. Keep your Groq account on its Free plan; this app cannot inspect or enforce provider billing.</p><label class="check-label field"><input name="ai-private" type="checkbox" ${ui.config.aiReplies?.privateMessages ? "checked" : ""}>Use AI for private AFK replies</label><label class="check-label field"><input name="ai-groups" type="checkbox" ${ui.config.aiReplies?.groups ? "checked" : ""}>Use AI for group replies</label>${field('<label for="ai-instructions">AI instructions</label>', `<textarea id="ai-instructions" name="ai-instructions" maxlength="2000" rows="5" required>${escapeHtml(ui.config.aiReplies?.instructions ?? "Use casual, friendly language. Keep replies to one or two short sentences. Say you are an automated assistant replying while the account owner is away. Do not invent facts about the owner.")}</textarea>`)}<div class="field-row">${field('<label for="ai-daily">AI requests per UTC day for this account</label>', `<input id="ai-daily" name="ai-daily" type="number" min="1" max="500" required value="${ui.config.aiReplies?.dailyLimit ?? 100}">`)}${field('<label for="ai-cooldown">AI cooldown per chat (seconds)</label>', `<input id="ai-cooldown" name="ai-cooldown" type="number" min="2" max="3600" required value="${ui.config.aiReplies?.cooldownSeconds ?? 15}">`)}</div><p class="hint">A shared maximum of 500 requests per UTC day and one request every two seconds applies across accounts, including previews and failed attempts. Faster traffic uses custom replies.</p></div><div class="form-section"><h3>Group auto replies</h3><label class="check-label field"><input name="group-enabled" type="checkbox" ${ui.config.groupReplies?.enabled ? "checked" : ""}>Enable rotating replies in all joined group chats</label><p class="hint">One reply per incoming message: 1 → 2 → 3 → 1, separately in each chat room. Add or join groups in Steam first. Own messages, system notices and old messages are ignored. The sequence resets when HourBoostr restarts. Subscriptions update within 30 seconds.</p>${[0,1,2].map(i => field(`<label for="group-reply-${i}">Group reply ${i+1}</label>`, `<textarea id="group-reply-${i}" name="group-reply-${i}" rows="3" maxlength="500">${escapeHtml(ui.config.groupReplies?.messages?.[i] ?? "")}</textarea>`)).join("")}${field('<label for="group-cooldown">Minimum time between group replies (seconds)</label>', `<input id="group-cooldown" name="group-cooldown" type="number" min="1" max="3600" required value="${ui.config.groupReplies?.cooldownSeconds ?? 5}">`, 'Messages received during the cooldown are skipped without advancing the sequence. Start at 5 seconds to reduce reply loops with other bots. Applies separately to each chat room.')}</div><p class="callout">Cooldowns are shared across message transports. Your own message echoes are ignored. Message contents are not written to the activity log.</p><div class="form-actions"><button class="button primary" type="submit">Save reply settings</button><span id="afk-feedback" class="small-text muted" role="status"></span></div></form>` + `<section class="panel form-panel"><h3>Groq connection · shared across accounts</h3><p id="ai-status" role="status">Loading connection status…</p><p class="hint">Configure Groq on the machine running HourBoostr. The browser does not receive the key.</p><form id="ai-preview-form">${field('<label for="ai-preview">Preview incoming message</label>', '<textarea id="ai-preview" name="message" maxlength="1000" rows="2" required></textarea>')}<button class="button" type="submit">Generate preview · no Steam message sent</button><p class="callout" id="ai-preview-result" role="status">Save reply settings before previewing. A preview uses your request budget.</p></form></section>`;
  $("#main").insertAdjacentHTML("beforeend", window.HBRGroupRegulars.card);
  window.HBRGroupRegulars.mount(document.getElementById("group-regulars"), ui.selected);
  $("#main").insertAdjacentHTML("beforeend", window.HBRProfileComments.card);
  window.HBRProfileComments.mount(document.getElementById("profile-comments"), ui.selected);
  loadAiStatus();
}
async function loadAiStatus() {
  try {
    const data = await api("/api/ai/status");
    const target = $("#ai-status");
    if (target) target.textContent = `${data.configured ? "Key saved" : "No key saved"} · ${data.model} · ${data.requestsToday}/${data.globalDailyLimit} requests today (UTC) · ${data.status}`;
  } catch (error) { const target = $("#ai-status"); if (target) target.textContent = error.message; }
}

function renderStatistics() {
  const stats = ui.stats;
  const games = Object.values(stats.games).sort(
    (a, b) => b.idleSeconds - a.idleSeconds,
  );
  const days = Object.entries(stats.dailySeconds)
    .sort(([a], [b]) => a.localeCompare(b))
    .slice(-14);
  const totals = days.map(([date, values]) => [
    date,
    Object.values(values).reduce((a, b) => a + b, 0),
  ]);
  const maximum = Math.max(3600, ...totals.map(([, seconds]) => seconds));
  const chart = totals.length
    ? `<svg class="chart" viewBox="0 0 720 160" role="img" aria-label="Estimated combined game-hours by day">${totals
        .map(([date, seconds], i) => {
          const width = 680 / totals.length;
          const h = (seconds / maximum) * 112;
          return `<rect x="${20 + i * width}" y="${125 - h}" width="${Math.max(2, width - 10)}" height="${h}" rx="3"><title>${date}: ${hours(seconds)} estimated game-hours</title></rect><text x="${20 + i * width}" y="150">${date.slice(5)}</text>`;
        })
        .join("")}</svg>`
    : `<p class="empty-inline">Your activity will appear after games have been idling.</p>`;
  $("#main").innerHTML =
    heading(
      "Time, with context.",
      "Estimated idling time and Steam-reported playtime are different measurements.",
      button("Refresh statistics", "refresh-page"),
    ) +
    `<div class="stats-grid"><div class="stat-card"><p class="label">SESSION WALL TIME</p><div class="stat-value">${hours(stats.sessionSeconds)} <span class="small-text muted">hr</span></div></div><div class="stat-card"><p class="label">RECORDED GAME-HOURS</p><div class="stat-value">${hours(games.reduce((n, game) => n + game.idleSeconds, 0))}</div><p class="stat-footnote">Combined across games</p></div><div class="stat-card"><p class="label">GAMES IN LIBRARY</p><div class="stat-value">${games.length}</div></div><div class="stat-card"><p class="label">LIBRARY SYNC</p><div class="stat-value small-text">${timeAgo(stats.lastLibrarySync)}</div></div></div><section class="panel"><div class="panel-title"><div><h2>Recent activity</h2><p>Combined estimated game-hours per UTC day. Several simultaneous games accumulate time independently.</p></div></div>${chart}</section><section class="panel form-section"><div class="panel-title"><h2>Game totals</h2></div><div class="table-wrap"><table><thead><tr><th>Game</th><th class="numeric">Steam reported</th><th class="numeric">Recorded here</th><th class="numeric">Estimated total</th><th class="numeric">Target</th></tr></thead><tbody>${
      games
        .slice(0, 100)
        .map(
          (game) =>
            `<tr><td>${escapeHtml(game.name)}</td><td class="numeric">${game.steamHoursReported === false ? "Unavailable" : (game.steamMinutes / 60).toFixed(1) + " hr"}</td><td class="numeric">${hours(game.idleSeconds)} hr</td><td class="numeric">${hours(game.estimatedTotalSeconds)} hr</td><td class="numeric">${ui.config.targetHours[String(game.appId)] ? `${ui.config.targetHours[String(game.appId)]} hr` : "—"}</td></tr>`,
        )
        .join("") ||
      '<tr><td colspan="5">Sign in to load game statistics.</td></tr>'
    }</tbody></table></div><p class="hint">Targets use the estimated total and stop further idling for that game. Steam’s reported hours may update later.</p></section>`;
}
let desktopState = { startup: false, minimizeOnClose: true };
function desktopPanel() {
  if (!window.chrome?.webview) return `<section class="panel form-panel"><h2>Windows startup and tray</h2><p class="muted small-text">Open HourBoostr.Desktop.exe from the Windows package to use startup and tray controls. Startup opens directly in the tray.</p></section>`;
  return `<section class="panel form-panel"><h2>Windows startup and tray</h2><label class="check-label"><input id="desktop-startup" type="checkbox" ${desktopState.startup ? "checked" : ""}>Start with Windows</label><p class="hint">Starts when you sign into Windows, directly in the system tray.</p><label class="check-label"><input id="desktop-close-to-tray" type="checkbox" ${desktopState.minimizeOnClose ? "checked" : ""}>Close window to tray</label><p class="hint">Keep Steam sessions running when you close the window. Choose Exit from the tray menu to stop the app. These preferences save immediately.</p><div class="form-actions">${button("Minimize to tray", "desktop-minimize")}</div></section>`;
}
if (window.chrome?.webview) {
  window.chrome.webview.addEventListener("message", ({ data }) => {
    if (data?.type !== "desktop-state") return;
    desktopState = data;
    if ($("#desktop-startup")) $("#desktop-startup").checked = data.startup;
    if ($("#desktop-close-to-tray")) $("#desktop-close-to-tray").checked = data.minimizeOnClose;
    if (data.error) toast(data.error);
  });
  document.addEventListener("change", (event) => {
    if (["desktop-startup", "desktop-close-to-tray"].includes(event.target.id)) {
      window.chrome.webview.postMessage({ action: event.target.id, enabled: event.target.checked });
    }
  });
}
function renderSettings() {
  $("#main").innerHTML =
    heading(
      "Your installation, your choices.",
      "Manage account settings, releases, and local access.",
    ) +
    `<div class="stack"><section class="panel form-panel"><div class="panel-title"><div><h2>Account settings</h2><p>Session credentials are stored separately from ordinary configuration.</p></div></div>${ui.config ? `<form id="account-form">${field('<label for="account-label">Account label</label>', `<input id="account-label" name="label" maxlength="80" required value="${escapeHtml(ui.config.label)}">`)}${field('<label for="persona-state">Friends status</label>', `<select id="persona-state" name="presence">${option("online", "Online", ui.config.presence)}${option("away", "Away", ui.config.presence)}${option("invisible", "Invisible", ui.config.presence)}</select>`)}<label class="check-label"><input name="enabled" type="checkbox" ${ui.config.enabled ? "checked" : ""}>Enable this account</label><label class="check-label"><input name="auto-accept-friends" type="checkbox" ${ui.config.autoAcceptFriendRequests ? "checked" : ""}>Automatically accept incoming friend requests</label><p class="hint">Accept pending and new requests from individual Steam accounts. Idling always respects Steam’s playing-elsewhere lock.</p><label class="check-label"><input name="friend-welcome-enabled" type="checkbox" ${ui.config.friendWelcomeEnabled ? "checked" : ""}>Send a welcome message after automatic acceptance</label>${field('<label for="friend-welcome-message">Friend welcome message</label>', `<textarea id="friend-welcome-message" name="friend-welcome-message" maxlength="500" rows="3">${escapeHtml(ui.config.friendWelcomeMessage ?? "Thanks for adding me! I am AFK right now and will respond later.")}</textarea>`, "Requires automatic friend acceptance. Waits three seconds, then sends once per Steam friend after successful acceptance. Existing friends are not messaged. Delivery failures are logged without automatic retries.")}<div class="form-actions"><button class="button primary" type="submit">Save account</button>${button("Forget saved session", "forget")}${button("Remove account", "remove", false, 'data-tone="danger"')}</div></form>` : '<p class="muted">Add an account to configure its settings.</p>'}</section><section class="panel form-panel"><div class="panel-title"><div><h2>Releases and updates</h2><p>Current version: ${escapeHtml(ui.data.version)}</p></div></div><form id="release-form">${field('<label for="release-repo">Remaster release repository</label>', '<input id="release-repo" name="repository" placeholder="owner/HourBoostr-Remastered">', "Set this after the remaster is published. Updates are checked only when you ask.")}<div class="button-row"><button class="button" type="submit">Save release source</button>${button("Check for updates", "check-updates")}</div></form><p id="update-result" class="callout" role="status">No update check has run.</p><p class="hint">The CLI verifies SHA-256 checksums before staging an update. Apply and rollback operate on stopped, managed release directories.</p></section>${desktopPanel()}<section class="panel form-panel"><h2>About HourBoostr Remastered by Otter</h2><p class="muted small-text">Created by Otter as a continuation of Ezzpify’s HourBoostr, distributed under GPL-3.0. <a href="https://steamcommunity.com/id/pornhub_sex5/" target="_blank" rel="noopener noreferrer">Otter’s Steam profile</a>. Steam decides playtime accounting and profile presence. Hosting availability and uninterrupted account access are not guaranteed.</p></section></div>`;
}

function openLogin() {
  if (!ui.selected) return toast("Add an account first.");
  ui.authId = ui.selected;
  ui.authStarted = false;
  ui.authSignature = "";
  dialog(
    "Sign in to Steam",
    `<div class="method-tabs" role="group" aria-label="Sign-in method"><button class="selected" data-action="method-password">Password &amp; Steam Guard</button><button data-action="method-qr">Scan QR code</button></div><p class="hint">Use your Steam account login name, which may differ from your profile name. Local testing should use a different account from your VPS.</p><form id="steam-login-form"><div id="password-fields"><div class="field"><label for="steam-login">Steam login name</label><input id="steam-login" name="loginName" autocomplete="username" autocapitalize="none" spellcheck="false" value="${escapeHtml(ui.config?.loginName || "")}" required></div><div class="field"><label for="steam-password">Password</label><input id="steam-password" name="password" type="password" autocomplete="current-password" required></div></div><p id="dialog-error" class="error" role="alert"></p><button class="button primary" id="start-signin" type="submit">Sign in</button></form><div class="auth-progress" id="auth-progress" aria-live="polite"></div>`,
    async (form, element) => {
      if (element.id === "guard-form") {
        await api(`/api/accounts/${ui.authId}/guard`, "POST", {
          code: form.get("code"),
        });
        $("#guard-code").value = "";
        toast("Code submitted to Steam.");
        return;
      }
      try {
        await api(`/api/accounts/${ui.authId}/login`, "POST", {
          method: ui.authMethod,
          loginName:
            ui.authMethod === "password" ? form.get("loginName") : null,
          password: ui.authMethod === "password" ? form.get("password") : null,
        });
      } finally {
        $("#steam-password").value = "";
      }
      ui.authStarted = true;
      $("#steam-login-form").hidden = true;
      await poll();
    },
  );
  ui.authMethod = "password";
}
function updateAuth() {
  if (!ui.authId || !ui.authStarted || !$("#dialog").open) return;
  const account = ui.data.accounts.find((a) => a.id === ui.authId);
  if (!account) return;
  if (account.loggedIn) {
    closeDialog();
    toast("Steam sign-in succeeded.");
    render().catch((error) => toast(error.message));
    return;
  }
  const signature = JSON.stringify([
    account.state,
    account.detail,
    account.qrSvg,
  ]);
  if (signature === ui.authSignature) return;
  ui.authSignature = signature;
  const progress = $("#auth-progress");
  if (!progress) return;
  if (account.state === "signed_out") return;
  progress.innerHTML = `<p class="small-text">${["connecting", "authenticating", "waiting_approval"].includes(account.state) ? '<span class="spinner" aria-hidden="true"></span>' : ""}${escapeHtml(account.detail)}</p>${account.qrSvg ? '<div class="qr-frame" id="qr-frame"></div>' : ""}${account.state === "waiting_guard" ? '<form id="guard-form"><div class="field"><label for="guard-code">Steam Guard code</label><input id="guard-code" name="code" autocomplete="one-time-code" autocapitalize="characters" spellcheck="false" minlength="4" maxlength="10" required></div><button class="button primary" type="submit">Submit code</button></form>' : ""}${account.state === "auth_required" ? '<p class="error">Automatic sign-in retries are paused.</p><button class="button" data-action="login">Try again</button>' : ""}`;
  if (account.qrSvg) $("#qr-frame").innerHTML = account.qrSvg;
  if (account.state === "waiting_guard") $("#guard-code").focus();
}

document.addEventListener("click", async (event) => {
  const tab = event.target.closest("[data-tab]");
  const accountButton = event.target.closest("[data-account]");
  const action = event.target.closest("[data-action]");
  try {
    if (tab) {
      if (
        ui.dirty &&
        !confirm("You have unsaved changes. Leave this page without saving?")
      )
        return;
      ui.dirty = false;
      ui.tab = tab.dataset.tab;
      await render();
      return;
    }
    if (accountButton) {
      if (
        ui.dirty &&
        !confirm("You have unsaved changes. Switch accounts without saving?")
      )
        return;
      ui.dirty = false;
      ui.selected = accountButton.dataset.account;
      ui.offset = 0;
      ui.search = "";
      localStorage.setItem("hbr-account", ui.selected);
      renderAccounts();
      await render();
      return;
    }
    if (!action || action.disabled) return;
    const name = action.dataset.action;
    if (name.startsWith("go-")) {
      ui.tab = name.slice(3);
      await render();
      return;
    }
    if (name === "desktop-minimize") return window.chrome?.webview?.postMessage({action: name});
    if (name === "close-dialog") return closeDialog();
    if (name === "add")
      return dialog(
        "Add a Steam account",
        `<form><div class="field"><label for="new-label">Account label</label><input id="new-label" name="label" required maxlength="80" placeholder="For example: My second account"></div><p class="hint">This label stays in your local configuration. You will sign into Steam separately.</p><p class="error" id="dialog-error" role="alert"></p><button class="button primary" type="submit">Add account</button></form>`,
        async (form) => {
          const account = await api("/api/accounts", "POST", {
            label: form.get("label"),
          });
          ui.selected = account.id;
          ui.config = account;
          closeDialog();
          ui.tab = "overview";
          await poll();
          await render();
          openLogin();
        },
      );
    if (name === "login") return openLogin();
    if (name.startsWith("method-")) {
      ui.authMethod = name === "method-qr" ? "qr" : "password";
      $("#steam-login-form").hidden = false;
      $("#password-fields").hidden = ui.authMethod === "qr";
      $("#steam-login").required = ui.authMethod !== "qr";
      $("#steam-password").required = ui.authMethod !== "qr";
      $("#start-signin").textContent =
        ui.authMethod === "qr" ? "Create sign-in QR code" : "Sign in";
      $$(".method-tabs button").forEach((node) =>
        node.classList.toggle("selected", node === action),
      );
      return;
    }
    if (name === "lock") {
      await api("/api/session", "DELETE");
      showPairing();
      return;
    }
    if (["pause", "resume"].includes(name)) {
      await api(`/api/accounts/${ui.selected}/${name}`, "POST");
      toast(name === "pause" ? "Pause requested." : "Resume requested.");
      await poll();
      return;
    }
    if (name === "refresh-page") return render();
    if (name === "save-library") {
      await saveLibrary();
      toast("Game selection saved.");
      return;
    }
    if (name === "randomize") {
      await saveLibrary();
      await api(`/api/accounts/${ui.selected}/randomize`, "POST");
      ui.config = await api(`/api/accounts/${ui.selected}/settings`);
      renderLibrary();
      toast("A new random group will be selected.");
      return;
    }
    if (name === "refresh-library") {
      action.disabled = true;
      await api(`/api/accounts/${ui.selected}/refresh`, "POST");
      await loadLibrary();
      renderLibrary();
      toast("Library refresh finished.");
      return;
    }
    if (["next", "previous"].includes(name)) {
      ui.offset = Math.max(0, ui.offset + (name === "next" ? 60 : -60));
      await loadLibrary();
      renderLibrary();
      return;
    }
    if (name === "target") {
      const appId = Number(action.dataset.app);
      const game = ui.library.find((item) => item.appId === appId);
      return dialog(
        "Set an hour target",
        `<form><p class="muted small-text">${escapeHtml(game?.name || `App ${appId}`)}</p><div class="field"><label for="target-hours">Target estimated total hours</label><input id="target-hours" name="hours" type="number" min="0.01" step="0.01" max="1000000" value="${ui.config.targetHours[String(appId)] || ""}"><p class="hint">Leave blank to remove this target. Estimates include Steam-reported playtime and time recorded here.</p></div><p class="error" id="dialog-error" role="alert"></p><button class="button primary" type="submit">Save target</button></form>`,
        async (form) => {
          const value = form.get("hours");
          if (value === "") delete ui.config.targetHours[String(appId)];
          else ui.config.targetHours[String(appId)] = Number(value);
          await saveConfig();
          closeDialog();
          toast("Hour target saved.");
        },
      );
    }
    if (["forget", "remove"].includes(name))
      return dialog(
        name === "forget" ? "Forget this session?" : "Remove this account?",
        `<form><p class="muted">${name === "forget" ? "This removes the saved session from this installation and stops this worker. It does not revoke other Steam devices." : "This stops the worker and removes this account’s local session, settings, and statistics."}</p><p class="error" id="dialog-error" role="alert"></p><div class="form-actions"><button class="button danger" type="submit">${name === "forget" ? "Forget session" : "Remove account"}</button></div></form>`,
        async () => {
          if (name === "forget")
            await api(`/api/accounts/${ui.selected}/forget`, "POST");
          else await api(`/api/accounts/${ui.selected}`, "DELETE");
          closeDialog();
          await poll();
          await render();
        },
      );
    if (name === "download-diagnostics") {
      const data = await api("/api/diagnostics");
      const url = URL.createObjectURL(
        new Blob([JSON.stringify(data, null, 2)], { type: "application/json" }),
      );
      const link = document.createElement("a");
      link.href = url;
      link.download = "hourboostr-diagnostics.json";
      link.click();
      URL.revokeObjectURL(url);
      return;
    }
    if (name === "check-updates") {
      $("#update-result").textContent =
        "Checking the configured release source…";
      const result = await api("/api/updates");
      $("#update-result").textContent =
        result.message ||
        (result.updateAvailable
          ? `Release ${result.latestVersion} is available. Use the CLI to verify and stage the package.`
          : `You are on the current release (${result.currentVersion}).`);
    }
  } catch (error) {
    toast(error.message);
  } finally {
    if (action?.isConnected) action.disabled = false;
  }
});
document.addEventListener("change", (event) => {
  if (event.target.id === "profile-mode") syncProfileDisplayFields();
  if (!event.target.matches("[data-game]")) return;
  const id = Number(event.target.dataset.game);
  const ids = new Set(ui.config.selectedAppIds);
  if (event.target.checked) ids.add(id);
  else ids.delete(id);
  ui.config.selectedAppIds = [...ids];
  event.target
    .closest(".game-row")
    .classList.toggle("selected", event.target.checked);
  $("#selection-count").textContent = ids.size;
});
document.addEventListener("input", (event) => {
  if (event.target.id !== "game-search") return;
  clearTimeout(searchTimer);
  const position = event.target.selectionStart;
  searchTimer = setTimeout(async () => {
    try {
      ui.search = event.target.value;
      ui.offset = 0;
      await loadLibrary();
      renderLibrary();
      $("#game-search").focus();
      $("#game-search").setSelectionRange(position, position);
    } catch (error) {
      toast(error.message);
    }
  }, 300);
});
document.addEventListener("submit", async (event) => {
  if (event.target.closest("dialog") || event.target.id === "pair-form") return;
  event.preventDefault();
  const form = new FormData(event.target);
  const submit = event.submitter;
  if (submit) submit.disabled = true;
  try {
    if (event.target.id === "profile-display-form") {
      if (ui.libraryDirty) throw new Error("Save your game selection first, then save the profile display.");
      const profileAccountId = ui.selected;
      const currentProfileConfig = await api(`/api/accounts/${profileAccountId}/settings`);
      if (ui.selected !== profileAccountId) throw new Error("The selected account changed. Try again.");
      currentProfileConfig.profileDisplay = {
        mode: String(form.get("mode")), customText: String(form.get("custom") ?? currentProfileConfig.profileDisplay?.customText ?? ""),
        intervalMinutes: Number(form.get("interval") ?? currentProfileConfig.profileDisplay?.intervalMinutes ?? 60),
        appIds: ["rotate", "mixed"].includes(String(form.get("mode"))) ? form.getAll("display-game").map(Number) : (currentProfileConfig.profileDisplay?.appIds ?? []),
      };
      const savedProfileConfig = await api(`/api/accounts/${profileAccountId}/settings`, "PUT", currentProfileConfig);
      if (ui.selected !== profileAccountId) return;
      ui.config = savedProfileConfig; ui.dirty = false;
      $("#profile-display-feedback").textContent = savedProfileConfig.profileDisplay.mode === "custom" ? `Custom status saved and requested: ${savedProfileConfig.profileDisplay.customText}. Steam decides visibility.` : "Profile display mode saved. Steam decides the visible status.";
      await poll();
    }
    if (event.target.id === "schedule-form") {
      ui.config.schedule = {
        enabled: form.has("enabled"),
        timeZone: form.get("time-zone"),
        start: form.get("start"),
        end: form.get("end"),
        days: form.getAll("day"),
      };
      await saveConfig();
      $("#schedule-feedback").textContent = "Schedule saved.";
    }
    if (event.target.id === "afk-form") {
      ui.config.afk = {
        enabled: form.has("enabled"),
        message: form.get("message"),
        cooldownMinutes: Number(form.get("cooldown")),
        excludedFriends: form
          .get("excluded")
          .split(",")
          .map((x) => x.trim())
          .filter(Boolean),
        quietHours: {
          enabled: form.has("quiet"),
          timeZone: form.get("quiet-zone"),
          start: form.get("quiet-start"),
          end: form.get("quiet-end"),
          days: [
            "sunday",
            "monday",
            "tuesday",
            "wednesday",
            "thursday",
            "friday",
            "saturday",
          ],
        },
      };
      ui.config.aiReplies = {
        privateMessages: form.has("ai-private"), groups: form.has("ai-groups"),
        instructions: String(form.get("ai-instructions") ?? ""),
        dailyLimit: Number(form.get("ai-daily")), cooldownSeconds: Number(form.get("ai-cooldown")),
      };
      ui.config.groupReplies = {
        enabled: form.has("group-enabled"),
        messages: [0,1,2].map(i => String(form.get(`group-reply-${i}`) ?? "")),
        cooldownSeconds: Number(form.get("group-cooldown")),
      };
      await saveConfig();
      $("#afk-feedback").textContent = "Reply settings saved.";
    }
    if (event.target.id === "ai-preview-form") {
      $("#ai-preview-result").textContent = "Generating preview…";
      const data = await api("/api/ai/preview", "POST", { accountId: ui.selected, message: String(form.get("message") ?? "") });
      $("#ai-preview-result").textContent = data.reply ?? `Custom reply would be used: ${data.details.status}`;
      await loadAiStatus();
    }
    if (event.target.id === "account-form") {
      ui.config.label = form.get("label");
      ui.config.presence = form.get("presence");
      ui.config.enabled = form.has("enabled");
      ui.config.autoAcceptFriendRequests = form.has("auto-accept-friends");
      ui.config.friendWelcomeEnabled = form.has("friend-welcome-enabled");
      ui.config.friendWelcomeMessage = String(form.get("friend-welcome-message") ?? "");
      await saveConfig();
      toast("Account settings saved.");
      await poll();
    }
    if (event.target.id === "release-form") {
      await api("/api/release-repository", "PUT", {
        repository: form.get("repository"),
      });
      toast("Release source saved.");
    }
  } catch (error) {
    toast(error.message);
  } finally {
    if (submit) submit.disabled = false;
  }
});
$("#pair-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  $("#pair-error").textContent = "";
  try {
    await api("/api/session", "POST", { key: $("#pair-key").value });
    $("#pair-key").value = "";
    showShell();
    await poll();
    await render();
  } catch (error) {
    $("#pair-error").textContent = error.message;
  }
});
function applyTheme(value) {
  document.documentElement.dataset.theme = value;
  $("#theme-toggle").setAttribute(
    "aria-label",
    `Switch to ${value === "dark" ? "light" : "dark"} theme`,
  );
}
applyTheme(localStorage.getItem("hbr-theme") || "dark");
$("#theme-toggle").addEventListener("click", () => {
  const value =
    document.documentElement.dataset.theme === "dark" ? "light" : "dark";
  localStorage.setItem("hbr-theme", value);
  applyTheme(value);
});
document.addEventListener("visibilitychange", () => {
  if (!document.hidden) poll();
});
(async () => {
  const fragment = new URLSearchParams(location.hash.slice(1));
  const key = fragment.get("key");
  if (key) {
    history.replaceState(null, "", location.pathname);
    try {
      await api("/api/session", "POST", { key });
    } catch (error) {
      $("#pair-error").textContent = error.message;
    }
  }
  await poll();
  if (ui.paired) await render();
  setInterval(poll, 2000);
})();

function markUnsaved(event) {
  if (event.target.matches("[data-game], #selection-mode, #random-count, #preferred-app, #rotation-minutes, #excluded-apps")) ui.libraryDirty = true;
  if (
    event.target.closest(
      "#schedule-form, #afk-form, #account-form, #release-form, #profile-display-form",
    ) ||
    event.target.matches(
      "[data-game], #selection-mode, #random-count, #preferred-app, #rotation-minutes, #excluded-apps",
    )
  ) {
    ui.dirty = true;
    const feedback = document.querySelector(
      "#afk-feedback, #schedule-feedback, #profile-display-feedback",
    );
    if (feedback) feedback.textContent = "Unsaved changes. Save to apply them.";
  }
}
document.addEventListener("input", markUnsaved);
document.addEventListener("change", markUnsaved);
