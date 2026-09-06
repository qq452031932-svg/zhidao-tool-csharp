"use strict";

const state = {
  csrf: "",
  candidate: null,
  releases: [],
  uploadOperation: "",
  publishOperation: "",
  rollbackRelease: "",
  rollbackOperations: new Map(),
  readOnly: false,
  busy: false,
  candidatePublishBlocked: false
};

const byId = (id) => document.getElementById(id);
const views = ["login-view", "password-view", "console-view"];
const errorMessages = {
  authentication_failed: "账号或密码不正确，或者登录已经失效。",
  password_rejected: "当前密码不正确，或新密码不符合要求。",
  request_rejected: "安全校验失败，请刷新页面后重试。",
  upload_too_large: "发布包超过允许大小。",
  invalid_zip_envelope: "ZIP 格式或文件结构不符合发布要求。",
  signature_mismatch: "发布签名与固定证书不匹配。",
  version_floor: "版本号不是递增，请生成更高版本。",
  release_validation_failed: "发布包文件、哈希或引用关系校验失败。",
  insufficient_storage: "服务器空间不足，暂时无法接收新版本。",
  candidate_expired: "候选版本已经过期，请重新上传。",
  operation_conflict: "已有发布或回滚正在进行，请稍后刷新。",
  publish_unavailable: "发布服务正在恢复，当前只能查看数据。",
  upload_unavailable: "上传服务暂时不可用。",
  history_unavailable: "版本历史暂时不可用。",
  upload_in_progress: "上传仍在处理中，请稍后刷新。",
  confirmation_required: "请完成确认后再继续。",
  operation_failed: "操作失败，请记录操作编号并稍后重试。"
};

function showView(id) {
  for (const view of views) byId(view).hidden = view !== id;
  byId("logout-button").hidden = id !== "console-view";
}

function setLoading(active) {
  byId("loading-state").hidden = !active;
  byId("connection-state").textContent = active ? "正在连接" : "已连接";
}

function showPageError(message) {
  byId("error-message").textContent = message;
  byId("error-state").hidden = false;
}

function clearPageError() { byId("error-state").hidden = true; }

function messageFor(error, fallback) {
  if (error && error.code && errorMessages[error.code]) return errorMessages[error.code];
  return fallback || "操作失败，请稍后重试。";
}

function showToast(message) {
  const toast = byId("toast");
  toast.textContent = message;
  toast.hidden = false;
  window.setTimeout(() => { toast.hidden = true; }, 4200);
}

function setReadOnly(active, error) {
  state.readOnly = active;
  byId("readonly-state").hidden = !active;
  for (const id of ["release-file", "upload-button", "publish-button"]) byId(id).disabled = active || byId(id).disabled;
  for (const button of document.querySelectorAll("#history-body button")) button.disabled = active;
  if (active && error) showToast(messageFor(error, "后台处于只读保护状态。"));
  updateActions();
}

async function readJSON(response) {
  const contentType = response.headers.get("Content-Type") || "";
  if (!contentType.startsWith("application/json")) return {};
  try { return await response.json(); } catch (_) { return {}; }
}

async function api(path, options = {}) {
  const headers = new Headers(options.headers || {});
  if (options.body && !(options.body instanceof Blob)) headers.set("Content-Type", "application/json");
  if (options.method && options.method !== "GET" && state.csrf) headers.set("X-CSRF-Token", state.csrf);
  const response = await fetch(path, { ...options, headers, credentials: "same-origin", cache: "no-store" });
  const body = await readJSON(response);
  if (!response.ok) {
    const error = new Error(body.message || response.statusText);
    error.code = body.code || "operation_failed";
    error.status = response.status;
    error.operationId = body.operation_id || "";
    if (response.status === 401 && !options.suppressSessionExpiry) await expiredSession();
    if (response.status === 503 && (error.code === "publish_unavailable" || error.code === "upload_unavailable")) setReadOnly(true, error);
    throw error;
  }
  return body;
}

async function bootstrapLogin() {
  const result = await api("/admin/api/session/bootstrap", { method: "GET" });
  return result.csrf_token || "";
}

