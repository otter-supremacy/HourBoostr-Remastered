"use strict";
(() => {
  const form = document.getElementById("chat-form");
  const field = name => form.elements.namedItem(name);
  const pane = document.getElementById("chat-settings");
  const result = document.getElementById("chat-result");
  const preview = document.getElementById("chat-preview-list");
  const checks = ["privateReplies", "quietHours", "groupReplies", "autoAccept", "welcomeEnabled", "aiPrivate", "aiGroup"];
  const texts = ["privateMessage", "quietStart", "quietEnd", "timeZone", "welcomeMessage", "gptPrompt", "qwenPrompt"];
  const numbers = ["privateCooldownMinutes", "groupCooldownSeconds"];
  let chosenGroups = [];
  let memberId = null, linked = false, loaded = false, loading = false, saving = false, generation = 0;

  function fill(settings) {
    checks.forEach(name => { field(name).checked = settings[name]; });
    [...texts, ...numbers].forEach(name => { field(name).value = settings[name]; });
    field("excludedFriends").value = settings.excludedFriends.join(", ");
    chosenGroups = settings.regularGroups.slice();
    settings.groupMessages.forEach((value, index) => { field("group" + (index + 1)).value = value; });
    welcomeState();
  }

  function collect() {
    const data = Object.fromEntries(checks.map(name => [name, field(name).checked]));
    texts.forEach(name => { data[name] = field(name).value.trim(); });
    numbers.forEach(name => { data[name] = Number(field(name).value); });
    data.excludedFriends = field("excludedFriends").value.split(/[\s,]+/).filter(Boolean);
    data.regularGroups = chosenGroups;
    data.groupMessages = [1, 2, 3].map(index => field("group" + index).value.trim());
    if (field("aiPrivate").disabled) data.aiPrivate = false;
    if (field("aiGroup").disabled) data.aiGroup = false;
    return data;
  }

  function welcomeState() {
    field("welcomeEnabled").disabled = !field("autoAccept").checked;
    if (field("welcomeEnabled").disabled) field("welcomeEnabled").checked = false;
    field("welcomeMessage").disabled = !field("welcomeEnabled").checked;
  }

  function showGroq(data) {
    for (const name of ["aiPrivate", "aiGroup"]) field(name).disabled = !data.aiConfigured;
    document.getElementById("groq-status").textContent =
      "GPT " + (data.aiConfigured ? "key saved" : "key required") + " (" + data.gptUsed +
      "/500) · Qwen " + (data.qwenConfigured ? "key saved" : "optional") +
      " (" + data.qwenUsed + "/500) · UTC daily usage";
  }

  async function changeKey(remove) {
    const status = document.getElementById("groq-status");
    const model = document.getElementById("groq-model").value;
    const input = document.getElementById("groq-key");
    const buttons = ["save-groq-key", "remove-groq-key"].map(id => document.getElementById(id));
    const stamp = generation;
    buttons.forEach(button => { button.disabled = true; });
    try {
      await api("/api/member/chat/key/" + model, remove ? "DELETE" : "PUT", remove ? undefined : { key: input.value.trim() });
      if (stamp !== generation) return;
      input.value = "";
      const data = await api("/api/member/chat");
      if (stamp === generation && data) showGroq(data);
    } catch (error) { if (stamp === generation) status.textContent = error.message; }
    finally { buttons.forEach(button => { button.disabled = false; }); }
  }
  document.getElementById("save-groq-key").addEventListener("click", () => changeKey(false));
  document.getElementById("remove-groq-key").addEventListener("click", () => changeKey(true));

  document.getElementById("groq-preview").addEventListener("click", async event => {
    const button = event.currentTarget;
    const output = document.getElementById("groq-preview-result");
    const stamp = generation;
    button.disabled = true;
    output.textContent = "Generating preview...";
    try {
      const data = await api("/api/member/chat/preview", "POST", { message: document.getElementById("groq-sample").value });
      if (stamp !== generation) return;
      output.textContent = data?.reply || "";
      const status = await api("/api/member/chat");
      if (stamp === generation && status) showGroq(status);
    } catch (error) { if (stamp === generation) output.textContent = error.message; }
    finally { button.disabled = false; }
  });

  async function load() {
    if (loaded || loading) return;
    loading = true;
    const stamp = generation;
    try {
      const data = await api("/api/member/chat");
      if (!data || stamp !== generation) return;
      fill(data.settings);
      showGroq(data);
      loaded = true;
      form.hidden = false;
      if (document.getElementById("regulars-panel").open) refreshRegulars();
    } catch (error) { document.getElementById("chat-mode").textContent = error.message; }
    finally { if (stamp === generation) loading = false; }
  }

  document.addEventListener("hbr-member-change", ({ detail }) => {
    const nextId = detail.member?.id || null;
    const nextLinked = Boolean(detail.member?.linked);
    if (nextId !== memberId || linked !== nextLinked) {
      generation++;
      loaded = loading = false;
      form.hidden = true;
      preview.hidden = true;
      form.reset();
      result.textContent = "";
      document.getElementById("groq-preview-result").textContent = "";
      chosenGroups = [];
      document.getElementById("regulars-groups").replaceChildren();
      document.getElementById("regulars-people").replaceChildren();
    }
    memberId = nextId;
    linked = nextLinked;
    pane.hidden = !memberId;
    document.getElementById("chat-mode").textContent = detail.config?.simulated
      ? "Steam activity is simulated. You can save and preview settings here."
      : "Chat automation runs while your Steam worker is connected. Start idling from Overview to connect it.";
    if (memberId && detail.account) load();
  });

  const regularsPanel = document.getElementById("regulars-panel");
  async function refreshRegulars() {
    const stamp = generation;
    const groupsNode = document.getElementById("regulars-groups");
    const peopleNode = document.getElementById("regulars-people");
    try {
      const data = await api("/api/member/chat/regulars");
      if (!data || stamp !== generation) return;
      document.getElementById("group-opt-in").textContent = data.optIn ? "Opt-in command: " + data.optIn : "Connect Steam to see your live opt-in command.";
      groupsNode.replaceChildren();
      const available = new Map(data.groups.map(group => [group.id, group.name]));
      for (const id of chosenGroups) if (!available.has(id)) available.set(id, "Saved group " + id);
      if (!available.size) groupsNode.textContent = "Start a Steam session, then refresh to see joined groups.";
      for (const [id, name] of available) {
        const label = document.createElement("label");
        const checkbox = document.createElement("input");
        checkbox.type = "checkbox";
        checkbox.checked = chosenGroups.includes(id);
        checkbox.addEventListener("change", () => {
          if (!checkbox.checked) chosenGroups = chosenGroups.filter(value => value !== id);
          else if (chosenGroups.length < 10) chosenGroups.push(id);
          else { checkbox.checked = false; result.textContent = "Choose up to 10 groups."; }
        });
        label.append(checkbox, document.createTextNode(name));
        groupsNode.append(label);
      }
      peopleNode.replaceChildren();
      if (!data.candidates.length) peopleNode.textContent = "No eligible suggestions.";
      for (const candidate of data.candidates) {
        const row = document.createElement("p");
        const approve = document.createElement("button");
        approve.type = "button";
        approve.textContent = "Send friend request";
        approve.disabled = !data.connected;
        approve.addEventListener("click", async () => {
          if (!window.confirm("Send a Steam friend request to " + candidate.steamId + "?")) return;
          approve.disabled = true;
          try {
            const response = await api("/api/member/chat/regulars/approve", "POST", { steamId: candidate.steamId });
            result.textContent = response ? "Friend request submitted." : "Sign in again.";
            await refreshRegulars();
          } catch (error) { result.textContent = error.message; approve.disabled = false; }
        });
        row.append(document.createTextNode(candidate.steamId + " Ã‚Â· " + candidate.activeDays + " active days "), approve);
        peopleNode.append(row);
      }
    } catch (error) { if (stamp === generation) peopleNode.textContent = error.message; }
  }
  regularsPanel.addEventListener("toggle", () => { if (regularsPanel.open && loaded) refreshRegulars(); });
  document.getElementById("regulars-refresh").addEventListener("click", refreshRegulars);

  field("autoAccept").addEventListener("change", welcomeState);
  field("welcomeEnabled").addEventListener("change", welcomeState);
  form.addEventListener("submit", async event => {
    event.preventDefault();
    if (saving) return;
    saving = true;
    const stamp = generation;
    form.querySelector('[type="submit"]').disabled = true;
    try {
      const saved = await api("/api/member/chat", "PUT", collect());
      if (stamp === generation) result.textContent = saved ? "Chat settings saved." : "Sign in again to save changes.";
    } catch (error) { if (stamp === generation) result.textContent = error.message; }
    finally { saving = false; form.querySelector('[type="submit"]').disabled = false; }
  });
  document.getElementById("chat-preview").addEventListener("click", () => {
    const settings = collect();
    const rows = [];
    if (settings.privateReplies) rows.push(["Private reply", settings.privateMessage]);
    if (settings.groupReplies) settings.groupMessages.forEach((message, index) => rows.push(["Group reply " + (index + 1), message]));
    if (settings.autoAccept && settings.welcomeEnabled) rows.push(["Welcome after acceptance", settings.welcomeMessage]);
    preview.replaceChildren();
    const intro = document.createElement("p");
    intro.textContent = rows.length ? "Preview only. No messages were sent." : "Enable a reply or welcome to preview it.";
    preview.append(intro);
    for (const [label, message] of rows) {
      const item = document.createElement("p");
      item.textContent = label + " Â· " + (message || "Enter a message");
      preview.append(item);
    }
    preview.hidden = false;
  });
})();
