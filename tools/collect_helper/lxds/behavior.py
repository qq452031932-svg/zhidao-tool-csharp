"""采集时的浏览器指纹、账号节奏与常规问题穿插。"""

from __future__ import annotations

import random
import time


def _chrome(version: int, platform: str = "Windows") -> dict[str, str]:
    if platform == "macOS":
        ua = f"Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{version}.0.0.0 Safari/537.36"
    else:
        ua = f"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{version}.0.0.0 Safari/537.36"
    return {
        "ua": ua,
        "sec_ch_ua": f'"Chromium";v="{version}", "Google Chrome";v="{version}", "Not-A.Brand";v="99"',
        "platform": f'"{platform}"',
    }


def _edge(version: int) -> dict[str, str]:
    return {
        "ua": f"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{version}.0.0.0 Safari/537.36 Edg/{version}.0.0.0",
        "sec_ch_ua": f'"Chromium";v="{version}", "Microsoft Edge";v="{version}", "Not-A.Brand";v="99"',
        "platform": '"Windows"',
    }


_PROFILES = (
    _chrome(124), _chrome(126), _chrome(128), _chrome(130),
    _edge(126), _edge(129), _chrome(127, "macOS"),
    {
        "ua": "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:128.0) Gecko/20100101 Firefox/128.0",
        "platform": '"Windows"',
    },
)
_COMMON_QUESTIONS = (
    "今天星期几？", "如何提高阅读效率？", "家用电器如何清洁？", "怎样保持规律作息？",
    "常见的学习方法有哪些？", "周末适合做什么运动？", "如何整理书桌？", "每天喝多少水合适？",
)


def pick_profile() -> dict:
    """返回一套 UA 与 Client Hint 配对的浏览器指纹。"""
    return dict(random.choice(_PROFILES))


def pick_common_question(used: set) -> str:
    """轮换常规问题；用完后开启下一轮。"""
    available = [question for question in _COMMON_QUESTIONS if question not in used]
    if not available:
        used.clear()
        available = list(_COMMON_QUESTIONS)
    question = random.choice(available)
    used.add(question)
    return question


def shuffle_blocks(items: list, size: int) -> list:
    """仅在每个块内部洗牌，保留块间总体顺序。"""
    if size <= 0:
        raise ValueError("块大小必须大于零")
    output = []
    for offset in range(0, len(items), size):
        block = list(items[offset:offset + size])
        random.shuffle(block)
        output.extend(block)
    return output


def pick_every(rng_range: tuple[int, int]) -> int:
    return random.randint(*rng_range)


class HumanPacer:
    """记录单账号下一次可请求时间及可选周期长休息。"""

    def __init__(
        self,
        gap_range: tuple[float, float] = (6, 10),
        break_after_range: tuple[int, int] | None = (8, 12),
        break_range: tuple[float, float] | None = (12.0, 25.0),
    ) -> None:
        if (break_after_range is None) != (break_range is None):
            raise ValueError("长休息间隔和时长需要同时指定")
        self.gap_range = gap_range
        self.break_after_range = break_after_range
        self.break_range = break_range
        self._next_break = pick_every(break_after_range) if break_after_range else None
        self._requests = 0
        self._next_ready = 0.0
        self._cool_until = 0.0

    def wait_time(self, now: float) -> float:
        return max(0.0, self._next_ready - now, self._cool_until - now)

    def note_request(self, read_seconds: float) -> tuple[bool, float]:
        """安排 6–10 秒账号间隔；若到长休息轮次，返回 `(True, 秒数)`。"""
        now = time.monotonic()
        self._next_ready = now + random.uniform(*self.gap_range)
        self._requests += 1
        if self._next_break is not None and self._requests >= self._next_break:
            duration = random.uniform(*self.break_range)
            self._cool_until = max(self._cool_until, now + duration)
            self._requests = 0
            self._next_break = pick_every(self.break_after_range)
            return True, duration
        return False, 0.0

    def cool_down(self, seconds: float) -> None:
        self._cool_until = max(self._cool_until, time.monotonic() + seconds)

    def stagger(self, seconds: float) -> None:
        self._next_ready = max(self._next_ready, time.monotonic() + seconds)

    def reset(self) -> None:
        self._next_ready = 0.0
        self._cool_until = 0.0
        self._requests = 0
        self._next_break = pick_every(self.break_after_range) if self.break_after_range else None