async function expiredSession() {
  state.csrf = "";
  state.candidate = null;
  showView("login-view");
  byId("connection-state").textContent = "登录已失效";
  byId("login-error").textContent = "登录已失效，请重新登录。";
  byId("login-error").hidden = false;
}

async function initialize() {
  setLoading(true);
  clearPageError();
  try {
    const current = await api("/admin/api/session/current", { method: "GET", suppressSessionExpiry: true });
    state.csrf = current.csrf_token || "";
    if (current.must_change_password) {
      showView("password-view");
    } else {
      showView("console-view");
      await loadDashboard();
    }
  } catch (error) {
    if (error.status !== 401) {
      showPageError(messageFor(error, "无法连接发布后台。"));
    } else {
      showView("login-view");
    }
  } finally {
    setLoading(false);
  }
}

async function login(event) {
  event.preventDefault();
  const form = event.currentTarget;
  const button = form.querySelector("button[type='submit']");
  const errorBox = byId("login-error");
  button.disabled = true;
  errorBox.hidden = true;
  try {
    const preauthCSRF = await bootstrapLogin();
    const result = await api("/admin/api/session/login", {
      method: "POST",
      headers: { "X-CSRF-Token": preauthCSRF },
      body: JSON.stringify({ username: form.elements.username.value, password: form.elements.password.value })
    });
    state.csrf = result.csrf_token;
    if (result.must_change_password) {
      byId("current-password").value = form.elements.password.value;
      form.elements.password.value = "";
      showView("password-view");
      byId("new-password").focus();
    } else {
      form.reset();
      showView("console-view");
      await loadDashboard();
    }
  } catch (error) {
    errorBox.textContent = messageFor(error, "登录失败，请稍后重试。");
    errorBox.hidden = false;
    byId("login-password").focus();
  } finally {
    button.disabled = false;
  }
}

async function changePassword(event) {
  event.preventDefault();
  const form = event.currentTarget;
  const button = form.querySelector("button[type='submit']");
  const errorBox = byId("password-error");
  button.disabled = true;
  errorBox.hidden = true;
  try {
    const result = await api("/admin/api/session/password", {
      method: "POST",
      body: JSON.stringify({ current_password: form.elements.current_password.value, new_password: form.elements.new_password.value })
    });
    state.csrf = result.csrf_token;
    form.reset();
    byId("login-password").value = "";
    byId("current-password").value = "";
    byId("new-password").value = "";
    showView("console-view");
    await loadDashboard();
    showToast("密码已更新。以后请使用新密码登录。");
  } catch (error) {
    errorBox.textContent = messageFor(error, "密码修改失败。");
    errorBox.hidden = false;
  } finally {
    button.disabled = false;
  }
}

async function logout() {
  try {
    await api("/admin/api/session/logout", { method: "POST", body: "{}" });
  } catch (error) {
    if (error.status !== 401) {
      showPageError(messageFor(error, "退出失败，当前登录仍然有效，请重试。"));
      showToast("退出失败，未清除当前登录。 ");
      return;
    }
  }
  state.csrf = "";
  state.candidate = null;
  showView("login-view");
  byId("login-password").value = "";
  byId("login-password").focus();
}

function formatDate(value) {
  if (!value) return "—";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "—";
  return new Intl.DateTimeFormat("zh-CN", { dateStyle: "medium", timeStyle: "short" }).format(date);
}

function setCurrent(releases) {
  const current = releases.find((item) => item.state === "current");
  byId("empty-state").hidden = Boolean(current);
  byId("current-version").textContent = current ? `v${current.version}` : "—";
  byId("current-time").textContent = current ? formatDate(current.committed_at) : "等待首次发布";
  byId("current-notes").textContent = current && current.notes ? current.notes : "暂无更新说明";
  byId("current-assets").textContent = current && current.assets ? String(current.assets.length) : "0";
  byId("current-actor").textContent = current ? "管理员" : "—";
  byId("current-status").textContent = current ? "运行中" : "尚未发布";
  byId("current-status").className = current ? "status-pill success" : "status-pill";
}

function cell(text) {
  const value = document.createElement("td");
  value.textContent = text;
  return value;
}

