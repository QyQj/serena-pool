"use strict";
const byId = id => document.getElementById(id);
const rows = byId("rows"), detail = byId("detail"), connection = byId("connection"), notice = byId("notice");
const messages = __DASHBOARD_MESSAGES__;
const text = (key, ...values) => messages[key].replace(/\{(\d+)\}/g, (_, index) => String(values[index]));
const locale = document.documentElement.lang;
const clockFormat = new Intl.DateTimeFormat(locale, {timeStyle: "medium", hour12: false});
const timestampFormat = new Intl.DateTimeFormat(locale, {dateStyle: "medium", timeStyle: "medium"});
const durationFormat = new Intl.NumberFormat(locale, {style: "unit", unit: "millisecond", maximumFractionDigits: 0});
const clock = value => clockFormat.format(new Date(value));
const states = {starting: text("DashboardStarting"), active: text("DashboardActive"), idle: text("DashboardIdle"), failed: text("DashboardWorkerFailed")};
const callStates = {
  succeeded: text("DashboardCallSucceeded"), failed: text("DashboardCallFailed"),
  canceled: text("DashboardCallCanceled"), unknown: text("DashboardCallUnknown"),
  unverified: text("DashboardCallUnverified")
};
let workers = [], selected = null, syncing = false, version = "", activityVersion = "";

function el(tag, text, className) {
  const node = document.createElement(tag);
  if (text !== undefined) node.textContent = text;
  if (className) node.className = className;
  return node;
}

function action(label, key, fn) {
  const button = el("button", label);
  button.type = "button";
  button.dataset.key = key;
  button.addEventListener("click", fn);
  return button;
}

function tableCell(label, item) {
  const cell = el("td");
  cell.dataset.label = label;
  cell.append(item);
  return cell;
}

async function api(path, method = "GET") {
  const res = await fetch(path, {method, cache: "no-store"});
  if (res.status === 409) throw new Error(text("DashboardConflict"));
  if (!res.ok) throw new Error(text("DashboardHttpError", res.status));
  return res;
}

function render() {
  const focused = document.activeElement?.dataset.key;
  const fragment = document.createDocumentFragment();
  for (const w of workers) {
    const row = el("tr");
    if (w.number === selected) row.className = "selected";
    const name = el("th", w.workdir.split(/[\\/]/).filter(Boolean).at(-1) ?? w.workdir);
    name.scope = "row";
    name.append(el("span", w.workdir, "path"));
    row.append(name);
    for (const [label, item] of [
      [text("DashboardStatus"), el("span", states[w.state], "tag " + w.state)],
      [text("DashboardLeases"), el("span", w.leases.length)],
      [text("DashboardQueue"), el("span", Number(w.running) + " / " + w.queued, "mono")],
      [text("DashboardActions"), action(text("DashboardView"), "view-" + w.number, () => { selected = w.number; render(); byId("detail-title").focus(); })]
    ]) {
      row.append(tableCell(label, item));
    }
    fragment.append(row);
  }
  rows.replaceChildren(fragment);
  byId("summary").textContent = text("DashboardSummary", workers.length, workers.reduce((n, w) => n + w.leases.length, 0));
  byId("empty").hidden = workers.length !== 0;
  const w = workers.find(item => item.number === selected);
  detail.hidden = !w;
  detail.replaceChildren();
  if (w) {
    const header = el("div", undefined, "detail-head"), title = el("h2", text("DashboardWorker", w.number));
    title.id = "detail-title";
    title.tabIndex = -1;
    header.append(title, action(text("DashboardClose"), "close", () => {
      selected = null;
      render();
      document.querySelector('[data-key="view-' + w.number + '"]')?.focus();
    }));
    const info = el("dl");
    for (const [label, value] of [[text("DashboardWorkdir"), w.workdir], [text("DashboardPid"), w.pid ?? "—"], [text("DashboardStarted"), timestampFormat.format(new Date(w.started))],
      [text("DashboardRecycleAt"), w.recycleAt && clock(w.recycleAt)], [text("DashboardLastError"), w.lastError]]) {
      if (value) info.append(el("dt", label), el("dd", value));
    }
    detail.append(header, info);
    for (const lease of w.leases) {
      const item = el("div", undefined, "lease");
      item.append(el("strong", text("DashboardLease", lease.number)), el("span", text("DashboardAcquired", clock(lease.acquired))),
        el("span", text("DashboardLastActivity", clock(lease.lastActivity))), action(text("DashboardRelease"), "release-" + lease.number,
          () => change("/api/leases/" + lease.number, text("DashboardConfirmRelease", lease.number))));
      detail.append(item);
    }
    if (!w.running && w.queued === 0 && w.leases.length === 0)
      detail.append(action(text("DashboardRecycle"), "recycle-" + w.number, () => change("/api/workers/" + w.number, text("DashboardConfirmRecycle", w.number))));
  } else selected = null;
  if (focused) document.querySelector('[data-key="' + CSS.escape(focused) + '"]')?.focus({preventScroll: true});
}

