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
  candidatePublishBlocked: false,
  dashboardEpoch: 0,
  activeAdminTab: "cards",
  licenseSoftware: [],
  licensePackages: [],
  licenseCards: [],
  selectedCardIDs: new Set(),
  generatedPlaintexts: [],
  generatedClearTimer: 0,
  pendingPackageChange: null,
  activeCardID: "",
  licenseEpoch: 0,
  licenseControllers: new Set(),
  mutationKeys: new Map(),
  licensePages: {
    cards: { page: 1, pageSize: 100, total: 0 },
    software: { page: 1, pageSize: 50, total: 0 },
    packages: { page: 1, pageSize: 50, total: 0 },
    devices: { page: 1, pageSize: 50, total: 0 }
  }
};

const gateNames = ["package", "channel", "version", "signature", "files", "references", "storage"];

const byId = (id) => document.getElementById(id);
const views = ["login-view", "password-view", "console-view"];
const passwordGuidance = "请使用至少 8 位、与临时密码不同的密码。";
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
  operation_failed: "操作失败，请记录操作编号并稍后重试。",
  license_service_unavailable: "授权服务暂时不可用，请稍后重试。",
  rate_limited: "操作过于频繁，请按提示时间重试。",
  invalid_request: "输入内容不符合要求，请检查后重试。"
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

function codePointLength(value) {
  return Array.from(value).length;
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
    error.retryAfter = Number.parseInt(response.headers.get("Retry-After") || "0", 10) || 0;
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
  clearLicenseTransientState();
  state.mutationKeys.clear();
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
      await openDefaultAdminTab();
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
      await openDefaultAdminTab();
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
  const errorBox = byId("password-error");
  const newPassword = form.elements.new_password;
  if (codePointLength(newPassword.value) < 8) {
    errorBox.textContent = passwordGuidance;
    errorBox.hidden = false;
    newPassword.focus();
    return;
  }
  const button = form.querySelector("button[type='submit']");
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
    await openDefaultAdminTab();
    showToast("密码已更新。以后请使用新密码登录。");
  } catch (error) {
    errorBox.textContent = messageFor(error, "密码修改失败。");
    errorBox.hidden = false;
  } finally {
    button.disabled = false;
  }
}

async function logout() {
  clearLicenseTransientState();
  try {
    await api("/admin/api/session/logout", { method: "POST", body: "{}" });
  } catch (error) {
    if (error.status !== 401) {
      showPageError(messageFor(error, "退出失败，当前登录仍然有效，请重试。"));
      showToast("退出失败，未清除当前登录。 ");
      return;
    }
  }
  state.mutationKeys.clear();
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
    value.colSpan = 7;
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
    row.append(cell(item.actor || "管理员"));
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
    if (description && !item.dataset.defaultEvidence) item.dataset.defaultEvidence = description.textContent;
    if (description && gate && gate.evidence) description.textContent = gate.evidence;
  }
}

function allGatesPassed(candidate) {
  const gates = candidate && candidate.report && candidate.report.gates;
  return Boolean(gates && gateNames.every((name) => gates[name] && gates[name].passed === true));
}

function renderCandidate(candidate) {
  if (candidate && candidate.state === "published") candidate = null;
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
  const passed = candidate.state === "validated" && report.verified === true && allGatesPassed(candidate);
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
  const canPublish = !state.candidatePublishBlocked && state.candidate && state.candidate.state === "validated" && state.candidate.report && state.candidate.report.verified && allGatesPassed(state.candidate);
  byId("publish-button").disabled = !canPublish || state.readOnly || state.busy;
}

async function loadDashboard() {
  const epoch = ++state.dashboardEpoch;
  clearPageError();
  setLoading(true);
  try {
    const [historyResult, candidateResult] = await Promise.allSettled([
      api("/admin/api/releases", { method: "GET" }),
      api("/admin/api/candidates/current", { method: "GET" })
    ]);
    if (epoch !== state.dashboardEpoch) return;
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
    if (epoch === state.dashboardEpoch) setLoading(false);
  }
}

function selectedFileChanged() {
  state.dashboardEpoch += 1;
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
  const epoch = ++state.dashboardEpoch;
  state.candidatePublishBlocked = true;
  try {
    const authoritative = await api("/admin/api/candidates/current", { method: "GET" });
    if (epoch !== state.dashboardEpoch) return;
    renderCandidate(authoritative);
    byId("candidate-state").textContent = "旧候选已保留（不可发布）";
    byId("publish-button").disabled = true;
  } catch (error) {
    if (epoch !== state.dashboardEpoch) return;
    if (error.status === 404) renderCandidate(null);
  }
}

