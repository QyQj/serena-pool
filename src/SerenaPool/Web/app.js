"use strict";
const byId = id => document.getElementById(id);
const login = byId("login"), dashboard = byId("dashboard"), rows = byId("rows"), detail = byId("detail");
const states = {starting: "启动中", active: "使用中", idle: "空闲", failed: "已失效"};
const clock = iso => new Date(iso).toLocaleTimeString("zh-CN", {hour12: false});
let token = "", workers = [], selected = null, syncing = false, version = "";

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

function disconnect() {
  token = "";
  workers = [];
  version = "";
  selected = null;
  dashboard.hidden = true;
  login.hidden = false;
  rows.replaceChildren();
  detail.replaceChildren();
  byId("connection").textContent = "本机管理台";
  byId("notice").textContent = "";
  byId("token").focus();
}

async function api(path, method = "GET") {
  const res = await fetch(path, {method, headers: {Authorization: "Bearer " + token}, cache: "no-store"});
  if (res.status === 401) throw new Error("访问令牌无效，请重新连接。");
  if (res.status === 409) throw new Error("实例正在使用，无法回收。");
  if (!res.ok) throw new Error("请求失败（HTTP " + res.status + "）");
  return res;
}

function render() {
  const focused = document.activeElement.dataset.key;
  const fragment = document.createDocumentFragment();
  for (const w of workers) {
    const row = el("tr");
    if (w.number === selected) row.className = "selected";
    const name = el("th", w.workdir.split(/[\\/]/).filter(Boolean).at(-1) ?? w.workdir);
    name.scope = "row";
    name.append(el("span", w.workdir, "path"));
    row.append(name);
    for (const [label, item] of [
      ["状态", el("span", states[w.state], "tag " + w.state)],
      ["领用", el("span", w.leases.length)],
      ["执行 / 排队", el("span", Number(w.running) + " / " + w.queued, "mono")],
      ["操作", action("查看详情", "view-" + w.number, () => { selected = w.number; render(); byId("detail-title").focus(); })]
    ]) {
      const cell = el("td");
      cell.dataset.label = label;
      cell.append(item);
      row.append(cell);
    }
    fragment.append(row);
  }
  rows.replaceChildren(fragment);
  byId("summary").textContent = workers.length + " 个实例 · " + workers.reduce((n, w) => n + w.leases.length, 0) + " 个领用";
  byId("empty").hidden = workers.length !== 0;
  const w = workers.find(item => item.number === selected);
  detail.hidden = !w;
  detail.replaceChildren();
  if (w) {
    const header = el("div", undefined, "detail-head"), title = el("h2", "实例 #" + w.number);
    title.id = "detail-title";
    title.tabIndex = -1;
    header.append(title, action("关闭详情", "close", () => {
      selected = null;
      render();
      document.querySelector('[data-key="view-' + w.number + '"]')?.focus();
    }));
    const info = el("dl");
    for (const [label, value] of [["工作目录", w.workdir], ["进程 PID", w.pid ?? "—"], ["启动时间", new Date(w.started).toLocaleString("zh-CN")],
      ["预计回收", w.recycleAt && clock(w.recycleAt)], ["最近错误", w.lastError]]) {
      if (value) info.append(el("dt", label), el("dd", value));
    }
    detail.append(header, info);
    for (const lease of w.leases) {
      const item = el("div", undefined, "lease");
      item.append(el("strong", "领用 #" + lease.number), el("span", "领用时间 " + clock(lease.acquired)),
        el("span", "最近活动 " + clock(lease.lastActivity)), action("释放", "release-" + lease.number,
          () => change("/api/leases/" + lease.number, "释放领用 #" + lease.number + "？排队调用将取消。")));
      detail.append(item);
    }
    if (!w.running && w.queued === 0 && w.leases.length === 0)
      detail.append(action("立即回收", "recycle-" + w.number, () => change("/api/workers/" + w.number, "回收实例 #" + w.number + "？")));
  } else selected = null;
  if (focused) document.querySelector('[data-key="' + CSS.escape(focused) + '"]')?.focus({preventScroll: true});
}

async function refresh() {
  if (!token || syncing) return;
  syncing = true;
  const key = token;
  try {
    const data = await (await api("/api/workers")).json();
    if (token !== key) return;
    const next = JSON.stringify(data);
    if (next !== version) { workers = data; version = next; render(); }
    login.hidden = true;
    dashboard.hidden = false;
    byId("token").value = "";
    byId("connection").textContent = "本机已连接";
    byId("updated").textContent = clock(Date.now());
    byId("notice").textContent = "";
  } catch (err) {
    if (token === key) {
      byId("connection").textContent = "连接失败";
      byId("notice").textContent = err.message;
      if (!login.hidden) token = "";
    }
  } finally { syncing = false; }
}

async function change(path, message) {
  if (!window.confirm(message)) return;
  try { await api(path, "DELETE"); await refresh(); }
  catch (err) { byId("notice").textContent = err.message; }
}

byId("login-form").addEventListener("submit", event => {
  event.preventDefault();
  if (syncing) return;
  token = byId("token").value.trim();
  if (token) refresh();
});
byId("logout").addEventListener("click", disconnect);
byId("refresh").addEventListener("click", refresh);
document.addEventListener("visibilitychange", () => { if (!document.hidden) refresh(); });
setInterval(() => { if (!document.hidden && !dashboard.hidden) refresh(); }, 2500);
