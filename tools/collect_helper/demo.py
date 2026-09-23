"""离线演示：不联网、不用账号，把一次采集会吐出的事件流原样打印出来。

用途是让人先看清宿主界面将会收到什么，再去接界面。
运行：py -3.12 demo.py
"""

from __future__ import annotations

import sys
import tempfile
import threading
from datetime import date
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import main  # noqa: E402
import protocol  # noqa: E402
from lxds.collect import CollectWorker  # noqa: E402

TODAY = date.today().isoformat()
WORDS = ["云边有个小卖部", "长安的荔枝", "雪中悍刀行"]


def search_page(qid, title):
    return ('<a href="https://zhidao.baidu.com/question/%s.html?fr=search">%s</a>'
            '<span>%s</span>' % (qid, title, TODAY))


def detail_page(surl, pwd):
    return ('<div class="best-text">分享 https://pan.baidu.com/s/%s 提取码：%s</div>'
            % (surl, pwd))


class SampleFetcher:
    """用固定样本冒充百度知道，第三个关键词故意触发一次风控询问。"""

    def __init__(self, on_risk):
        self._on_risk = on_risk
        self._served = 0

    def get(self, url, **kwargs):
        from urllib.parse import parse_qs, urlsplit
        if "/question/" in url:
            qid = url.split("/question/", 1)[1].split(".", 1)[0]
            index = int(qid[0])
            return detail_page("1AbCdE%d" % index, ["8k2m", "7h3n", "2p9q"][index]), None
        query = parse_qs(urlsplit(url).query)
        word = query["word"][0]
        if word not in WORDS:
            return "没有找到相关结果", None
        index = WORDS.index(word)
        if index == 2 and self._served < 3:
            self._served += 1
            self._on_risk()
        return search_page("%d1" % index, "求《%s》百度网盘资源" % word), None


class Pool:
    def __init__(self):
        self.slots = [type("Slot", (), {"exit_kind": "direct"})()]

    def start(self):
        pass

    def on_silent_risk(self, keywords, page):
        return True


class PrintingChannel:
    """把事件打印成人看得懂的样子。"""

    ICONS = {"started": "▶", "item": "✓", "progress": "…", "status": "·",
             "log": " ", "ask": "!", "done": "■", "error": "×"}

    def __init__(self):
        self._lock = threading.Lock()
        self.asked = threading.Event()

    def send(self, event, **fields):
        protocol.encode(event, **fields)
        icon = self.ICONS.get(event, "?")
        with self._lock:
            if event == "item":
                print("  %s 采到  %-14s %s  提取码 %s"
                      % (icon, fields["name"], fields["link"], fields["pwd"]))
            elif event == "progress":
                print("  %s 进度  %d / %d" % (icon, fields["found"], fields["target"]))
            elif event == "started":
                print("  %s 开始  %s 模式，%d 个关键词"
                      % (icon, fields["mode"], fields["keywords"]))
            elif event == "ask":
                print("  %s 风控  %s  ← 此处阻塞，等界面弹窗答复" % (icon, fields["reason"]))
                self.asked.set()
            elif event == "done":
                print("  %s 结束  共 %d 条，被停止=%s，统计=%s"
                      % (icon, fields["found"], fields["stopped"], fields["stats"]))
            elif event in ("status", "log"):
                text = fields.get("text", "")
                if text:
                    print("  %s %s" % (icon, text))
            else:
                print("  %s %s %s" % (icon, event, fields))


def run():
    channel = PrintingChannel()
    risk_wanted = threading.Event()

    def on_risk():
        risk_wanted.set()

    print("=" * 66)
    print("采集事件流演示（离线样本，无账号、无网络）")
    print("=" * 66)

    with tempfile.TemporaryDirectory(prefix="collect-demo-") as temporary:
        def factory(**kwargs):
            kwargs["checkpoint_path"] = str(Path(temporary) / "checkpoint.json")
            return CollectWorker(fetcher=SampleFetcher(on_risk), pool=Pool(), **kwargs)

        session = main.Session(channel, worker_factory=factory)

        def answer_when_asked():
            if channel.asked.wait(20):
                print("  > 界面回答「换节点后继续」")
                session.answer(True)

        threading.Thread(target=answer_when_asked, daemon=True).start()
        session.start(protocol.parse_start({
            "cmd": "start", "mode": "precise", "keywords": WORDS,
            "accounts": [{"name": "知道甲", "cookie": "BDUSS=演示用，不会发出去"}]}))
        session.join(timeout=60)

    print("=" * 66)
    print("以上每一行，界面侧都会收到一条对应的 JSON 事件。")


if __name__ == "__main__":
    run()