function uploadSelected() {
  const file = byId("release-file").files[0];
  if (!file || state.readOnly || state.busy) return;
  state.dashboardEpoch += 1;
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
      state.dashboardEpoch += 1;
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

function clearLicenseError() {
  byId("license-error").hidden = true;
}

function showLicenseError(error) {
  byId("license-error-message").textContent = messageFor(error, "授权服务暂时不可用，请稍后重试。");
  byId("license-error").hidden = false;
}

function wait(milliseconds) {
  return new Promise((resolve) => window.setTimeout(resolve, milliseconds));
}

async function licenseRead(path, options = {}) {
  let latest;
  for (let attempt = 0; attempt < 3; attempt += 1) {
    try {
      return await api(path, options);
    } catch (error) {
      latest = error;
      const temporary = !error.status || error.status === 429 || error.status >= 500;
      if (!temporary || error.status === 401 || attempt === 2) throw error;
      const exponential = 250 * (2 ** attempt);
      const retryAfter = Math.min(10000, Math.max(0, (error.retryAfter || 0) * 1000));
      const delay = Math.min(10000, Math.max(exponential + Math.floor(Math.random() * 251), retryAfter));
      await wait(delay);
    }
  }
  throw latest;
}

function setActionStatus(id, message, kind = "") {
  const target = byId(id);
  target.textContent = message;
  target.className = `inline-status ${kind}`.trim();
}

function coolDownAction(button, seconds, statusID) {
  const safeSeconds = Math.min(3600, Math.max(1, Number(seconds) || 1));
  const until = Date.now() + safeSeconds * 1000;
  button.dataset.retryUntil = String(until);
  button.disabled = true;
  const status = byId(statusID);
  const previousLive = status ? status.getAttribute("aria-live") : null;
  if (status) status.setAttribute("aria-live", "off");
  const update = () => {
    const remaining = Math.ceil((until - Date.now()) / 1000);
    if (remaining > 0) {
      setActionStatus(statusID, `请等待 ${remaining} 秒后重试。`, "warning");
      window.setTimeout(update, Math.min(1000, remaining * 1000));
      return;
    }
    delete button.dataset.retryUntil;
    button.disabled = false;
    if (status) {
      if (previousLive === null) status.removeAttribute("aria-live");
      else status.setAttribute("aria-live", previousLive);
    }
    setActionStatus(statusID, "现在可以重试。");
  };
  update();
}

function canonicalValue(value) {
  if (Array.isArray(value)) return value.map(canonicalValue);
  if (value && typeof value === "object") {
    const result = {};
    for (const key of Object.keys(value).sort()) result[key] = canonicalValue(value[key]);
    return result;
  }
  return value;
}

function mutationSignature(path, method, body) {
  return `${method.toUpperCase()} ${path} ${JSON.stringify(canonicalValue(body))}`;
}

async function licenseMutation(path, method, body, button, statusID, request = null) {
  if (button && (button.disabled || Number(button.dataset.retryUntil || 0) > Date.now())) return null;
  if (button) button.disabled = true;
  const signature = mutationSignature(path, method, body);
  const idempotencyKey = state.mutationKeys.get(signature) || crypto.randomUUID();
  state.mutationKeys.set(signature, idempotencyKey);
  try {
    const result = await api(path, {
      method,
      headers: { "Idempotency-Key": idempotencyKey },
      body: JSON.stringify(body),
      signal: request ? request.controller.signal : undefined
    });
    state.mutationKeys.delete(signature);
    clearLicenseError();
    return result;
  } catch (error) {
    if (error.name === "AbortError") return null;
    const temporary = !error.status || error.status === 429 || error.status >= 500;
    if (!temporary) state.mutationKeys.delete(signature);
    if (error.code === "license_service_unavailable") showLicenseError(error);
    if (error.status === 429 && button) {
      coolDownAction(button, error.retryAfter, statusID);
    } else if (statusID) {
      setActionStatus(statusID, messageFor(error, "操作失败，请稍后重试。"), "error");
    }
    return null;
  } finally {
    if (button && !button.dataset.retryUntil) button.disabled = false;
  }
}

function beginLicenseRequest() {
  const controller = new AbortController();
  state.licenseControllers.add(controller);
  return { controller, epoch: state.licenseEpoch };
}

function endLicenseRequest(request) {
  state.licenseControllers.delete(request.controller);
}

function licenseRequestStillCurrent(request, tab = state.activeAdminTab) {
  return !request.controller.signal.aborted && request.epoch === state.licenseEpoch && state.activeAdminTab === tab;
}

async function userLicenseRead(path, options, button, statusID, tab) {
  if (button && (button.disabled || Number(button.dataset.retryUntil || 0) > Date.now())) return null;
  if (button) button.disabled = true;
  const request = beginLicenseRequest();
  try {
    const result = await api(path, { ...options, signal: request.controller.signal });
    if (!licenseRequestStillCurrent(request, tab)) return null;
    clearLicenseError();
    return { result, request };
  } catch (error) {
    if (error.name === "AbortError") return null;
    if (error.status === 429 && button) coolDownAction(button, error.retryAfter, statusID);
    else if (error.code === "license_service_unavailable") showLicenseError(error);
    else if (statusID) setActionStatus(statusID, messageFor(error, "读取失败，请稍后重试。"), "error");
    return null;
  } finally {
    endLicenseRequest(request);
    if (button && !button.dataset.retryUntil) button.disabled = false;
  }
}

function clearGeneratedCards() {
  if (state.generatedClearTimer) window.clearTimeout(state.generatedClearTimer);
  state.generatedClearTimer = 0;
  state.generatedPlaintexts = [];
  byId("generated-card-list").textContent = "";
  byId("generated-card-window").hidden = true;
}

function closeLicenseDialogs() {
  for (const id of ["package-impact-dialog", "renew-card-dialog", "remark-card-dialog", "delete-card-dialog"]) {
    const dialog = byId(id);
    if (dialog && dialog.open) dialog.close();
  }
}

function clearLicenseTransientState() {
  state.licenseEpoch += 1;
  for (const controller of state.licenseControllers) controller.abort();
  state.licenseControllers.clear();
  clearGeneratedCards();
  byId("filter-complete-card").value = "";
  byId("generate-note").value = "";
  byId("remark-value").value = "";
  state.activeCardID = "";
  state.selectedCardIDs.clear();
  state.pendingPackageChange = null;
  state.mutationKeys.clear();
  byId("delete-card-confirm-check").checked = false;
  byId("delete-card-target").textContent = "卡密 — · 套餐 —";
  closeLicenseDialogs();
  updateCardSelection();
}

async function openDefaultAdminTab() {
  clearLicenseError();
  await selectAdminTab("cards", true);
}

async function selectAdminTab(name, force = false) {
  if (!force && state.activeAdminTab === name && !byId(`${name}-panel`).hidden) return;
  if (state.activeAdminTab !== name) clearLicenseTransientState();
  state.activeAdminTab = name;
  byId("empty-state").hidden = name !== "ota";
  for (const tab of document.querySelectorAll("[data-admin-tab]")) {
    const active = tab.dataset.adminTab === name;
    tab.classList.toggle("active", active);
    tab.setAttribute("aria-selected", String(active));
    tab.tabIndex = active ? 0 : -1;
    byId(`${tab.dataset.adminTab}-panel`).hidden = !active;
  }
  setLoading(true);
  try {
    if (name === "cards") await loadCardsWorkspace();
    else if (name === "software") await loadSoftware();
    else if (name === "packages") await loadPackagesWorkspace();
    else if (name === "devices") await loadDevices();
    else if (name === "ota") await loadDashboard();
  } catch (error) {
    if (error.status !== 401) showLicenseError(error);
  } finally {
    setLoading(false);
  }
}

function option(value, label) {
  const item = document.createElement("option");
  item.value = value;
  item.textContent = label;
  return item;
}

function updatePagination(kind, itemCount) {
  const page = state.licensePages[kind];
  const start = page.total && itemCount ? ((page.page - 1) * page.pageSize) + 1 : 0;
  const end = start ? start + itemCount - 1 : 0;
  byId(`${kind}-page-range`).textContent = `${start}–${end} / ${page.total}`;
  byId(`${kind}-page-prev`).disabled = page.page <= 1;
  byId(`${kind}-page-next`).disabled = page.page * page.pageSize >= page.total;
}

async function fetchAllLicensePages(path) {
  const items = [];
  let page = 1;
  let total = 0;
  do {
    const separator = path.includes("?") ? "&" : "?";
    const result = await licenseRead(`${path}${separator}page=${page}&page_size=100`, { method: "GET" });
    const chunk = Array.isArray(result.items) ? result.items : [];
    items.push(...chunk);
    total = Number(result.total) || items.length;
    page += 1;
    if (!chunk.length) break;
  } while (items.length < total && page <= 100);
  return items;
}

function refreshPackageSelects() {
  for (const [id, prompt] of [["generate-package", "选择套餐"], ["filter-package", "全部套餐"]]) {
    const select = byId(id);
    const selected = select.value;
    select.replaceChildren(option("", prompt));
    for (const item of state.licensePackages) select.append(option(item.id, item.name));
    if ([...select.options].some((item) => item.value === selected)) select.value = selected;
  }
}

async function ensureLicenseReferences() {
  const [softwareItems, packageItems] = await Promise.all([
    fetchAllLicensePages("/admin/api/license/software"),
    fetchAllLicensePages("/admin/api/license/packages")
  ]);
  state.licenseSoftware = softwareItems;
  state.licensePackages = packageItems;
  refreshPackageSelects();
  clearLicenseError();
}

function cardFilterPayload() {
  const page = state.licensePages.cards;
  return {
    page: page.page,
    page_size: page.pageSize,
    complete_card: byId("filter-complete-card").value.trim(),
    prefix8: byId("filter-prefix8").value.trim(),
    last4: byId("filter-last4").value.trim(),
    note_contains: byId("filter-note").value.trim(),
    package_id: byId("filter-package").value,
    card_type: byId("filter-card-type").value,
    state: byId("filter-state").value
  };
}

async function loadCardsWorkspace() {
  await ensureLicenseReferences();
  await loadCards();
}

async function loadCards(button = null, payloadOverride = null) {
  const payload = payloadOverride || cardFilterPayload();
  let result;
  if (button) {
    const response = await userLicenseRead("/admin/api/license/cards/search", { method: "POST", body: JSON.stringify(payload) }, button, "card-action-status", "cards");
    if (!response) return false;
    result = response.result;
  } else {
    const request = beginLicenseRequest();
    try {
      result = await licenseRead("/admin/api/license/cards/search", { method: "POST", body: JSON.stringify(payload), signal: request.controller.signal });
      if (!licenseRequestStillCurrent(request, "cards")) return false;
    } catch (error) {
      if (error.name !== "AbortError") throw error;
      return false;
    } finally {
      endLicenseRequest(request);
    }
  }
  state.licenseCards = Array.isArray(result.items) ? result.items : [];
  const page = state.licensePages.cards;
  page.page = Number(result.page) || page.page;
  page.pageSize = Number(result.page_size) || page.pageSize;
  page.total = Number(result.total) || 0;
  if (!state.licenseCards.length && page.total > 0 && page.page > 1) {
    page.page -= 1;
    return loadCards();
  }
  state.selectedCardIDs.clear();
  renderCards(page.total);
  clearLicenseError();
  return true;
}

function cardStateLabel(value) {
  return value === "effective" ? "有效" : value === "expired" ? "已到期" : "未激活";
}

function cardTypeLabel(value) {
  return value === "quarter" ? "季卡" : value === "half_year" ? "半年卡" : value === "year" ? "年卡" : "月卡";
}

function actionButton(text, action, handler, danger = false) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = `button ${danger ? "danger" : "ghost"}`;
  button.textContent = text;
  button.dataset.action = action;
  button.addEventListener("click", handler);
  return button;
}

