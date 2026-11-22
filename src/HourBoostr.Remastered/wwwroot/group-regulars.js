"use strict";

window.HBRGroupRegulars = (() => {
  const card = `<section class="panel form-panel" id="group-regulars" aria-labelledby="regulars-title">
    <div class="panel-title"><div><h2 id="regulars-title">Group Regulars</h2>
    <p>Review friend suggestions from Steam groups you choose.</p></div>
    <button type="button" class="button" data-regular-refresh>Refresh</button></div>
    <p role="status" data-regular-status>Loading joined groups…</p>
    <div data-regular-content></div>
  </section>`;

  function mount(section, accountId) {
    const path = `/api/accounts/${encodeURIComponent(accountId)}/group-regulars`;
    const status = section.querySelector("[data-regular-status]");
    const content = section.querySelector("[data-regular-content]");
    const refresh = section.querySelector("[data-regular-refresh]");

    function render(data) {
      const nameByGroup = new Map(data.groups.map(group => [group.id, group.name]));
      const enabled = new Set(data.groupIds);
      const choices = data.groups.map(group => `<label class="check-label field">
        <input type="checkbox" name="group" value="${escapeHtml(group.id)}" ${enabled.has(group.id) ? "checked" : ""}>
        ${escapeHtml(group.name)}</label>`).join("");
      const suggestions = data.suggestions.map(person => `<li class="form-section">
        <strong>${escapeHtml(person.name || `Steam user ${person.steamId}`)}</strong>
        <p class="hint">${escapeHtml(nameByGroup.get(person.groupId))} · Active on ${person.activeDays} days · Asked to connect</p>
        <div class="button-row"><button type="button" class="button primary" data-regular-add="${escapeHtml(person.steamId)}">Add friend</button>
        <button type="button" class="button" data-regular-dismiss="${escapeHtml(person.steamId)}">Dismiss</button></div></li>`).join("");
      content.innerHTML = `<p>Members qualify after chatting on three separate UTC dates within 14 days in the same selected group and asking to connect.</p>
        <p class="hint">To opt in, they can type <code>${escapeHtml(data.consentCommand)}</code> in that group. HourBoostr stores dates and the opt-in signal, without keeping chat text.</p>
        ${suggestions ? `<ul class="stack">${suggestions}</ul>` : '<p class="muted">No eligible suggestions yet. Only new group activity counts.</p>'}
        <form data-regular-settings>
          <fieldset class="form-section"><legend>Groups to watch (up to five)</legend>
            ${choices || '<p class="hint">Steam returned no joined chat groups.</p>'}
          </fieldset>
          <div class="button-row"><button type="submit" class="button primary">Save groups</button>
          <button type="button" class="button" data-regular-mute ${enabled.size ? "" : "disabled"}>Mute suggestions</button></div>
        </form>`;
      status.textContent = enabled.size ? `Watching ${enabled.size} group${enabled.size === 1 ? "" : "s"}.` : "Suggestions are off.";
      for (const button of content.querySelectorAll("[data-regular-add]")) {
        const person = data.suggestions.find(item => item.steamId === button.dataset.regularAdd);
        button.addEventListener("click", () => dialog(
          "Send a Steam friend request?",
          `<form><p>Send one request to <strong>${escapeHtml(person.name || person.steamId)}</strong>.</p>
          <p class="hint">You can send one request through Group Regulars every 24 hours for this account.</p>
          <p class="error" id="dialog-error" role="alert"></p><button class="button primary" type="submit">Send request</button></form>`,
          async () => {
            await api(path, "POST", { action: "add", steamId: person.steamId });
            closeDialog();
            toast("Friend request submitted to Steam.");
            await load();
          },
        ));
      }
    }

    async function load(method = "GET", body) {
      refresh.disabled = true;
      for (const input of content.querySelectorAll("input,button")) input.disabled = true;
      try {
        if (method !== "GET") await api(path, method, body);
        const data = await api(path);
        if (section.isConnected) render(data);
      } catch (error) {
        status.textContent = error.message;
        for (const input of content.querySelectorAll("input,button")) input.disabled = false;
      } finally {
        refresh.disabled = false;
      }
    }

    refresh.addEventListener("click", () => load());
    content.addEventListener("submit", event => {
      event.preventDefault();
      const groupIds = new FormData(event.target).getAll("group");
      if (groupIds.length > 5) { status.textContent = "Choose up to five groups."; return; }
      void load("PUT", { groupIds });
    });
    content.addEventListener("click", event => {
      const button = event.target.closest("button");
      if (button?.hasAttribute("data-regular-mute")) void load("PUT", { groupIds: [] });
      if (button?.dataset.regularDismiss) void load("POST", { action: "dismiss", steamId: button.dataset.regularDismiss });
    });
    // The dashboard can render other account controls without querying Steam group data.
    status.textContent = "Select Refresh to load joined groups and suggestions.";
  }

  return { card, mount };
})();
