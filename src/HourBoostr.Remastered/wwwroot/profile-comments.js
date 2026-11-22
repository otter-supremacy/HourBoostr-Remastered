"use strict";

window.HBRProfileComments = (() => {
  const card = `<section class="panel form-panel" id="profile-comments" aria-labelledby="profile-comments-title">
    <div class="panel-title"><div><h2 id="profile-comments-title">Profile comment inbox</h2>
    <p>Check for new Steam comments every 30 seconds while signed in.</p></div></div>
    <p class="hint">HourBoostr blocks links before sending eligible comment text to Groq.
    Copy a screened comment and open the author’s Steam profile to post it yourself.</p>
    <p role="status" data-comment-status>Loading inbox…</p><div data-comment-content></div>
  </section>`;

  function mount(section, accountId) {
    const path = `/api/accounts/${encodeURIComponent(accountId)}/profile-comments`;
    const status = section.querySelector("[data-comment-status]");
    const content = section.querySelector("[data-comment-content]");
    let busy = false;

    function render(data) {
      const checked = data.checkedAt ? new Date(data.checkedAt).toLocaleString() : "Not checked yet";
      status.textContent = data.enabled
        ? `${data.status} · ${checked} · ${data.blocked} blocked`
        : "Monitoring is off. Older comments aren’t imported when you turn it on.";
      const items = data.items.map(item => {
        const id = escapeHtml(item.id);
        const author = String(item.author);
        const url = /^\d{17,20}$/.test(author) ? `https://steamcommunity.com/profiles/${author}/` : "";
        return `<li class="form-section"><p><strong>${item.verdict === "safe" ? "Passed screening" : "Needs review"}</strong>
        · ${escapeHtml(new Date(item.at).toLocaleString())}</p>
        <label for="profile-comment-${id}">Comment text</label>
        <textarea id="profile-comment-${id}" rows="3" readonly></textarea>
        <div class="button-row"><button type="button" class="button" data-comment-copy="${id}">Copy exact text</button>
        ${url ? `<a class="button" href="${url}" rel="noreferrer">Open Steam profile</a>` : ""}
        <button type="button" class="button" data-comment-dismiss="${id}">Dismiss</button></div></li>`;
      }).join("");
      content.innerHTML = `<div class="button-row">
        <button type="button" class="button primary" data-comment-toggle="${data.enabled ? "off" : "on"}">${data.enabled ? "Pause and clear inbox" : "Enable monitoring"}</button>
        <button type="button" class="button" data-comment-refresh>Refresh inbox</button></div>
        ${items ? `<ul class="stack">${items}</ul>` : '<p class="muted">No comments awaiting review.</p>'}`;
      for (const item of data.items) {
        const text = section.ownerDocument.getElementById(`profile-comment-${item.id}`);
        if (text) text.value = item.text;
      }
    }

    async function load(method = "GET", body) {
      if (busy || !section.isConnected) return;
      busy = true;
      for (const button of content.querySelectorAll("button")) button.disabled = true;
      try {
        if (method !== "GET") await api(path, method, body);
        const data = await api(path);
        if (section.isConnected) render(data);
      } catch (error) {
        status.textContent = error.message;
        if (!content.children.length) content.innerHTML = '<button type="button" class="button" data-comment-refresh>Retry</button>';
      } finally {
        busy = false;
        for (const button of content.querySelectorAll("button")) button.disabled = false;
      }
    }

    content.addEventListener("click", async event => {
      const button = event.target.closest("button");
      if (!button || busy) return;
      if (button.hasAttribute("data-comment-refresh")) return load();
      if (button.dataset.commentToggle) return load("PUT", { enabled: button.dataset.commentToggle === "on" });
      if (button.dataset.commentDismiss) return load("POST", { id: button.dataset.commentDismiss });
      if (button.dataset.commentCopy) {
        const text = section.ownerDocument.getElementById(`profile-comment-${button.dataset.commentCopy}`);
        if (!text) return;
        try {
          await navigator.clipboard.writeText(text.value);
          toast("Comment copied.");
        } catch {
          text.focus();
          text.select();
          status.textContent = "Text selected. Use your device’s Copy command.";
        }
      }
    });
    void load();
  }

  return { card, mount };
})();