function renderCards(total) {
  const body = byId("cards-body");
  body.replaceChildren();
  byId("cards-total").textContent = `${total} 张`;
  if (!state.licenseCards.length) {
    const row = document.createElement("tr");
    const empty = cell("没有符合条件的卡密"); empty.colSpan = 10; empty.className = "table-empty"; row.append(empty); body.append(row);
    updateCardSelection();
    updatePagination("cards", 0);
    return;
  }
  for (const item of state.licenseCards) {
    const row = document.createElement("tr");
    row.dataset.cardId = item.id;
    const selectCell = document.createElement("td");
    const checkbox = document.createElement("input"); checkbox.type = "checkbox"; checkbox.setAttribute("aria-label", `选择 ${item.prefix8}…${item.last4}`);
    checkbox.addEventListener("change", () => { if (checkbox.checked) state.selectedCardIDs.add(item.id); else state.selectedCardIDs.delete(item.id); updateCardSelection(); });
    selectCell.append(checkbox); row.append(selectCell);
    const keyCell = cell(`${item.prefix8}••••••••${item.last4}`); keyCell.className = "license-key-mask"; row.append(keyCell);
    row.append(cell(item.package_name || "—"));
    row.append(cell(`${cardTypeLabel(item.card_type)}·${item.duration_days} 天`));
    const statusCell = document.createElement("td"); const status = document.createElement("span"); status.className = `state-dot ${item.state === "effective" ? "success" : item.state === "expired" ? "danger" : "warning"}`; status.textContent = cardStateLabel(item.state); statusCell.append(status); row.append(statusCell);
    row.append(cell(formatDate(item.activated_at))); row.append(cell(formatDate(item.expires_at)));
    const binding = cell(item.binding_short_id || "未绑定"); binding.className = "short-id"; row.append(binding);
    row.append(cell(item.note || "—"));
    const actions = document.createElement("td"); actions.className = "row-actions";
    actions.append(actionButton("复制", "card-copy", (event) => copyCard(item, event.currentTarget)));
    actions.append(actionButton("续期", "card-renew", () => openCardDialog("renew-card-dialog", item)));
    actions.append(actionButton("备注", "card-remark", () => openCardDialog("remark-card-dialog", item)));
    const unbind = actionButton("解绑", "card-unbind", (event) => unbindCard(item, event.currentTarget)); unbind.disabled = !item.binding_short_id; actions.append(unbind);
    actions.append(actionButton("永久删除", "card-delete", () => openCardDialog("delete-card-dialog", item), true));
    row.append(actions); body.append(row);
  }
  updateCardSelection();
  updatePagination("cards", state.licenseCards.length);
}

