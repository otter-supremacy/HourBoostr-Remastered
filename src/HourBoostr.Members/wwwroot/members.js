"use strict";
const get = id => document.getElementById(id);
const nav = document.querySelector("nav");
let config, member, games = [], installedGames = "", busy = false, polling = false;
let recoveryCode = "", authPending = false;
let appearanceOwner, savingAppearance = false;
const authMode = { "/signup": "register", "/reset": "recover" }[location.pathname] || "login";
const states = { unlinked: "Steam is not linked.", pairing: "Waiting for Steam approval.", paused: "Paused", connecting: "Connecting",
  idling: "Idling", reconnecting: "Reconnecting", pairing_failed: "Link failed. Try again.", connection_failed: "Connection stopped. Try starting again." };
async function api(path, method = "GET", payload) {
  const options = { method, cache: "no-store" };
  if (method !== "GET") options.headers = { "X-HBR-Member": "1", "Content-Type": "application/json" };
  if (payload) options.body = JSON.stringify(payload);
  const response = await fetch(path, options);
  if (response.status === 401 && method === "GET") return null;
  if (!response.ok) {
    const details = await response.json().catch(() => ({}));
    throw Error(details.error || (response.status === 429 ? "Try again in a minute." : "The service could not complete the request."));
  }
  return response.status === 200 ? response.json() : true;
}
function notice(text) { get("notice").textContent = text; get("notice").hidden = !text; }
function render() {
  get("loading").hidden = true;
  get("save-recovery").hidden = !recoveryCode;
  get("signin").hidden = Boolean(member) || Boolean(recoveryCode);
  nav.hidden = !member || Boolean(recoveryCode);
  const account = location.hash === "#account";
  get("account").hidden = !member || !account || Boolean(recoveryCode);
  get("overview").hidden = !member || account || Boolean(recoveryCode);
  if (recoveryCode) return;
  document.dispatchEvent(new CustomEvent("hbr-member-change", { detail: { member, config, account } }));
  if (!member) {
    get("password-auth").hidden = !config.passwords;
    get("switch-auth").hidden = !config.passwords;
    get("google").hidden = !config.google || (config.passwords && authMode === "recover");
    get("auth-divider").hidden = get("google").hidden || !config.passwords;
    get("closed").hidden = config.google || config.passwords;
    get("public-preview").hidden = !config.registrationOpen;
    return;
  }
  if (["/login", "/signup", "/reset"].includes(location.pathname)) {
    location.replace("/dashboard");
    return;
  }
  document.title = "Dashboard | HourBoostr Members";
  for (const link of nav.querySelectorAll("a")) {
    if (link.hash === (account ? "#account" : "#overview")) link.setAttribute("aria-current", "page");
    else link.removeAttribute("aria-current");
  }
  get("email").textContent = member.email;
  get("appearance-help").textContent = config.simulated
    ? "Changes are saved in this demo. They won’t appear in Steam."
    : "Steam decides which badges and titles your friends see.";
  get("connection").textContent = states[member.state] || "Unavailable";
  get("current-game").textContent = games.find(game => game.appId === member.game)?.name || "No game selected";
  get("estimated").textContent = Math.floor(member.estimatedSeconds / 60) + " min";
  get("play").textContent = member.requested ? "Pause idling" : "Start idling";
  get("play").disabled = busy || !member.game;
  get("setup").hidden = Boolean(member.game);
  get("link-state").textContent = config.simulated
    ? (member.linked ? "Demo account linked. No Steam activity is reported." : "Link a demo account to try the Steam workflow.")
    : (member.linked ? "One Steam account linked." : states[member.state] || "Steam is not linked.");
  get("link").hidden = member.linked;
  get("link").disabled = busy || member.state === "pairing";
  get("unlink").hidden = !member.linked && member.state !== "pairing";
  get("unlink").disabled = busy;
  get("qr").hidden = config.simulated || member.state !== "pairing" || get("qr").dataset.loaded !== "yes";
  get("game").hidden = !member.linked;
  if (installedGames !== JSON.stringify(games)) {
    installedGames = JSON.stringify(games);
    get("game-picker").replaceChildren(new Option("Choose an owned game", ""), ...games.map(game => new Option(game.name, game.appId)));
    get("game-picker").value = member.game || "";
  }
}
get("qr").addEventListener("load", () => { get("qr").dataset.loaded = "yes"; render(); });
async function refresh() {
  if (polling) return;
  polling = true;
  try {
    config ||= await api("/api/member/config");
    const previous = member;
    member = await api("/api/member/me");
    if (member?.id !== appearanceOwner && location.hash === "#account") {
      const form = get("appearance-form");
      form.hidden = true;
      if (member) {
        try {
          const appearance = await api("/api/member/appearance");
          if (appearance) {
            fillAppearance(appearance);
            appearanceOwner = member.id;
            form.hidden = false;
          }
        } catch (error) { get("appearance-result").textContent = error.message; }
      }
    }
    if (!member) appearanceOwner = undefined;
    if (member?.linked !== previous?.linked || (member?.linked && !games.length)) games = member?.linked ? await api("/api/member/games") || [] : [];
    if (!config.simulated && member?.state === "pairing" && location.hash === "#account") get("qr").src = "/api/member/steam/qr?t=" + Date.now();
    else { get("qr").removeAttribute("src"); delete get("qr").dataset.loaded; }
    render();
  } finally { polling = false; }
}
async function run(path, method, body) {
  if (busy) return;
  busy = true;
  render();
  try {
    notice("");
    await api(path, method, body);
    await refresh();
  } catch (error) { notice(error.message); }
  finally { busy = false; render(); }
}
get("play").addEventListener("click", () => run("/api/member/" + (member.requested ? "pause" : "start"), "POST"));
get("link").addEventListener("click", () => run("/api/member/steam/link", "POST"));
get("unlink").addEventListener("click", () => run("/api/member/steam/link", "DELETE"));
get("logout").addEventListener("click", () => run("/api/member/logout", "POST"));
get("game").addEventListener("submit", event => {
  event.preventDefault();
  const appId = Number(get("game-picker").value);
  if (appId) run("/api/member/game", "PUT", { appId });
});
function fillAppearance(settings) {
  const form = get("appearance-form");
  for (const name of ["status", "badge", "customText"]) form.elements[name].value = settings[name];
  showAppearance();
}
function showAppearance() {
  const { status, badge, customText } = get("appearance-form").elements;
  get("appearance-preview-line").textContent = status.value === "invisible" ? "Appears offline"
    : status.selectedOptions[0].textContent + (badge.value === "normal" ? "" : " · " + badge.selectedOptions[0].textContent);
  get("appearance-preview-text").textContent = status.value === "invisible" ? "" : customText.value.trim();
}
get("appearance-form").addEventListener("input", showAppearance);
get("appearance-form").addEventListener("submit", async event => {
  event.preventDefault();
  if (savingAppearance) return;
  savingAppearance = true;
  const form = event.currentTarget;
  const button = form.querySelector("button[type=submit]");
  const result = get("appearance-result");
  button.disabled = true;
  try {
    const saved = await api("/api/member/appearance", "PUT", Object.fromEntries(new FormData(form)));
    fillAppearance(saved);
    result.textContent = "Appearance saved.";
  } catch (error) { result.textContent = error.message; }
  finally { button.disabled = false; savingAppearance = false; }
});

