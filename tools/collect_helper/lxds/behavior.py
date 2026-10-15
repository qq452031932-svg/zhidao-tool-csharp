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
        "mobile": "?0",
        "platform": f'"{platform}"',
    }


def _edge(version: int) -> dict[str, str]:
    return {
        "ua": f"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{version}.0.0.0 Safari/537.36 Edg/{version}.0.0.0",
        "sec_ch_ua": f'"Chromium";v="{version}", "Microsoft Edge";v="{version}", "Not-A.Brand";v="99"',
        "mobile": "?0",
        "platform": '"Windows"',
    }


_PROFILES = (
    _chrome(124), _chrome(126), _chrome(128), _chrome(130),
    _edge(126), _edge(129), _chrome(127, "macOS"),
    {
        "ua": "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:128.0) Gecko/20100101 Firefox/128.0",
        "sec_ch_ua": "", "mobile": "", "platform": "",
    },
)
_COMMON_QUESTIONS = (
    "明天天气怎么样", "红烧肉怎么做才好吃", "厨房下水道堵了怎么办", "手机充电越来越慢是什么原因",
    "怎么把微信聊天记录迁移到新手机", "个人所得税怎么申报", "感冒了吃什么水果好", "猫一直掉毛正常吗",
    "绿萝叶子发黄怎么办", "汽车胎压多少合适", "高铁上可以带充电宝吗", "怎么去除衣服上的油渍",
    "晚上失眠有什么办法", "电动车电池冬天掉电快吗", "路由器信号差怎么解决", "煮鸡蛋几分钟最合适",
    "洗衣机洗完有异味怎么办", "离职后社保怎么处理", "阳台适合种什么花", "电脑风扇声音大怎么处理",
    "银行卡被冻结了怎么办", "近视眼怎么慢慢恢复视力", "租房签合同注意什么", "冰箱冷藏室结冰怎么办",
    "怎么判断西瓜熟了没有", "新买的铁锅怎么开锅", "空调外机噪音大怎么解决", "应届生怎么准备面试",
    "肩膀酸痛怎么缓解", "快递一直不更新物流怎么办",
)
BROWSER_PROFILES = _PROFILES
COMMON_QUESTIONS = _COMMON_QUESTIONS
GAP_RANGE = (6.0, 10.0)
BREAK_AFTER_RANGE = (8, 12)
BREAK_RANGE = (12.0, 25.0)
READ_RANGE = (3.0, 6.0)
PAGE_RANGE = (2.5, 6.0)
COOLDOWN_RANGE = (60.0, 90.0)
RISK_SOFT_COOLDOWN = (10.0, 20.0)
RISK_ESCALATE_HITS = 3
RISK_SILENT_ROUNDS = 3
RISK_SILENT_COOLDOWN = ((60.0, 90.0), (90.0, 150.0), (120.0, 180.0))
FILLER_EVERY_RANGE = (8, 12)
OPEN_EVERY_RANGE = (8, 12)
FIRST_STAGGER_RANGE = (3.0, 8.0)
# 多个账号共用同一网络出口：任意两次请求之间至少隔这么久，避免几个账号挤在一起连发。
# 单账号间隔 6–10 秒不受影响；10 个账号时整体约每 4 秒一次，约为单账号的两倍速度。
GLOBAL_GAP_RANGE = (3.0, 5.0)


def pick_profile() -> dict:
    """返回一套 UA 与 Client Hint 配对的浏览器指纹。"""
    return dict(random.choice(_PROFILES))


def pick_common_question(used: set | None = None) -> str:
    """选择未用问题；记录由调用方维护，用尽后从全部问题选择。"""
    available = [question for question in _COMMON_QUESTIONS if not used or question not in used]
    return random.choice(available or list(_COMMON_QUESTIONS))


def shuffle_blocks(items: list, size: int = 10) -> list:
    """仅在每个块内部洗牌，保留块间总体顺序。"""
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
        self.reset()

    def wait_time(self, now: float | None = None) -> float:
        return max(0.0, self.next_at - (time.time() if now is None else now))

    def note_request(self, read_seconds: float = 0.0) -> tuple[bool, float]:
        """阅读时间扩展间隔；周期长休息按原版覆盖本次间隔。"""
        self.total += 1
        self.count_since_break += 1
        gap = random.uniform(*self.gap_range)
        if read_seconds:
            gap = max(gap, float(read_seconds))
        long_break = False
        if self.next_break_after is not None and self.count_since_break >= self.next_break_after:
            gap = random.uniform(*self.break_range)
            self.count_since_break = 0
            self.next_break_after = pick_every(self.break_after_range)
            long_break = True
        self.next_at = time.time() + gap
        self.in_break = long_break
        return long_break, round(gap, 1)

    def cool_down(self, seconds: float) -> None:
        self.next_at = max(self.next_at, time.time() + float(seconds))
        self.in_break = False

    def stagger(self, seconds: float) -> None:
        self.next_at = max(self.next_at, time.time() + float(seconds))

    def reset(self) -> None:
        self.next_at = 0.0
        self.in_break = False
        self.count_since_break = 0
        self.next_break_after = pick_every(self.break_after_range) if self.break_after_range else None
        self.total = 0