function updateCardSelection() {
  byId("card-selection-count").textContent = `已选 ${state.selectedCardIDs.size} 张`;
  byId("export-selected-cards").disabled = state.selectedCardIDs.size === 0;
  byId("select-all-cards").checked = state.licenseCards.length > 0 && state.selectedCardIDs.size === state.licenseCards.length;
}

async function generateCards(event) {
  event.preventDefault();
  const button = byId("generate-cards-button");
  const note = byId("generate-note").value.trim();
  const request = beginLicenseRequest();
  const result = await licenseMutation("/admin/api/license/cards/generate", "POST", {
    package_id: byId("generate-package").value,
    duration_days: Number(byId("generate-card-type").value),
    count: Number(byId("generate-count").value),
    default_note: note || null,
    per_card_notes: []
  }, button, "card-action-status", request);
  endLicenseRequest(request);
  if (!result || !licenseRequestStillCurrent(request, "cards")) return;
  const items = Array.isArray(result.items) ? result.items : [];
  state.generatedPlaintexts = items.map((item) => item.plaintext).filter(Boolean);
  byId("generated-card-list").textContent = state.generatedPlaintexts.join("\n");
  byId("generated-card-window").hidden = state.generatedPlaintexts.length === 0;
  if (state.generatedClearTimer) window.clearTimeout(state.generatedClearTimer);
  state.generatedClearTimer = window.setTimeout(clearGeneratedCards, 60000);
  setActionStatus("card-action-status", `已生成 ${items.length} 张，请立即复制或导出。`);
  try { await loadCards(); } catch (error) { showLicenseError(error); }
}