function renderActivity({capacity, calls}) {
  const logRows = document.createDocumentFragment(), toolRows = document.createDocumentFragment();
  const totals = new Map();
  let observed = 0;
  for (const call of calls) {
    const row = el("tr"), heading = el("th"), time = el("time", timestampFormat.format(new Date(call.at)));
    time.dateTime = call.at;
    heading.scope = "row";
    heading.append(time);
    const tools = Object.entries(call.tools ?? {});
    if (call.tools !== null) observed++;
    for (const [name, count] of tools) totals.set(name, (totals.get(name) ?? 0) + count);
    row.append(heading,
      tableCell(text("DashboardWorkdir"), el("span", call.workdir, "path")),
      tableCell(text("DashboardCallLease"), el("span", call.lease, "mono")),
      tableCell(text("DashboardStatus"), el("span", callStates[call.status], "tag " + call.status)),
      tableCell(text("DashboardCallDuration"), el("span", durationFormat.format(call.elapsedMs), "elapsed")),
      tableCell(text("DashboardReplTools"), el("span", tools.map(([name, count]) => name + " ×" + count).join(", ") || "—", "mono")));
    logRows.append(row);
  }
  for (const [name, count] of [...totals].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))) {
    const row = el("tr"), heading = el("th", name, "mono");
    heading.scope = "row";
    row.append(heading, tableCell(text("DashboardToolInvocations"), String(count)));
    toolRows.append(row);
  }
  byId("call-rows").replaceChildren(logRows);
  byId("usage-rows").replaceChildren(toolRows);
  byId("call-summary").textContent = text("DashboardCallSummary", calls.length, capacity);
  byId("usage-summary").textContent = text("DashboardUsageSummary", observed, calls.length);
  byId("calls-empty").hidden = calls.length > 0;
  byId("usage-empty").hidden = totals.size > 0;
}

async function refresh() {
  if (syncing) return;
  syncing = true;
  try {
    const [data, activity] = await Promise.all(["/api/workers", "/api/calls"].map(async path => (await api(path)).json()));
    const next = JSON.stringify(data);
    if (next !== version) { workers = data; version = next; render(); }
    const nextActivity = JSON.stringify(activity);
    if (nextActivity !== activityVersion) { renderActivity(activity); activityVersion = nextActivity; }
    if (connection.textContent !== text("DashboardConnected")) notice.textContent = "";
    connection.textContent = text("DashboardConnected");
    byId("updated").textContent = clock(Date.now());
  } catch (err) {
    connection.textContent = text("DashboardFailed");
    notice.textContent = err.message;
  } finally { syncing = false; }
}

async function change(path, message) {
  if (!window.confirm(message)) return;
  try {
    await api(path, "DELETE");
    notice.textContent = "";
    await refresh();
  } catch (err) { notice.textContent = err.message; }
}

byId("refresh").addEventListener("click", refresh);
document.addEventListener("visibilitychange", () => { if (!document.hidden) refresh(); });
setInterval(() => { if (!document.hidden) refresh(); }, 2500);
refresh();