function renderHistory() {
  const body = byId("history-body");
  body.replaceChildren();
  if (!state.releases.length) {
    const row = document.createElement("tr");
    const value = cell("暂无版本记录");
    value.colSpan = 6;
    value.className = "table-empty";
    row.append(value);
    body.append(row);
    return;
  }
  for (const item of state.releases.slice(0, 3)) {
    const row = document.createElement("tr");
    row.append(cell(`v${item.version}`));
    row.append(cell(item.state === "current" ? "当前版本" : "已保留"));
    row.append(cell(formatDate(item.committed_at)));
    row.append(cell(item.notes || "暂无说明"));
    const assets = document.createElement("td");
    const assetList = document.createElement("span");
    assetList.className = "asset-list";
    const values = Array.isArray(item.assets) ? item.assets : [];
    assetList.textContent = values.length ? `${values.length} 个文件 · ${formatBytes(values.reduce((sum, asset) => sum + (asset.size_bytes || 0), 0))}` : "无资产摘要";
    assets.append(assetList);
    row.append(assets);
    const action = document.createElement("td");
    if (item.state === "current") {
      action.textContent = "—";
    } else {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "button ghost";
      button.textContent = "回滚到此版本";
      button.disabled = state.readOnly;
      button.addEventListener("click", () => openRollback(item));
      action.append(button);
    }
    row.append(action);
    body.append(row);
  }
}