async function copyGeneratedCards() {
  if (!state.generatedPlaintexts.length) return;
  const epoch = state.licenseEpoch;
  try {
    await navigator.clipboard.writeText(state.generatedPlaintexts.join("\n"));
    if (epoch === state.licenseEpoch && state.activeAdminTab === "cards") setActionStatus("card-action-status", "新卡密已复制。");
  } catch (_) {
    if (epoch === state.licenseEpoch) setActionStatus("card-action-status", "复制失败，请允许剪贴板权限后重试。", "error");
  }
}

async function copyCard(item, button) {
  try {
    const response = await userLicenseRead(`/admin/api/license/cards/${item.id}/copy`, { method: "GET" }, button, "card-action-status", "cards");
    if (!response || !licenseRequestStillCurrent(response.request, "cards")) return;
    try {
      await navigator.clipboard.writeText(response.result.plaintext || "");
      if (licenseRequestStillCurrent(response.request, "cards")) setActionStatus("card-action-status", `已复制 ${item.prefix8}…${item.last4}。`);
    } catch (_) {
      if (licenseRequestStillCurrent(response.request, "cards")) setActionStatus("card-action-status", "复制失败，请允许剪贴板权限后重试。", "error");
    }
  } catch (error) {
    setActionStatus("card-action-status", messageFor(error, "复制失败，请稍后重试。"), "error");
  }
}

async function exportSelectedCards(event) {
  const button = event.currentTarget;
  if (!state.selectedCardIDs.size || button.disabled) return;
  button.disabled = true;
  const request = beginLicenseRequest();
  try {
    const response = await fetch("/admin/api/license/cards/export", { method: "POST", credentials: "same-origin", cache: "no-store", signal: request.controller.signal, headers: { "Content-Type": "application/json", "X-CSRF-Token": state.csrf, "Idempotency-Key": crypto.randomUUID() }, body: JSON.stringify({ card_ids: [...state.selectedCardIDs] }) });
    if (!response.ok) {
      const body = await readJSON(response); const error = new Error(body.message || "导出失败。"); error.code = body.code || "operation_failed"; error.status = response.status; error.retryAfter = Number(response.headers.get("Retry-After")) || 0;
      if (error.status === 401) await expiredSession();
      if (error.status === 429) coolDownAction(button, error.retryAfter, "card-action-status");
      else setActionStatus("card-action-status", messageFor(error, "导出失败。"), "error");
      return;
    }
    const blob = await response.blob();
    if (!licenseRequestStillCurrent(request, "cards")) return;
    const href = URL.createObjectURL(blob); const link = document.createElement("a"); link.href = href; link.download = "license-cards.csv"; link.click(); URL.revokeObjectURL(href);
    setActionStatus("card-action-status", `已导出 ${state.selectedCardIDs.size} 张卡密。`);
  } catch (error) {
    if (error.name !== "AbortError" && licenseRequestStillCurrent(request, "cards")) setActionStatus("card-action-status", "导出失败，请稍后重试。", "error");
  } finally {
    endLicenseRequest(request);
    if (!button.dataset.retryUntil) button.disabled = state.selectedCardIDs.size === 0;
  }
}

function openCardDialog(dialogID, item) {
  state.activeCardID = item.id;
  if (dialogID === "remark-card-dialog") byId("remark-value").value = item.note || "";
  if (dialogID === "delete-card-dialog") {
    byId("delete-card-confirm-check").checked = false;
    byId("delete-card-target").textContent = `卡密 ${item.prefix8}…${item.last4} · 套餐 ${item.package_name || "—"}`;
  }
  byId(dialogID).showModal();
}

async function submitCardAction(path, method, body, button, dialogID) {
  const result = await licenseMutation(`/admin/api/license/cards/${state.activeCardID}${path}`, method, body, button, "card-action-status");
  if (!result) return;
  if (dialogID && byId(dialogID).open) byId(dialogID).close();
  try { await loadCards(); } catch (error) { showLicenseError(error); }
}

