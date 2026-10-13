"""v1.0.4 账号/出口编排；保留助手的解码、状态与可中断等待适配。"""

from __future__ import annotations

import math
import os
import random
import re
import socket
import threading
import time
from urllib.parse import quote, urlsplit

import requests
from urllib3.util.request import ACCEPT_ENCODING

from lxds.behavior import (
    COOLDOWN_RANGE, FIRST_STAGGER_RANGE, PAGE_RANGE, READ_RANGE,
    RISK_ESCALATE_HITS, RISK_SILENT_COOLDOWN, RISK_SILENT_ROUNDS,
    RISK_SOFT_COOLDOWN, HumanPacer, pick_common_question, pick_profile,
)
from lxds.http_decode import decode_html_response
from lxds.zhidao_parse import classify_search_html, parse_search_page


_SEARCH = "https://zhidao.baidu.com/search?lm=0&rn=10&pn=0&fr=search&dyTabStr=null&word={}"
_PROXY_PORTS = (7890, 7891, 7897)
_PROBE_URLS = ("https://zhidao.baidu.com/robots.txt", "http://www.gstatic.com/generate_204",
               "https://www.google.com/generate_204")
_RISK_MARKERS = ("百度安全验证", "安全验证", "wappass", "请输入验证码")
REQUEST_TIMEOUT = 12
EXIT_DIRECT = "direct"
EXIT_PROXY = "proxy"


class PoolError(Exception):
    """采集请求池异常。"""


class CollectCancelled(PoolError):
    """采集已由用户停止。"""


class _RequestFailure(PoolError):
    """可重试的传输或 HTTP 状态失败，供 fetch 注入边界传递。"""


def _check_stop(stop_event) -> None:
    if stop_event is not None and stop_event.is_set():
        raise CollectCancelled("采集已停止")


def is_risk_html(html: str) -> bool:
    text = html or ""
    return len(text) < 60000 and any(marker in text for marker in _RISK_MARKERS)


def system_proxy_port() -> int:
    """原版额外读取 Windows 系统代理中的端口，仍只探测本机。"""
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                            r"Software\Microsoft\Windows\CurrentVersion\Internet Settings") as key:
            enabled = int(winreg.QueryValueEx(key, "ProxyEnable")[0] or 0)
            server = str(winreg.QueryValueEx(key, "ProxyServer")[0] or "")
    except (ImportError, OSError, ValueError, TypeError):
        return 0
    if not enabled or not server:
        return 0
    for part in re.split(r"[=;,\s]+", server):
        if ":" not in part:
            continue
        try:
            port = int(part.rsplit(":", 1)[1])
        except ValueError:
            continue
        if 1 <= port <= 65535:
            return port
    return 0


def candidate_ports() -> list[int]:
    ports = list(_PROXY_PORTS)
    extra = system_proxy_port()
    if extra and extra not in ports:
        ports.append(extra)
    return ports


def _tcp_alive(host: str, port: int, timeout: float) -> bool:
    try:
        with socket.create_connection((host, port), timeout=timeout):
            return True
    except OSError:
        return False


def probe_clash_http(port: int, timeout: float = 5.0, stop_event=None) -> bool:
    url = f"http://127.0.0.1:{port}"
    proxies = {"http": url, "https": url}
    for target in _PROBE_URLS:
        _check_stop(stop_event)
        try:
            response = requests.get(target, proxies=proxies, timeout=timeout,
                                    headers={"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"},
                                    allow_redirects=False)
        except requests.RequestException:
            _check_stop(stop_event)
            continue
        _check_stop(stop_event)
        if response.status_code in (200, 204):
            return True
    return False


