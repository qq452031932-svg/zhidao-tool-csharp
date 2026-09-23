"""双账号双出口的百度知道采集请求池与分级风控处理。"""

from __future__ import annotations

import os
import random
import time
from urllib.parse import quote

import requests

from lxds.behavior import HumanPacer, pick_common_question, pick_profile
from lxds.zhidao_parse import classify_search_html, parse_search_page


_SEARCH = "https://zhidao.baidu.com/search?lm=0&rn=10&pn=0&fr=search&dyTabStr=null&word={}"
_PROXY_PORTS = (7890, 7891, 7897)
_RISK_MARKERS = ("百度安全验证", "安全验证", "wappass", "请输入验证码")


class PoolError(Exception):
    """采集请求池异常。"""


class CollectCancelled(PoolError):
    """采集已由用户停止。"""


def is_risk_html(html: str) -> bool:
    """短页面且含验证关键词才当作风控，避免误判普通结果。"""
    return len(html) < 60000 and any(marker in html for marker in _RISK_MARKERS)


class AccountSlot:
    """一个 Cookie、一个固定出口和一套独立指纹/请求节奏。"""

    def __init__(self, name: str, cookie: str, exit_kind: str) -> None:
        self.name = name
        self.cookie = cookie
        self.exit_kind = exit_kind
        self.profile = pick_profile()
        self.pacer = HumanPacer()
        self.session = requests.Session()
        self.session.trust_env = False
        self.risk_hits = 0
        self.risk_confirmed = False
        self.unavailable_until = 0.0

    def reroll_profile(self) -> None:
        self.profile = pick_profile()
        self.pacer.reset()

    def proxies(self, pool: "SlotPool") -> dict:
        if self.exit_kind == "direct":
            return {}
        url = pool.proxy_url
        return {"http": url, "https": url}