async function renewCard(event) { event.preventDefault(); await submitCardAction("/renew", "POST", { duration_days: Number(byId("renew-duration").value) }, event.submitter || event.currentTarget.querySelector("button[type=submit]"), "renew-card-dialog"); }
async function remarkCard(event) { event.preventDefault(); const note = byId("remark-value").value.trim(); await submitCardAction("/remark", "PATCH", { note: note || null }, event.submitter || event.currentTarget.querySelector("button[type=submit]"), "remark-card-dialog"); }
async function deleteCard(event) { event.preventDefault(); if (!byId("delete-card-confirm-check").checked) return; await submitCardAction("", "DELETE", {}, byId("delete-card-confirm"), "delete-card-dialog"); }
async function unbindCard(item, button) { state.activeCardID = item.id; await submitCardAction("/unbind", "POST", {}, button, ""); }

async function searchCards(event) {
  event.preventDefault();
  state.licensePages.cards.page = 1;
  const payload = cardFilterPayload();
  byId("filter-complete-card").value = "";
  await loadCards(byId("search-cards-button"), payload);
}
function resetCardFilter() { byId("card-filter-form").reset(); state.licensePages.cards.page = 1; loadCards(byId("reset-card-filter")).catch(showLicenseError); }
function selectAllCards(event) { state.selectedCardIDs.clear(); for (const checkbox of document.querySelectorAll("#cards-body input[type=checkbox]")) { checkbox.checked = event.currentTarget.checked; if (checkbox.checked) state.selectedCardIDs.add(checkbox.closest("tr").dataset.cardId); } updateCardSelection(); }

async function loadSoftware(button = null) {
  const page = state.licensePages.software;
  const path = `/admin/api/license/software?page=${page.page}&page_size=${page.pageSize}`;
  const response = button ? await userLicenseRead(path, { method: "GET" }, button, "software-action-status", "software") : null;
  const result = button ? (response && response.result) : await licenseRead(path, { method: "GET" });
  if (!result) return false;
  state.licenseSoftware = Array.isArray(result.items) ? result.items : [];
  page.page = Number(result.page) || page.page;
  page.pageSize = Number(result.page_size) || page.pageSize;
  page.total = Number(result.total) || 0;
  if (!state.licenseSoftware.length && page.total > 0 && page.page > 1) { page.page -= 1; return loadSoftware(); }
  renderSoftware(); clearLicenseError();
  return true;
}

function renderSoftware() {
  const body = byId("software-body"); body.replaceChildren();
  if (!state.licenseSoftware.length) { const row = document.createElement("tr"); const empty = cell("暂无软件"); empty.colSpan = 5; empty.className = "table-empty"; row.append(empty); body.append(row); updatePagination("software", 0); return; }
  for (const item of state.licenseSoftware) {
    const row = document.createElement("tr"); row.dataset.softwareId = item.id;
    const id = cell(item.id); id.className = "short-id"; row.append(id);
    const nameCell = document.createElement("td"); const input = document.createElement("input"); input.value = item.name; input.maxLength = 128; input.setAttribute("aria-label", `软件名称 ${item.name}`); nameCell.append(input); row.append(nameCell);
    row.append(cell(String(item.package_count || 0)));
    row.append(cell(item.enabled ? "已启用" : "已停用"));
    const actions = document.createElement("td"); actions.className = "row-actions";
    actions.append(actionButton("保存名称", "software-save", (event) => updateSoftwareName(item, input.value, event.currentTarget)));
    actions.append(actionButton(item.enabled ? "停用" : "启用", "software-toggle", (event) => toggleSoftware(item, event.currentTarget), item.enabled));
    row.append(actions); body.append(row);
  }
  updatePagination("software", state.licenseSoftware.length);
}

async function createSoftware(event) {
  event.preventDefault(); const button = byId("software-create-button");
  const result = await licenseMutation("/admin/api/license/software", "POST", { name: byId("software-name").value.trim() }, button, "software-action-status");
  if (!result) return;
  result.package_count = Number(result.package_count) || 0;
  state.licensePages.software.total += 1;
  if (state.licenseSoftware.length < state.licensePages.software.pageSize) state.licenseSoftware.push(result);
  byId("software-name").value = "";
  renderSoftware();
  setActionStatus("software-action-status", "软件已创建。");
}

async function updateSoftwareName(item, name, button) {
  const result = await licenseMutation(`/admin/api/license/software/${item.id}`, "PATCH", { name: name.trim() }, button, "software-action-status");
  if (!result) return; item.name = result.name || name.trim(); renderSoftware(); setActionStatus("software-action-status", "软件名称已更新。");
}

async function toggleSoftware(item, button) {
  const action = item.enabled ? "disable" : "enable";
  const result = await licenseMutation(`/admin/api/license/software/${item.id}/${action}`, "POST", {}, button, "software-action-status");
  if (!result) return; item.enabled = !item.enabled; renderSoftware(); setActionStatus("software-action-status", item.enabled ? "软件已启用。" : "软件已停用。");
}