def probe_clash_detail(detect_timeout: float = 1.0, retries: int = 2, stop_event=None) -> tuple[int, bool, str]:
    # Keep the helper's explicit deployment override. Automatic discovery follows
    # the original local TCP + HTTP probes when no override was configured.
    _check_stop(stop_event)
    override = os.environ.get("LXDS_CLASH_PROXY")
    if override:
        parsed = urlsplit(override)
        return parsed.port or (443 if parsed.scheme == "https" else 80), True, "已配置代理出口"
    ports = candidate_ports()
    port_alive = False
    for attempt in range(max(1, int(retries))):
        for port in ports:
            _check_stop(stop_event)
            alive = _tcp_alive("127.0.0.1", port, detect_timeout)
            _check_stop(stop_event)
            if not alive:
                continue
            port_alive = True
            ok = probe_clash_http(port, stop_event=stop_event)
            _check_stop(stop_event)
            if ok:
                return port, True, f"已检测到可用的 Clash 代理端口 {port}"
        if attempt + 1 < max(1, int(retries)):
            if stop_event is None:
                time.sleep(0.5)
            elif stop_event.wait(0.5):
                raise CollectCancelled("采集已停止")
    _check_stop(stop_event)
    if port_alive:
        return 0, False, "检测到本地代理端口，但代理走不通（请在 Clash 里换一个可用节点，或检查规则/系统代理设置）"
    return 0, False, "没检测到 Clash 的本地代理端口（{}）；请确认 Clash 已开启，并且打开了「系统代理」或「混合端口」".format(
        "、".join(str(port) for port in ports))


class AccountSlot:
    def __init__(self, name: str, cookie: str, exit_kind: str) -> None:
        self.name = name or "百度知道账号"
        self.cookie = (cookie or "").strip()
        self.exit_kind = exit_kind
        self.profile = pick_profile()
        self.pacer = HumanPacer()
        self.session = requests.Session()
        self.session.trust_env = False
        self.flagged = False
        self.flag_count = 0
        self.risk_streak = 0
        self._apply_headers()

    # Former helper state names remain readable/writable at the adapter boundary.
    risk_hits = property(lambda self: self.risk_streak,
                         lambda self, value: setattr(self, "risk_streak", value))
    risk_confirmed = property(lambda self: self.flagged,
                              lambda self, value: setattr(self, "flagged", value))
    unavailable_until = property(lambda self: self.pacer.next_at,
                                 lambda self, value: setattr(self.pacer, "next_at", value))

    def _apply_headers(self) -> None:
        headers = {
            "User-Agent": self.profile["ua"],
            "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8",
            "Accept-Language": "zh-CN,zh;q=0.9", "Accept-Encoding": ACCEPT_ENCODING,
            "Upgrade-Insecure-Requests": "1", "Connection": "keep-alive",
            "Referer": "https://zhidao.baidu.com/", "Sec-Fetch-Dest": "document",
            "Sec-Fetch-Mode": "navigate", "Sec-Fetch-Site": "same-origin", "Sec-Fetch-User": "?1",
        }
        if self.profile.get("sec_ch_ua"):
            headers["sec-ch-ua"] = self.profile["sec_ch_ua"]
            headers["sec-ch-ua-mobile"] = self.profile.get("mobile") or "?0"
            headers["sec-ch-ua-platform"] = self.profile.get("platform") or '"Windows"'
        self.session.headers.clear()
        self.session.headers.update(headers)
        if self.cookie:
            self.session.headers["Cookie"] = self.cookie

    def reroll_profile(self) -> None:
        self.profile = pick_profile()
        self._apply_headers()

    def proxies(self, pool: "SlotPool") -> dict | None:
        if self.exit_kind == EXIT_PROXY and pool.proxy_port:
            return {"http": pool.proxy_url, "https": pool.proxy_url}
        return None

    def exit_text(self, pool: "SlotPool") -> str:
        return f"代理 {pool.proxy_port or '未检测'}" if self.exit_kind == EXIT_PROXY else "直连"