class SlotPool:
    """按账号轮转请求，只在所有账号都被确认限制时打扰用户。"""

    def __init__(self, accounts, pause_ask, on_log, stop_event, on_status) -> None:
        self.accounts = accounts
        self.pause_ask = pause_ask
        self.on_log = on_log
        self.stop_event = stop_event
        self.on_status = on_status
        self.slots: list[AccountSlot] = []
        self._next_index = 0
        self._used_common_questions: set[str] = set()
        self.proxy_url = os.environ.get("LXDS_CLASH_PROXY", f"http://127.0.0.1:{_PROXY_PORTS[0]}")

    def start(self) -> None:
        if self.slots:
            return
        if not self.accounts:
            raise PoolError("请先配置至少一个采集账号")
        for index, account in enumerate(self.accounts):
            exit_kind = account.get("exit_kind") or ("direct" if index == 0 else "proxy")
            self.slots.append(AccountSlot(account["name"], account["cookie"], exit_kind))

    def _interruptible_wait(self, seconds: float) -> None:
        deadline = time.monotonic() + max(0.0, seconds)
        while True:
            if self.stop_event.is_set():
                raise CollectCancelled("采集已停止")
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                return
            self.stop_event.wait(min(0.2, remaining))

    def acquire(self) -> AccountSlot:
        """取一个已过冷却期的账号；没有可用槽时可中断等待。"""
        self.start()
        while True:
            if self.stop_event.is_set():
                raise CollectCancelled("采集已停止")
            now = time.monotonic()
            for offset in range(len(self.slots)):
                index = (self._next_index + offset) % len(self.slots)
                slot = self.slots[index]
                if slot.unavailable_until <= now and slot.pacer.wait_time(now) <= 0:
                    self._next_index = (index + 1) % len(self.slots)
                    return slot
            delays = [max(slot.unavailable_until - now, slot.pacer.wait_time(now)) for slot in self.slots]
            self._interruptible_wait(min(max(0.0, delay) for delay in delays))

    def note_request(self, slot: AccountSlot, read_seconds: float) -> None:
        slot.risk_hits = 0
        slot.risk_confirmed = False
        slot.pacer.note_request(read_seconds)

    def _headers(self, slot: AccountSlot, referer: str | None = None) -> dict[str, str]:
        headers = {
            "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8",
            "Accept-Language": "zh-CN,zh;q=0.9",
            "Connection": "keep-alive",
            "Referer": referer or "https://zhidao.baidu.com/",
            "Sec-Fetch-Dest": "document",
            "Sec-Fetch-Mode": "navigate",
            "Sec-Fetch-Site": "same-origin",
            "sec-ch-ua-mobile": "?0",
            "sec-ch-ua-platform": slot.profile["platform"],
            "User-Agent": slot.profile["ua"],
            "Cookie": slot.cookie,
        }
        if "sec_ch_ua" in slot.profile:
            headers["sec-ch-ua"] = slot.profile["sec_ch_ua"]
        return headers

    def fetch(self, slot: AccountSlot, url: str, *, referer: str | None = None, timeout: int | None = None, purpose: str = "search") -> str:
        try:
            response = slot.session.get(
                url, headers=self._headers(slot, referer),
                proxies=slot.proxies(self), timeout=timeout or 30,
            )
        except requests.RequestException as exc:
            raise PoolError(f"采集网络异常（账号：{slot.name}）") from exc
        return response.text

    def probe_slot(self, slot: AccountSlot) -> bool:
        """用与当前资源无关的常规问题验证账号是否仍能搜索。"""
        question = pick_common_question(self._used_common_questions)
        try:
            body = self.fetch(slot, _SEARCH.format(quote(question)))
        except PoolError:
            return False
        if is_risk_html(body):
            return False
        items, _ = parse_search_page(body)
        return classify_search_html(body, items) != "unknown"

    def _ask_when_all_blocked(self, reason: str) -> bool:
        if not all(slot.risk_confirmed for slot in self.slots):
            return True
        self.on_status("所有采集账号都遇到安全验证")
        proceed = bool(self.pause_ask({"reason": reason, "accounts": [slot.name for slot in self.slots]}))
        if not proceed:
            raise CollectCancelled("用户选择停止采集")
        for slot in self.slots:
            # 用户换节点后只清状态与指纹，保留原来的出口类型。
            slot.risk_hits = 0
            slot.risk_confirmed = False
            slot.unavailable_until = 0.0
            slot.reroll_profile()
        return True

    def on_risk(self, slot: AccountSlot, reason: str) -> bool:
        """偶发命中静默降速；三连击才探针，必要时单号三轮冷却。"""
        slot.risk_hits += 1
        if slot.risk_hits < 3:
            slot.pacer.cool_down(random.uniform(10, 20))
            self.on_log(f"账号 {slot.name} 遇到验证页，已静默降速")
            return True
        slot.risk_hits = 0
        if self.probe_slot(slot):
            slot.risk_confirmed = False
            slot.pacer.cool_down(random.uniform(10, 20))
            return True
        slot.risk_confirmed = True
        if len(self.slots) > 1:
            slot.unavailable_until = time.monotonic() + random.uniform(60, 90)
            self.on_log(f"账号 {slot.name} 已静默冷却，其它账号继续采集")
            return self._ask_when_all_blocked(reason)
        for lower, upper in ((60, 90), (90, 150), (120, 180)):
            self._interruptible_wait(random.uniform(lower, upper))
            if self.probe_slot(slot):
                slot.risk_confirmed = False
                return True
        return self._ask_when_all_blocked(reason)

    def on_silent_risk(self, keywords, page) -> bool:
        """连续空结果先探针，不把确实没有资源误报为风控。"""
        for slot in self.slots:
            if self.probe_slot(slot):
                slot.risk_confirmed = False
                return True
            slot.risk_confirmed = True
        if len(self.slots) == 1:
            slot = self.slots[0]
            for lower, upper in ((60, 90), (90, 150), (120, 180)):
                self._interruptible_wait(random.uniform(lower, upper))
                if self.probe_slot(slot):
                    slot.risk_confirmed = False
                    return True
        return self._ask_when_all_blocked("连续无内容")


class PooledFetcher:
    def __init__(self, pool: SlotPool) -> None:
        self.pool = pool

    def _select_slot(self) -> AccountSlot:
        return self.pool.acquire()

    def get(self, url: str, referer: str | None = None, timeout: int | None = None, purpose: str = "search") -> tuple[str, AccountSlot]:
        slot = self._select_slot()
        body = self.pool.fetch(slot, url, referer=referer, timeout=timeout, purpose=purpose)
        if is_risk_html(body):
            self.pool.on_risk(slot, "安全验证页")
        else:
            self.pool.note_request(slot, 0)
        return body, slot


class PinnedExitFetcher(PooledFetcher):
    """优先固定出口，指定出口不存在时回退池默认分配。"""

    def __init__(self, pool: SlotPool, exit_kind: str = "direct") -> None:
        super().__init__(pool)
        self.exit_kind = exit_kind

    def _select_slot(self) -> AccountSlot:
        now = time.monotonic()
        for slot in self.pool.slots:
            if slot.exit_kind == self.exit_kind and slot.unavailable_until <= now and slot.pacer.wait_time(now) <= 0:
                return slot
        return self.pool.acquire()