const modeText = {
  login: ["Welcome back", "Sign in to your HourBoostr dashboard.", "Log in", "New to HourBoostr?", "Create an account", "/signup"],
  register: ["Create your account", "Try the member dashboard free with one demo account and one game.", "Create account", "Already have an account?", "Log in", "/login"],
  recover: ["Reset your password", "Enter the email and recovery code you saved when you registered.", "Reset password", "Remember your password?", "Log in", "/login"]
};
const [heading, intro, action, prompt, linkText, linkPath] = modeText[authMode];
get("auth-title").textContent = heading;
get("auth-intro").textContent = intro;
get("auth-submit").textContent = action;
get("switch-copy").textContent = prompt;
get("switch-link").textContent = linkText;
get("switch-link").href = linkPath;
if (location.pathname !== "/") document.title = heading + " | HourBoostr Members";
const passwordField = get("auth-password");
if (authMode !== "login") {
  passwordField.autocomplete = "new-password";
  passwordField.minLength = 15;
  passwordField.setAttribute("aria-describedby", "password-help");
  get("password-help").hidden = false;
  get("forgot").hidden = true;
}
if (authMode === "recover") {
  get("recovery-field").hidden = false;
  get("auth-recovery").required = true;
  get("password-label").textContent = "New password";
}
get("show-password").addEventListener("click", () => {
  const show = passwordField.type === "password";
  passwordField.type = show ? "text" : "password";
  get("show-password").textContent = show ? "Hide" : "Show";
  get("show-password").setAttribute("aria-label", show ? "Hide password" : "Show password");
});
get("password-auth").addEventListener("submit", async event => {
  event.preventDefault();
  if (authPending) return;
  authPending = true;
  const form = get("password-auth"), button = get("auth-submit"), errorText = get("auth-error");
  errorText.hidden = true;
  button.disabled = true;
  button.textContent = "Please wait…";
  form.setAttribute("aria-busy", "true");
  try {
    const result = await api("/auth/password/" + authMode, "POST", {
      email: get("auth-email").value, password: passwordField.value,
      recoveryCode: authMode === "recover" ? get("auth-recovery").value : null
    });
    form.reset();
    if (!result.recoveryCode) {
      location.replace("/dashboard");
      return;
    }
    recoveryCode = result.recoveryCode.match(/.{1,4}/g).join("-");
    get("recovery-value").textContent = recoveryCode;
    render();
    get("recovery-title").focus();
  } catch (error) {
    errorText.textContent = error.message;
    errorText.hidden = false;
  } finally {
    form.removeAttribute("aria-busy");
    button.disabled = false;
    button.textContent = action;
    authPending = false;
  }
});
get("copy-recovery").addEventListener("click", async () => {
  const result = get("copy-result");
  try {
    await navigator.clipboard.writeText(recoveryCode);
    result.textContent = "Recovery code copied.";
  } catch { result.textContent = "Select the code above and copy it manually."; }
});
get("recovery-done").addEventListener("click", () => location.replace("/dashboard"));
window.addEventListener("hashchange", () => { if (config) { render(); update(); } });
const loginError = new URLSearchParams(location.search).get("auth");
if (loginError) notice(loginError === "denied" ? "This Google account does not have access." : "Google sign-in could not be completed.");
async function update() {
  if (authPending || recoveryCode) return;
  try { await refresh(); } catch (error) { get("loading").hidden = true; notice(error.message); }
}
document.addEventListener("visibilitychange", () => { if (!document.hidden) update(); });
update();
setInterval(() => { if (!document.hidden && !busy) update(); }, 5000);