class SlotPool:
    def __init__(self, accounts, pause_ask=None, on_log=None, stop_event=None, on_status=None) -> None:
        self.accounts = list(accounts or [])[:2]
        self.slots = [AccountSlot(account.get("name") or account.get("remark") or account.get("id") or "",
                                  account.get("cookie", ""), EXIT_DIRECT if index == 0 else EXIT_PROXY)
                      for index, account in enumerate(self.accounts)]
        self.single = len(self.slots) == 1
        self.pause_ask = pause_ask
        self.on_log = on_log
        self.stop_event = stop_event
        self.on_status = on_status
        self._lock = threading.RLock()
        self._probe_questions: set[str] = set()
        self._used_common_questions = self._probe_questions
        self.silent_rounds = 0
        self.proxy_port = 0
        self.proxy_url = os.environ.get("LXDS_CLASH_PROXY", f"http://127.0.0.1:{_PROXY_PORTS[0]}")
        self._started = False
        self.cache_generation = 0

    def _log(self, text) -> None:
        if self.on_log:
            self.on_log(str(text))

    def _status(self, text) -> None:
        if self.on_status:
            self.on_status(str(text))

    def stopped(self) -> bool:
        return bool(self.stop_event and self.stop_event.is_set())

    def state_text(self) -> str:
        parts = []
        for slot in self.slots:
            text = f"{slot.name} {slot.exit_text(self)}"
            if slot.flagged:
                text += f"（风控冷却 {max(1, int(round(slot.pacer.wait_time())))}s）"
            parts.append(text)
        return " · ".join(parts) or "直连"

    def _set_proxy_port(self, port: int) -> None:
        self.proxy_port = port
        self.proxy_url = os.environ.get("LXDS_CLASH_PROXY") or f"http://127.0.0.1:{port}"

    def start(self) -> None:
        _check_stop(self.stop_event)
        if self._started:
            return
        if not self.slots:
            raise PoolError("没有可用的百度知道账号")
        port, ok, why = probe_clash_detail(stop_event=self.stop_event)
        _check_stop(self.stop_event)
        self._set_proxy_port(port if ok else 0)
        if len(self.slots) >= 2:
            if not ok:
                raise PoolError(f"两个账号采集需要一个账号走代理：{why}")
            order = list(self.slots)
            random.shuffle(order)
            order[0].exit_kind = EXIT_DIRECT
            order[1].exit_kind = EXIT_PROXY
            order[1].pacer.stagger(random.uniform(*FIRST_STAGGER_RANGE))
            self._log(f"出口分配：{order[0].name} 走直连、{order[1].name} 走代理 {port}")
        else:
            self.slots[0].exit_kind = EXIT_DIRECT
        self._started = True
        self._log(f"网络出口：{self.state_text()}")

    def _interruptible_wait(self, seconds: float) -> None:
        deadline = time.monotonic() + max(0.0, float(seconds))
        last_bucket = None
        while True:
            if self.stopped():
                raise CollectCancelled("采集已停止")
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                return
            bucket = math.ceil(remaining / 5)
            if bucket != last_bucket:
                self._status(f"等待请求间隔或账号冷却，约 {math.ceil(remaining)} 秒后继续；可随时停止")
                last_bucket = bucket
            if self.stop_event:
                self.stop_event.wait(min(0.2, remaining))
            else:
                time.sleep(min(0.2, remaining))

    def _sleep(self, seconds: float, chunk: float = 0.2) -> None:
        self._interruptible_wait(seconds)

    def _resume_cooled(self, now: float) -> None:
        for slot in self.slots:
            if slot.flagged and slot.pacer.wait_time(now) <= 0:
                slot.flagged = False
                self._log(f"账号「{slot.name}」冷却结束，恢复参与采集")

    def acquire(self) -> AccountSlot:
        if self.stopped():
            raise CollectCancelled("采集已停止")
        self.start()
        while True:
            if self.stopped():
                raise CollectCancelled("采集已停止")
            now = time.time()
            self._resume_cooled(now)
            ready = [slot for slot in self.slots if now >= slot.pacer.next_at]
            if not ready:
                self._sleep(min(slot.pacer.wait_time(now) for slot in self.slots))
                continue
            slot = min(ready, key=lambda candidate: candidate.pacer.next_at)
            wait = slot.pacer.wait_time(now)
            if wait <= 0:
                return slot
            self._sleep(wait)

    def note_request(self, slot: AccountSlot, read_seconds: float = 0.0) -> float:
        slot.risk_streak = 0
        self.silent_rounds = 0
        long_break, gap = slot.pacer.note_request(read_seconds)
        if long_break:
            self._log(f"账号「{slot.name}」长休息 {gap} 秒")
        return gap

    def _headers(self, slot: AccountSlot, referer: str | None = None) -> dict[str, str]:
        headers = dict(slot.session.headers)
        if referer:
            headers["Referer"] = referer
        return headers

    def fetch(self, slot: AccountSlot, url: str, *, referer: str | None = None,
              timeout: int | None = None, purpose: str = "search") -> str:
        """一次 HTTP 请求；重试由 PooledFetcher 管理，探针仍可单独调用。"""
        if self.stopped():
            raise CollectCancelled("采集已停止")
        self._status(f"账号 {slot.name} 正在请求{'详情页' if purpose == 'detail' else '搜索页'}，等待百度响应…")
        try:
            response = slot.session.get(url, headers=self._headers(slot, referer),
                                        proxies=slot.proxies(self) or {},
                                        timeout=REQUEST_TIMEOUT if timeout is None else timeout)
        except requests.RequestException as exc:
            raise _RequestFailure(f"采集网络异常（账号：{slot.name}）") from exc
        if response.status_code != 200:
            raise _RequestFailure(f"账号「{slot.name}」返回 HTTP {response.status_code}，稍后重试")
        return decode_html_response(response)

    def probe_slot(self, slot: AccountSlot | None) -> bool:
        if slot is None:
            return False
        question = pick_common_question(self._probe_questions)
        self._probe_questions.add(question)
        self._log(f"正在复核账号 {slot.name} 的搜索状态")
        try:
            body = self.fetch(slot, _SEARCH.format(quote(question)), referer="https://zhidao.baidu.com/",
                              timeout=REQUEST_TIMEOUT)
        except CollectCancelled:
            raise
        except (PoolError, requests.RequestException):
            return False
        if is_risk_html(body):
            return False
        items, _ = parse_search_page(body)
        healthy = bool(items) or classify_search_html(body, items) == "empty"
        if healthy:
            self.note_request(slot)
        return healthy

    def probe_healthy(self) -> bool:
        if not self.slots:
            return False
        slot = next((slot for slot in self.slots if slot.pacer.wait_time() <= 0), self.slots[0])
        return self.probe_slot(slot)

    def _usable_slots(self, exclude=None) -> list[AccountSlot]:
        return [slot for slot in self.slots if slot is not exclude
                and not (slot.flagged and slot.pacer.wait_time() > 0)]

    def _ask(self, kind: str, message: str) -> bool:
        if self.pause_ask is None:
            return False
        return bool(self.pause_ask({"kind": kind, "message": message, "reason": message,
                                    "accounts": [slot.name for slot in self.slots]}))

    def _refresh_proxy(self) -> None:
        _check_stop(self.stop_event)
        port, ok, why = probe_clash_detail(stop_event=self.stop_event)
        _check_stop(self.stop_event)
        if not ok:
            raise PoolError(f"未检测到可用 Clash 代理：{why}")
        self._set_proxy_port(port)

    def _reset_risk_state(self) -> None:
        self.silent_rounds = 0
        for slot in self.slots:
            slot.flagged = False
            slot.flag_count = 0
            slot.risk_streak = 0
            slot.reroll_profile()
            slot.pacer.reset()
            slot.pacer.stagger(random.uniform(0.0, FIRST_STAGGER_RANGE[1]))

    def _silent_round(self, slot: AccountSlot, detail: str) -> bool:
        if self.silent_rounds >= RISK_SILENT_ROUNDS:
            return False
        limits = RISK_SILENT_COOLDOWN[min(self.silent_rounds, len(RISK_SILENT_COOLDOWN) - 1)]
        self.silent_rounds += 1
        delay = random.uniform(*limits)
        slot.flagged = True
        slot.risk_streak = 0
        slot.pacer.cool_down(delay)
        round_text = f"第 {self.silent_rounds}/{RISK_SILENT_ROUNDS} 轮"
        self._log(f"{detail}；{round_text}静默冷却 {delay:.0f} 秒后自动重试（不打扰你，已采结果保留）")
        self._status(f"出口被临时限制，{round_text}静默冷却 {delay:.0f} 秒后自动重试…")
        return True

    def on_risk(self, slot: AccountSlot, reason: str = "") -> bool:
        self.cache_generation += 1
        slot.flagged = True
        slot.flag_count += 1
        slot.risk_streak += 1
        streak = slot.risk_streak
        if streak < RISK_ESCALATE_HITS:
            slot.pacer.cool_down(random.uniform(*RISK_SOFT_COOLDOWN))
            self._log(f"账号「{slot.name}」返回{reason or '安全验证页'}（第 {streak} 次，先当作偶发），暂时跳过它，稍后自动重试")
            return True
        if self.probe_healthy():
            slot.risk_streak = 0
            slot.pacer.cool_down(random.uniform(*RISK_SOFT_COOLDOWN))
            self._log(f"账号「{slot.name}」已连续 {streak} 次安全验证页，但健康探针正常 → 判定为偶发，继续采集")
            return True
        slot.risk_streak = 0
        slot.pacer.cool_down(random.uniform(*COOLDOWN_RANGE))
        others = self._usable_slots(exclude=slot)
        if others:
            self._log(f"账号「{slot.name}」连续 {streak} 次安全验证页，先停用它 60–90 秒；继续用{'、'.join(s.name for s in others)}采集，稍后自动重试它")
            return True
        if self.single:
            if self._silent_round(slot, f"账号「{slot.name}」连续 {streak} 次安全验证页、健康探针也异常"):
                return True
            return self._single_risk(slot)
        if any(self.probe_slot(candidate) for candidate in self.slots):
            self._log("有账号已经恢复，继续采集")
            return True
        # The original executable reaches the single-slot handler here, despite
        # its module-level prose describing a different path.
        return self._single_risk(slot)

    def _single_risk(self, slot: AccountSlot) -> bool:
        self.silent_rounds = 0
        if slot.exit_kind == EXIT_DIRECT:
            message = f"百度持续返回安全验证页（不是账号被封，通常是这个出口被临时限制）。请开启 Clash 后继续（账号「{slot.name}」将改用代理出口）；也可以停止任务，已采到的结果会保留。"
            if not self._ask("enable_proxy", message):
                raise CollectCancelled("用户选择停止采集")
            self._refresh_proxy()
            slot.exit_kind = EXIT_PROXY
            self._log(f"账号「{slot.name}」已切换到代理出口")
        else:
            message = "百度持续返回安全验证页（不是账号被封，通常是这个出口被临时限制）。请在 Clash 换一个节点后继续；也可以停止任务，已采到的结果会保留。"
            if not self._ask("risk", message):
                raise CollectCancelled("用户选择停止采集")
            self._refresh_proxy()
        slot.flagged = False
        slot.risk_streak = 0
        slot.reroll_profile()
        slot.pacer.reset()
        return True

    def _both_blocked(self) -> bool:
        message = "百度持续返回安全验证页（不是账号被封，通常是这两个出口都被临时限制）。请在 Clash 换一个节点后继续；也可以停止任务，已采到的结果会保留。"
        if not self._ask("risk", message):
            raise CollectCancelled("用户选择停止采集")
        self._refresh_proxy()
        self._reset_risk_state()
        self._log(f"已按新节点重置账号状态：{self.state_text()}")
        return True

    def on_silent_risk(self, keywords=None, page=False) -> bool:
        self.cache_generation += 1
        detail = "连续多页没有新增结果" if page else "连续多个关键词没有采到任何资源"
        self._log(f"{detail}，先做一次健康探针复核")
        healthy = [slot for slot in self.slots if self.probe_slot(slot)]
        if healthy:
            if len(healthy) < len(self.slots):
                for slot in self.slots:
                    if slot not in healthy:
                        slot.flagged = True
                        slot.risk_streak = 0
                        slot.pacer.cool_down(random.uniform(*COOLDOWN_RANGE))
                self._log(f"探针显示{'、'.join(slot.name for slot in healthy)}可用，先只用它继续采集；其余账号稍后自动重试")
                return True
            self._log("健康探针正常 → 判定为确实没有更多资源，不打扰用户，继续后面的关键词")
            return False
        if len(self.slots) == 1:
            slot = self.slots[0]
            if self._silent_round(slot, f"账号「{slot.name}」连续多页/多个关键词没有内容、健康探针也异常"):
                return True
            return self._single_risk(slot)
        for slot in self.slots:
            slot.flagged = True
            slot.risk_streak = 0
            slot.pacer.cool_down(random.uniform(*COOLDOWN_RANGE))
        return self._both_blocked()