function softwareCheckList(selectedIDs, attribute) {
  const wrapper = document.createElement("div"); wrapper.className = "check-list-inline";
  for (const software of state.licenseSoftware) {
    const label = document.createElement("label"); const input = document.createElement("input"); input.type = "checkbox"; input.value = software.id; input.checked = selectedIDs.includes(software.id); if (attribute) input.setAttribute(attribute, ""); label.append(input, document.createTextNode(software.name)); wrapper.append(label);
  }
  return wrapper;
}

async function loadPackagesWorkspace() {
  state.licenseSoftware = await fetchAllLicensePages("/admin/api/license/software");
  renderPackageMembers();
  await loadPackages();
}

function renderPackageMembers() { const target = byId("package-create-members"); target.replaceChildren(softwareCheckList([], "data-package-create-member")); }

function renderPackages() {
  const body = byId("packages-body"); body.replaceChildren();
  if (!state.licensePackages.length) { const row = document.createElement("tr"); const empty = cell("暂无套餐"); empty.colSpan = 3; empty.className = "table-empty"; row.append(empty); body.append(row); updatePagination("packages", 0); return; }
  for (const item of state.licensePackages) {
    const row = document.createElement("tr"); row.dataset.packageId = item.id; row.append(cell(item.name));
    const members = document.createElement("td"); const list = softwareCheckList(item.software_ids || [], "data-package-member"); members.append(list); row.append(members);
    const actions = document.createElement("td"); actions.className = "row-actions"; actions.append(actionButton("预览并更新", "package-preview", (event) => previewPackageChange(item, list, event.currentTarget))); row.append(actions); body.append(row);
  }
  updatePagination("packages", state.licensePackages.length);
}

async function loadPackages(button = null) {
  const page = state.licensePages.packages;
  const path = `/admin/api/license/packages?page=${page.page}&page_size=${page.pageSize}`;
  const response = button ? await userLicenseRead(path, { method: "GET" }, button, "package-action-status", "packages") : null;
  const result = button ? (response && response.result) : await licenseRead(path, { method: "GET" });
  if (!result) return false;
  state.licensePackages = Array.isArray(result.items) ? result.items : [];
  page.page = Number(result.page) || page.page;
  page.pageSize = Number(result.page_size) || page.pageSize;
  page.total = Number(result.total) || 0;
  if (!state.licensePackages.length && page.total > 0 && page.page > 1) { page.page -= 1; return loadPackages(); }
  renderPackages(); clearLicenseError(); return true;
}

async function createPackage(event) {
  event.preventDefault(); const ids = [...document.querySelectorAll("#package-create-members input:checked")].map((item) => item.value); const button = event.submitter || byId("package-create-form").querySelector("button[type=submit]");
  const result = await licenseMutation("/admin/api/license/packages", "POST", { name: byId("package-name").value.trim(), software_ids: ids }, button, "package-action-status");
  if (!result) return; byId("package-create-form").reset(); setActionStatus("package-action-status", "套餐已创建。"); await loadPackages();
}

async function previewPackageChange(item, list, button) {
  const ids = [...list.querySelectorAll("input:checked")].map((input) => input.value);
  const result = await licenseMutation(`/admin/api/license/packages/${item.id}/impact`, "POST", { software_ids: ids }, button, "package-action-status");
  if (!result) return;
  state.pendingPackageChange = { item, ids };
  byId("package-impact-summary").textContent = `将影响 ${result.affected_valid_cards || 0} 张当前有效卡密；暂时离线的客户端可能最长 ${result.max_offline_lease_delay_minutes || 30} 分钟后失效。`;
  byId("package-impact-dialog").showModal();
}

async function confirmPackageChange(event) {
  event.preventDefault(); if (!state.pendingPackageChange) return; const pending = state.pendingPackageChange;
  const result = await licenseMutation(`/admin/api/license/packages/${pending.item.id}/members`, "PUT", { software_ids: pending.ids }, byId("package-impact-confirm"), "package-action-status");
  if (!result) return; pending.item.software_ids = pending.ids; state.pendingPackageChange = null; byId("package-impact-dialog").close(); renderPackages();
}

async function loadDevices(button = null) {
  const page = state.licensePages.devices;
  const path = `/admin/api/license/devices?page=${page.page}&page_size=${page.pageSize}`;
  const response = button ? await userLicenseRead(path, { method: "GET" }, button, "device-action-status", "devices") : null;
  const result = button ? (response && response.result) : await licenseRead(path, { method: "GET" });
  if (!result) return false;
  const items = Array.isArray(result.items) ? result.items : [];
  page.page = Number(result.page) || page.page;
  page.pageSize = Number(result.page_size) || page.pageSize;
  page.total = Number(result.total) || 0;
  if (!items.length && page.total > 0 && page.page > 1) { page.page -= 1; return loadDevices(); }
  renderDevices(items); clearLicenseError(); return true;
}