function formatBytes(bytes) {
  if (!Number.isFinite(bytes) || bytes <= 0) return "0 B";
  const units = ["B", "KB", "MB", "GB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit += 1; }
  return `${value.toFixed(unit ? 1 : 0)} ${units[unit]}`;
}

function resetChecks(status = "pending") {
  for (const item of byId("validation-list").children) {
    const description = item.querySelector("small");
    if (description && !item.dataset.defaultEvidence) item.dataset.defaultEvidence = description.textContent;
    if (description && item.dataset.defaultEvidence) description.textContent = item.dataset.defaultEvidence;
    item.classList.remove("passed", "failed");
    item.firstElementChild.textContent = status === "failed" ? "×" : "○";
    if (status === "failed") item.classList.add("failed");
  }
}

function renderGateEvidence(candidate) {
  const gates = candidate && candidate.report && candidate.report.gates ? candidate.report.gates : {};
  for (const item of byId("validation-list").children) {
    const gate = gates[item.dataset.check];
    const passed = Boolean(gate && gate.passed === true);
    const failed = candidate && (candidate.state === "failed" || (candidate.state === "validated" && !passed));
    item.classList.toggle("passed", passed);
    item.classList.toggle("failed", Boolean(failed));
    item.firstElementChild.textContent = passed ? "✓" : failed ? "×" : "○";
    const description = item.querySelector("small");
    if (description && gate && gate.evidence) description.textContent = gate.evidence;
  }
}

function renderCandidate(candidate) {
  state.candidate = candidate;
  if (!candidate) {
    byId("candidate-state").textContent = "等待上传";
    byId("candidate-state").className = "status-pill";
    byId("candidate-version").textContent = "—";
    byId("candidate-files").textContent = "—";
    byId("candidate-expiry").textContent = "—";
    byId("candidate-notes").textContent = "上传后显示签名内容";
    resetChecks();
    updateActions();
    return;
  }
  const report = candidate.report || {};
  const passed = candidate.state === "validated" && report.verified === true;
  byId("candidate-state").textContent = passed ? "全部通过" : candidate.state === "uploading" ? "校验中" : "校验失败";
  byId("candidate-state").className = passed ? "status-pill success" : candidate.state === "failed" ? "status-pill danger" : "status-pill";
  byId("candidate-version").textContent = report.version ? `v${report.version}` : "—";
  byId("candidate-files").textContent = Number.isInteger(report.file_count) ? String(report.file_count) : "—";
  byId("candidate-expiry").textContent = formatDate(candidate.expires_at);
  byId("candidate-notes").textContent = report.notes_markdown || report.notesMarkdown || "签名包未提供可显示的更新说明";
  renderGateEvidence(candidate);
  updateActions();
}

function updateActions() {
  const file = byId("release-file").files && byId("release-file").files[0];
  byId("release-file").disabled = state.readOnly || state.busy;
  byId("upload-button").disabled = !file || state.readOnly || state.busy;
  const canPublish = !state.candidatePublishBlocked && state.candidate && state.candidate.state === "validated" && state.candidate.report && state.candidate.report.verified;
  byId("publish-button").disabled = !canPublish || state.readOnly || state.busy;
}

async function loadDashboard() {
  clearPageError();
  setLoading(true);
  try {
    const [historyResult, candidateResult] = await Promise.allSettled([
      api("/admin/api/releases", { method: "GET" }),
      api("/admin/api/candidates/current", { method: "GET" })
    ]);
    if (historyResult.status === "rejected") throw historyResult.reason;
    state.releases = Array.isArray(historyResult.value.releases) ? historyResult.value.releases.slice(0, 3) : [];
	setReadOnly(historyResult.value.read_only === true);
    setCurrent(state.releases);
    renderHistory();
    if (candidateResult.status === "fulfilled") renderCandidate(candidateResult.value);
    else if (candidateResult.reason.status === 404) renderCandidate(null);
    else throw candidateResult.reason;
  } catch (error) {
    if (error.status !== 401) showPageError(messageFor(error, "读取发布状态失败。"));
  } finally {
    setLoading(false);
  }
}

function selectedFileChanged() {
  const file = byId("release-file").files[0];
  state.uploadOperation = "";
  state.publishOperation = "";
  state.candidatePublishBlocked = Boolean(file);
  if (file) renderCandidate(null);
  byId("selected-file").textContent = file ? `${file.name} · ${formatBytes(file.size)}` : "尚未选择文件";
  byId("upload-progress-wrap").hidden = true;
  updateActions();
}

async function restoreAuthoritativeCandidate() {
  state.candidatePublishBlocked = true;
  try {
    const authoritative = await api("/admin/api/candidates/current", { method: "GET" });
    renderCandidate(authoritative);
    byId("candidate-state").textContent = "旧候选已保留（不可发布）";
    byId("publish-button").disabled = true;
  } catch (error) {
    if (error.status === 404) renderCandidate(null);
  }
}

function uploadSelected() {
  const file = byId("release-file").files[0];
  if (!file || state.readOnly || state.busy) return;
  state.busy = true;
  state.uploadOperation = state.uploadOperation || crypto.randomUUID();
  updateActions();
  const wrap = byId("upload-progress-wrap");
  const progress = byId("upload-progress");
  wrap.hidden = false;
  progress.value = 0;
  byId("upload-progress-text").textContent = "上传进度 0%";
  const request = new XMLHttpRequest();
  request.open("POST", "/admin/api/uploads");
  request.withCredentials = true;
  request.setRequestHeader("Content-Type", "application/zip");
  request.setRequestHeader("X-CSRF-Token", state.csrf);
  request.setRequestHeader("Idempotency-Key", state.uploadOperation);
  request.upload.addEventListener("progress", (event) => {
    if (!event.lengthComputable) return;
    const percent = Math.min(100, Math.round((event.loaded / event.total) * 100));
    progress.value = percent;
    byId("upload-progress-text").textContent = `上传进度 ${percent}%`;
    byId("upload-size-text").textContent = `${formatBytes(event.loaded)} / ${formatBytes(event.total)}`;
  });
  request.addEventListener("load", () => {
    state.busy = false;
    let result = {};
    try { result = JSON.parse(request.responseText || "{}"); } catch (_) { result = {}; }
    if (request.status === 202) {
      progress.value = 100;
      byId("upload-progress-text").textContent = "上传完成，校验已通过";
      state.candidatePublishBlocked = false;
      renderCandidate(result);
      showToast("发布包校验通过，请核对后确认发布。");
    } else {
      resetChecks("failed");
      showPageError(messageFor(result, "上传或校验失败。"));
      if (request.status === 401) expiredSession();
      if (request.status === 503) setReadOnly(true, result);
      restoreAuthoritativeCandidate();
    }
  });
  request.addEventListener("error", async () => {
    state.busy = false;
    showPageError("网络中断，未确认发布。可使用同一文件重试上传。");
    await restoreAuthoritativeCandidate();
  });
  request.send(file);
}

function openPublish() {
  if (byId("publish-button").disabled) return;
  byId("publish-confirm-check").checked = false;
  byId("publish-dialog").showModal();
  byId("publish-confirm-check").focus();
}

async function publishCandidate(event) {
  event.preventDefault();
  if (!byId("publish-confirm-check").checked || !state.candidate) return;
  state.publishOperation = state.publishOperation || crypto.randomUUID();
  const button = byId("publish-confirm-button");
  button.disabled = true;
  try {
    const result = await api(`/admin/api/candidates/${state.candidate.candidate_id}/publish`, {
      method: "POST",
      headers: { "Idempotency-Key": state.publishOperation },
      body: JSON.stringify({ confirm: true })
    });
    byId("publish-dialog").close();
    state.candidate = null;
    state.uploadOperation = "";
    state.publishOperation = "";
    byId("release-file").value = "";
    selectedFileChanged();
    await loadDashboard();
    showToast(`v${result.version || "新版本"} 已发布。`);
  } catch (error) {
    byId("publish-dialog").close();
    showPageError(`${messageFor(error, "发布失败。")} ${error.operationId ? `操作编号：${error.operationId}` : ""}`.trim());
  } finally {
    button.disabled = false;
  }
}

function openRollback(item) {
  state.rollbackRelease = item.id;
  byId("rollback-version").textContent = `v${item.version}`;
  byId("rollback-password").value = "";
  byId("rollback-confirm-check").checked = false;
  byId("rollback-error").hidden = true;
  byId("rollback-dialog").showModal();
  byId("rollback-password").focus();
}

async function rollback(event) {
  event.preventDefault();
  if (!state.rollbackRelease || !byId("rollback-confirm-check").checked) return;
  const button = byId("rollback-confirm-button");
  const errorBox = byId("rollback-error");
  const operation = state.rollbackOperations.get(state.rollbackRelease) || crypto.randomUUID();
  state.rollbackOperations.set(state.rollbackRelease, operation);
  button.disabled = true;
  errorBox.hidden = true;
  try {
    const result = await api(`/admin/api/releases/${state.rollbackRelease}/rollback`, {
      method: "POST",
      headers: { "Idempotency-Key": operation },
      body: JSON.stringify({ confirm: true, current_password: byId("rollback-password").value })
    });
    state.rollbackOperations.delete(state.rollbackRelease);
    byId("rollback-dialog").close();
    await loadDashboard();
    showToast(`已回滚到 v${result.version}。已升级客户端不会自动降级。`);
  } catch (error) {
    errorBox.textContent = `${messageFor(error, "回滚失败。")} ${error.operationId ? `操作编号：${error.operationId}` : ""}`.trim();
    errorBox.hidden = false;
    if (error.code === "password_rejected") byId("rollback-password").focus();
  } finally {
    byId("rollback-password").value = "";
    button.disabled = false;
  }
}

function clearRollbackPassword() {
  byId("rollback-password").value = "";
}

function closeDialog(event) {
  const dialog = byId(event.currentTarget.dataset.closeDialog);
  if (dialog) {
    if (dialog.id === "rollback-dialog") clearRollbackPassword();
    dialog.close();
  }
}

document.addEventListener("DOMContentLoaded", () => {
  byId("login-form").addEventListener("submit", login);
  byId("password-form").addEventListener("submit", changePassword);
  byId("logout-button").addEventListener("click", logout);
  byId("retry-button").addEventListener("click", initialize);
  byId("refresh-button").addEventListener("click", loadDashboard);
  byId("release-file").addEventListener("change", selectedFileChanged);
  byId("upload-button").addEventListener("click", uploadSelected);
  byId("publish-button").addEventListener("click", openPublish);
  byId("publish-form").addEventListener("submit", publishCandidate);
  byId("rollback-form").addEventListener("submit", rollback);
  byId("rollback-dialog").addEventListener("close", clearRollbackPassword);
  for (const button of document.querySelectorAll("[data-close-dialog]")) button.addEventListener("click", closeDialog);
  initialize();
});