class PooledFetcher:
    MAX_ATTEMPTS = 30

    def __init__(self, pool: SlotPool, stop_event=None) -> None:
        self.pool = pool
        self.stop_event = stop_event

    def stopped(self) -> bool:
        event = self.stop_event if self.stop_event is not None else getattr(self.pool, "stop_event", None)
        return bool(event and event.is_set())

    def _select_slot(self) -> AccountSlot:
        return self.pool.acquire()

    def _pick_slot(self) -> AccountSlot:
        return self._select_slot()

    def get(self, url: str, referer: str | None = None, timeout: int | None = None,
            purpose: str = "search") -> tuple[str, AccountSlot]:
        for _ in range(self.MAX_ATTEMPTS):
            if self.stopped():
                raise CollectCancelled("采集已停止")
            slot = self._pick_slot()
            try:
                body = self.pool.fetch(slot, url, referer=referer, timeout=timeout, purpose=purpose)
            except CollectCancelled:
                raise
            except (PoolError, requests.RequestException) as exc:
                if not isinstance(exc, (_RequestFailure, requests.RequestException)) and not isinstance(exc.__cause__, requests.RequestException):
                    raise
                self.pool._log(f"账号「{slot.name}」请求失败，稍后重试")
                slot.pacer.cool_down(random.uniform(2.0, 6.0))
                continue
            if is_risk_html(body):
                self.pool.on_risk(slot, "安全验证页")
                continue
            self.pool.note_request(slot, random.uniform(*(READ_RANGE if purpose == "search" else PAGE_RANGE)))
            return body, slot
        if self.stopped():
            raise CollectCancelled("采集已停止")
        raise PoolError("连续多次被风控，已停止本次采集")


class PinnedExitFetcher(PooledFetcher):
    """原版固定出口补量：匹配槽直接使用，不经过常规节奏调度。"""

    def __init__(self, pool: SlotPool, exit_kind: str = EXIT_DIRECT, stop_event=None) -> None:
        super().__init__(pool, stop_event)
        self.exit_kind = exit_kind
        self.pin_exit = exit_kind

    def _select_slot(self) -> AccountSlot:
        for slot in getattr(self.pool, "slots", []) or []:
            if getattr(slot, "exit_kind", "") == self.pin_exit:
                return slot
        return self.pool.acquire()