function renderDevices(items) {
  const body = byId("devices-body"); body.replaceChildren();
  if (!items.length) { const row = document.createElement("tr"); const empty = cell("暂无设备绑定"); empty.colSpan = 5; empty.className = "table-empty"; row.append(empty); body.append(row); updatePagination("devices", 0); return; }
  for (const item of items) {
    const row = document.createElement("tr"); const card = cell(item.card_short_id); card.className = "short-id"; row.append(card); const machine = cell(item.machine_short_id); machine.className = "short-id"; row.append(machine); row.append(cell(formatDate(item.bound_at))); row.append(cell(formatDate(item.last_successful_validation)));
    const action = document.createElement("td"); action.append(actionButton("管理员解绑", "device-unbind", (event) => unbindDevice(item, event.currentTarget), true)); row.append(action); body.append(row);
  }
  updatePagination("devices", items.length);
}

async function unbindDevice(item, button) {
  const result = await licenseMutation(`/admin/api/license/devices/${item.id}/unbind`, "POST", {}, button, "device-action-status");
  if (result) {
    try { await loadDevices(); } catch (error) { showLicenseError(error); }
  }
}

async function changeLicensePage(kind, delta, button) {
  const page = state.licensePages[kind];
  const previous = page.page;
  const target = previous + delta;
  if (target < 1 || (delta > 0 && previous * page.pageSize >= page.total)) return;
  page.page = target;
  try {
    let loaded = false;
    if (kind === "cards") loaded = await loadCards(button);
    else if (kind === "software") loaded = await loadSoftware(button);
    else if (kind === "packages") loaded = await loadPackages(button);
    else if (kind === "devices") loaded = await loadDevices(button);
    if (!loaded) page.page = previous;
  } catch (error) {
    page.page = previous;
    if (error.status !== 401) showLicenseError(error);
  }
}

function handleAdminTabKey(event) {
  if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
  const tabs = [...document.querySelectorAll("[data-admin-tab]")];
  const current = tabs.indexOf(event.currentTarget);
  let target = current;
  if (event.key === "ArrowLeft") target = (current - 1 + tabs.length) % tabs.length;
  if (event.key === "ArrowRight") target = (current + 1) % tabs.length;
  if (event.key === "Home") target = 0;
  if (event.key === "End") target = tabs.length - 1;
  event.preventDefault();
  tabs[target].focus();
  selectAdminTab(tabs[target].dataset.adminTab).catch(showLicenseError);
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

function clearLicenseDialogState(event) {
  if (event.currentTarget.id === "package-impact-dialog") state.pendingPackageChange = null;
  if (event.currentTarget.id === "remark-card-dialog") byId("remark-value").value = "";
  if (event.currentTarget.id === "delete-card-dialog") {
    byId("delete-card-confirm-check").checked = false;
    byId("delete-card-target").textContent = "卡密 — · 套餐 —";
  }
  state.activeCardID = "";
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
  for (const id of ["package-impact-dialog", "renew-card-dialog", "remark-card-dialog", "delete-card-dialog"]) byId(id).addEventListener("close", clearLicenseDialogState);
  for (const tab of document.querySelectorAll("[data-admin-tab]")) {
    tab.addEventListener("click", () => selectAdminTab(tab.dataset.adminTab));
    tab.addEventListener("keydown", handleAdminTabKey);
  }
  byId("license-retry-button").addEventListener("click", () => selectAdminTab(state.activeAdminTab, true));
  byId("card-generate-form").addEventListener("submit", generateCards);
  byId("copy-generated-cards").addEventListener("click", copyGeneratedCards);
  byId("clear-generated-cards").addEventListener("click", clearGeneratedCards);
  byId("card-filter-form").addEventListener("submit", searchCards);
  byId("reset-card-filter").addEventListener("click", resetCardFilter);
  byId("select-all-cards").addEventListener("change", selectAllCards);
  byId("export-selected-cards").addEventListener("click", exportSelectedCards);
  byId("software-create-form").addEventListener("submit", createSoftware);
  byId("package-create-form").addEventListener("submit", createPackage);
  byId("package-impact-form").addEventListener("submit", confirmPackageChange);
  byId("renew-form").addEventListener("submit", renewCard);
  byId("remark-form").addEventListener("submit", remarkCard);
  byId("delete-card-form").addEventListener("submit", deleteCard);
  byId("refresh-devices").addEventListener("click", (event) => loadDevices(event.currentTarget).catch(showLicenseError));
  for (const kind of ["cards", "software", "packages", "devices"]) {
    byId(`${kind}-page-prev`).addEventListener("click", (event) => changeLicensePage(kind, -1, event.currentTarget));
    byId(`${kind}-page-next`).addEventListener("click", (event) => changeLicensePage(kind, 1, event.currentTarget));
  }
  for (const button of document.querySelectorAll("[data-close-dialog]")) button.addEventListener("click", closeDialog);
  initialize();
});
