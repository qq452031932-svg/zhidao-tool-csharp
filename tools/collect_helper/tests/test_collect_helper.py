"""采集助手的离线契约测试。不联网、不用账号、不起浏览器。

采集逻辑本身由 lxds 自带的测试覆盖；这里只验证助手该负责的三件事：
协议帧的校验、回调到事件的翻译、以及 stop / ask 的收放。
"""

import json
import pathlib
import subprocess
import sys
import threading
import time
import unittest
from pathlib import Path

HELPER_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HELPER_ROOT))

import main  # noqa: E402
import protocol  # noqa: E402


class RecordingChannel:
    """把事件收进列表，替代真实 stdout。"""

    def __init__(self):
        self.events = []
        self._lock = threading.Lock()

    def send(self, event, **fields):
        protocol.encode(event, **fields)  # 顺带校验事件名合法
        with self._lock:
            self.events.append((event, fields))

    def names(self):
        with self._lock:
            return [name for name, _ in self.events]

    def first(self, event):
        with self._lock:
            for name, fields in self.events:
                if name == event:
                    return fields
        return None

    def all(self, event):
        with self._lock:
            return [fields for name, fields in self.events if name == event]


class ScriptedWorker:
    """按脚本回放回调的假采集器，接口与 CollectWorker 对齐。"""

    def __init__(self, **kwargs):
        self.kwargs = kwargs
        self.on_item = kwargs["on_item"]
        self.on_progress = kwargs["on_progress"]
        self.on_status = kwargs["on_status"]
        self.on_log = kwargs["on_log"]
        self.pause_ask = kwargs["pause_ask"]
        self.stopped = False
        self.ask_answer = None
        self.raise_error = None
        self.wait_before_finish = None

    def stop(self):
        self.stopped = True

    def run(self):
        if self.raise_error is not None:
            raise self.raise_error
        self.on_status("开始采集")
        self.on_item({"资源名称": "云边有个小卖部", "资源链接": "https://pan.baidu.com/s/1Ab?pwd=8k2m",
                      "提取码": "8k2m", "来源链接": "https://zhidao.baidu.com/question/1.html"})
        self.on_progress({"found": 1, "target": 2})
        self.on_log("跳过重复链接：x")
        if self.ask_answer is None:
            self.ask_answer = self.pause_ask({"reason": "采集账号受限"})
        if self.wait_before_finish is not None:
            self.wait_before_finish.wait(5)
        return {"items": [], "found": 1, "stats": {"found": 1, "no_pwd": 0},
                "stopped": self.stopped}


class ProtocolTests(unittest.TestCase):
    def test_encode_rejects_unknown_event(self):
        with self.assertRaises(protocol.ProtocolError):
            protocol.encode("nonsense")

    def test_encode_keeps_chinese_readable(self):
        line = protocol.encode("status", text="时间范围自动放宽到一年")
        self.assertIn("时间范围", line)
        self.assertEqual("时间范围自动放宽到一年", json.loads(line)["text"])

    def test_decode_rejects_malformed_and_unknown(self):
        for bad in ("不是 JSON", "[]", json.dumps({"cmd": "launch"}), json.dumps({})):
            with self.assertRaises(protocol.ProtocolError):
                protocol.decode(bad)

    def test_decode_rejects_oversized_frame(self):
        huge = json.dumps({"cmd": "start", "pad": "x" * (protocol.MAX_FRAME_BYTES + 10)})
        with self.assertRaises(protocol.ProtocolError):
            protocol.decode(huge)

    def test_parse_start_requires_mode_keywords_and_account(self):
        base = {"cmd": "start", "mode": "precise", "keywords": ["甲"],
                "accounts": [{"name": "知道甲", "cookie": "BDUSS=x"}]}
        self.assertEqual("precise", protocol.parse_start(base)["mode"])
        for mutate in (
            lambda f: f.update(mode="turbo"),
            lambda f: f.update(keywords=[]),
            lambda f: f.update(keywords=["  ", ""]),
            lambda f: f.update(accounts=[]),
            lambda f: f.update(accounts=[{"name": "无凭据"}]),
            lambda f: f.update(count=-1),
        ):
            frame = dict(base)
            frame["accounts"] = [dict(a) for a in base["accounts"]]
            mutate(frame)
            with self.assertRaises(protocol.ProtocolError):
                protocol.parse_start(frame)

    def test_fuzzy_mode_requires_a_positive_count(self):
        frame = {"cmd": "start", "mode": "fuzzy", "keywords": ["小说"],
                 "accounts": [{"cookie": "BDUSS=x"}]}
        with self.assertRaises(protocol.ProtocolError):
            protocol.parse_start(frame)
        frame["count"] = 5
        self.assertEqual(5, protocol.parse_start(frame)["count"])

    def test_parse_start_names_unnamed_accounts_and_trims_keywords(self):
        options = protocol.parse_start({
            "cmd": "start", "mode": "precise", "keywords": ["  甲  ", "乙", "  "],
            "accounts": [{"cookie": "BDUSS=x"}]})
        self.assertEqual(["甲", "乙"], options["keywords"])
        self.assertEqual("账号1", options["accounts"][0]["name"])


class KeywordFileTests(unittest.TestCase):
    """关键词文件的读取契约。中文 txt 的编码是这里最容易翻车的地方。"""

    def _write(self, payload, encoding):
        import tempfile
        handle = tempfile.NamedTemporaryFile(prefix="kw-", suffix=".txt", delete=False)
        handle.write(payload.encode(encoding) if isinstance(payload, str) else payload)
        handle.close()
        self.addCleanup(lambda: pathlib.Path(handle.name).unlink(missing_ok=True))
        return handle.name

    def test_reads_utf8_with_and_without_bom(self):
        for encoding in ("utf-8", "utf-8-sig"):
            path = self._write("云边有个小卖部\n长安的荔枝\n", encoding)
            result = protocol.read_keyword_file(path)
            self.assertEqual(["云边有个小卖部", "长安的荔枝"], result["items"])
            self.assertEqual("utf-8", result["encoding"])

    def test_reads_legacy_gb18030_file(self):
        path = self._write("雪中悍刀行\n三体\n", "gb18030")
        result = protocol.read_keyword_file(path)
        self.assertEqual(["雪中悍刀行", "三体"], result["items"])
        self.assertEqual("gb18030", result["encoding"])

    def test_trims_blank_lines_and_deduplicates(self):
        path = self._write("  甲  \n\n乙\n甲\n   \n", "utf-8")
        result = protocol.read_keyword_file(path)
        self.assertEqual(["甲", "乙"], result["items"])
        self.assertEqual(5, result["lines"])

    def test_caps_the_keyword_count(self):
        path = self._write("\n".join("词%d" % i for i in range(protocol.MAX_KEYWORDS + 50)), "utf-8")
        result = protocol.read_keyword_file(path)
        self.assertEqual(protocol.MAX_KEYWORDS, len(result["items"]))
        self.assertTrue(result["truncated"])

    def test_rejects_missing_file(self):
        with self.assertRaises(protocol.ProtocolError):
            protocol.read_keyword_file("这个文件不存在.txt")

    def test_rejects_oversized_file(self):
        path = self._write(b"x" * (protocol.MAX_KEYWORD_FILE_BYTES + 1), None)
        with self.assertRaises(protocol.ProtocolError):
            protocol.read_keyword_file(path)

    def test_command_requires_a_path(self):
        with self.assertRaises(protocol.ProtocolError):
            protocol.parse_read_keywords({"cmd": "read_keywords"})
        self.assertEqual("a.txt", protocol.parse_read_keywords({"cmd": "read_keywords", "path": "a.txt"}))


class SessionTests(unittest.TestCase):
    def _options(self, **overrides):
        options = protocol.parse_start({
            "cmd": "start", "mode": "precise", "keywords": ["云边有个小卖部"],
            "count": 2, "accounts": [{"name": "知道甲", "cookie": "BDUSS=x"}]})
        options.update(overrides)
        return options

    def _run_session(self, configure=None):
        channel = RecordingChannel()
        created = {}

        def factory(**kwargs):
            worker = ScriptedWorker(**kwargs)
            created["worker"] = worker
            if configure is not None:
                configure(worker, session)
            return worker

        session = main.Session(channel, worker_factory=factory)
        session.start(self._options())
        return channel, session, created

    def test_callbacks_are_translated_into_events(self):
        channel, session, created = self._run_session(
            configure=lambda worker, s: threading.Timer(0.05, s.answer, args=(True,)).start())
        session.join(timeout=10)
        self.assertIn("started", channel.names())

        item = channel.first("item")
        self.assertEqual("云边有个小卖部", item["name"])
        self.assertEqual("8k2m", item["pwd"])
        self.assertEqual("https://pan.baidu.com/s/1Ab?pwd=8k2m", item["link"])

        self.assertEqual({"found": 1, "target": 2}, channel.first("progress"))
        self.assertEqual("开始采集", channel.first("status")["text"])
        self.assertIn("跳过重复链接", channel.first("log")["text"])

        done = channel.first("done")
        self.assertEqual(1, done["found"])
        self.assertFalse(done["stopped"])
        self.assertEqual({"found": 1, "no_pwd": 0}, done["stats"])

    def test_started_is_emitted_before_any_collection_event(self):
        channel, session, created = self._run_session(
            configure=lambda worker, s: threading.Timer(0.05, s.answer, args=(True,)).start())
        session.join(timeout=10)
        names = channel.names()
        self.assertEqual("started", names[0],
                         "started 必须先发，否则界面会在未开始状态下收到数据")

    def test_precise_progress_falls_back_to_keyword_count_as_denominator(self):
        channel = RecordingChannel()
        session = main.Session(channel, worker_factory=ScriptedWorker)
        options = protocol.parse_start({
            "cmd": "start", "mode": "precise", "keywords": ["甲", "乙", "丙"],
            "accounts": [{"cookie": "BDUSS=x"}]})
        self.assertEqual(0, options["count"], "精准模式本就不限总量")
        session.answer(True)
        session.start(options)
        session.join(timeout=10)
        self.assertEqual(3, channel.first("started")["target"])
        for progress in channel.all("progress"):
            self.assertNotEqual(0, progress["target"], "分母为 0 会让界面画不出进度")

    def test_ask_blocks_until_the_host_answers(self):
        channel, session, created = self._run_session()
        for _ in range(100):
            if channel.first("ask") is not None:
                break
            time.sleep(0.02)
        self.assertIsNotNone(channel.first("ask"), "应当先发出 ask")
        self.assertEqual("采集账号受限", channel.first("ask")["reason"])
        self.assertIsNone(channel.first("done"), "未答复前不应结束")

        session.answer(True)
        session.join(timeout=10)
        self.assertTrue(created["worker"].ask_answer, "宿主答复应原样传回采集器")
        self.assertIsNotNone(channel.first("done"))

    def test_stop_releases_a_worker_waiting_on_ask(self):
        channel, session, created = self._run_session()
        for _ in range(100):
            if channel.first("ask") is not None:
                break
            time.sleep(0.02)
        session.stop()
        session.join(timeout=10)
        self.assertTrue(created["worker"].stopped, "stop 应传达到采集器")
        self.assertFalse(created["worker"].ask_answer, "停止时不应答复继续")
        self.assertIsNotNone(channel.first("done"), "停止后仍须给出 done")

    def test_worker_crash_is_reported_without_killing_the_session(self):
        channel = RecordingChannel()

        def factory(**kwargs):
            worker = ScriptedWorker(**kwargs)
            worker.raise_error = RuntimeError("网络炸了")
            return worker

        session = main.Session(channel, worker_factory=factory)
        session.start(self._options())
        session.join(timeout=10)
        self.assertIn("网络炸了", channel.first("error")["message"])
        self.assertTrue(channel.first("done")["stopped"])
        self.assertFalse(session.running)

    def test_stop_just_before_risk_prompt_is_not_lost(self):
        def configure(worker, session):
            pause_ask = worker.pause_ask

            def stop_before_asking(payload):
                session.stop()
                return pause_ask(payload)

            worker.pause_ask = stop_before_asking

        channel, session, created = self._run_session(configure=configure)
        try:
            session.join(timeout=1)
            self.assertFalse(session.running, "停止信号不能在进入风控等待时被清掉")
            self.assertFalse(created["worker"].ask_answer)
            self.assertTrue(channel.first("done")["stopped"])
            self.assertIsNone(channel.first("ask"), "已停止后不应再等用户答复")
        finally:
            session.stop()
            session.join(timeout=2)

    def test_second_start_while_running_is_rejected(self):
        gate = threading.Event()

        def configure(worker, session):
            worker.ask_answer = False
            worker.wait_before_finish = gate

        channel, session, created = self._run_session(configure=configure)
        try:
            with self.assertRaises(protocol.ProtocolError):
                session.start(self._options())
        finally:
            gate.set()
            session.join(timeout=10)


class ProcessTests(unittest.TestCase):
    """真正把助手当子进程驱动一遍，验证信道本身。"""

    def _launch(self):
        return subprocess.Popen(
            [sys.executable, str(HELPER_ROOT / "main.py")],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8", bufsize=1)

    def test_handshake_validation_and_clean_exit(self):
        process = self._launch()
        try:
            ready = json.loads(process.stdout.readline())
            self.assertEqual("ready", ready["ev"])
            self.assertEqual(protocol.PROTOCOL_VERSION, ready["protocolVersion"])

            process.stdin.write(json.dumps({"cmd": "ping"}) + "\n")
            process.stdin.flush()
            self.assertEqual("pong", json.loads(process.stdout.readline())["ev"])

            process.stdin.write("这不是 JSON\n")
            process.stdin.flush()
            self.assertEqual("error", json.loads(process.stdout.readline())["ev"])

            process.stdin.write(json.dumps({"cmd": "start", "mode": "precise"}) + "\n")
            process.stdin.flush()
            bad = json.loads(process.stdout.readline())
            self.assertEqual("error", bad["ev"])
            self.assertIn("keywords", bad["message"])

            process.stdin.close()
            self.assertEqual(0, process.wait(timeout=30))
            self.assertEqual("", process.stderr.read().strip())
        finally:
            if process.poll() is None:
                process.kill()

    def test_chinese_survives_the_channel_regardless_of_console_encoding(self):
        process = self._launch()
        try:
            json.loads(process.stdout.readline())
            process.stdin.write(json.dumps(
                {"cmd": "start", "mode": "precise", "keywords": ["云边有个小卖部"]}) + "\n")
            process.stdin.flush()
            reply = json.loads(process.stdout.readline())
            self.assertEqual("error", reply["ev"])
            self.assertIn("账号", reply["message"])  # 中文往返未被控制台代码页破坏
            process.stdin.close()
            process.wait(timeout=30)
        finally:
            if process.poll() is None:
                process.kill()


class RealWorkerIntegrationTests(unittest.TestCase):
    """用真实 CollectWorker 走一遍，只把网络换成固定样本。

    假采集器验证不了「助手和真采集器的接口是否对得上」，这组测试补这个洞。
    """

    def test_real_worker_reports_activity_before_waiting_for_the_first_network_response(self):
        import tempfile
        from lxds.collect import CollectWorker

        entered = threading.Event()
        release = threading.Event()
        channel = RecordingChannel()

        class WaitingFetcher:
            def get(self, url, **kwargs):
                entered.set()
                release.wait(5)
                return "没有找到相关结果", None

        with tempfile.TemporaryDirectory(prefix="collect-progress-") as temporary:
            def factory(**kwargs):
                kwargs["checkpoint_path"] = str(Path(temporary) / "checkpoint.json")
                return CollectWorker(fetcher=WaitingFetcher(), pool=self._Pool(), **kwargs)

            session = main.Session(channel, worker_factory=factory)
            session.start(protocol.parse_start({
                "cmd": "start", "mode": "precise", "keywords": ["测试关键词"],
                "accounts": [{"name": "测试账号", "cookie": "BDUSS=test-cookie"}]}))
            try:
                self.assertTrue(entered.wait(2))
                self.assertIsNotNone(channel.first("status"), "首个请求返回前也必须报告当前步骤")
                self.assertTrue(any("测试关键词" in event["text"] for event in channel.all("status")))
                self.assertTrue(any("第 1 页" in event["text"] for event in channel.all("status")))
            finally:
                session.stop()
                release.set()
                session.join(timeout=5)

    def test_cooldown_reports_waiting_and_can_be_stopped(self):
        from lxds.pool import SlotPool, CollectCancelled

        stop = threading.Event()
        statuses = []

        def status(text):
            statuses.append(text)
            stop.set()

        pool = SlotPool([], None, lambda text: None, stop, status)
        with self.assertRaises(CollectCancelled):
            pool._interruptible_wait(60)
        self.assertTrue(any("冷却" in text and "秒" in text for text in statuses))

    @staticmethod
    def _page(items=(), next_pn=None):
        chunks = []
        for qid, title, when in items:
            chunks.append(
                '<a href="https://zhidao.baidu.com/question/%s.html?fr=search">%s</a>'
                '<span>%s</span>' % (qid, title, when))
        if next_pn is not None:
            chunks.append('<a class="pager-next" href="/search?word=x&pn=%d">下一页</a>' % next_pn)
        return "".join(chunks) if chunks else "没有找到相关结果"

    @staticmethod
    def _detail(link, pwd="8k2m"):
        return ('<div class="best-text">分享 https://pan.baidu.com/s/%s 提取码：%s</div>'
                % (link, pwd)) + '<!--' + 'x' * 2100 + '-->'

    class _Pool:
        def __init__(self):
            self.slots = [type("Slot", (), {"exit_kind": "direct"})()]

        def start(self):
            pass

        def on_silent_risk(self, keywords, page):
            return True

    class _Fetcher:
        def __init__(self, pages, details):
            self.pages, self.details = pages, details

        def get(self, url, **kwargs):
            from urllib.parse import parse_qs, urlsplit
            if "/question/" in url:
                qid = url.split("/question/", 1)[1].split(".", 1)[0]
                return self.details.get(qid, ""), None
            query = parse_qs(urlsplit(url).query)
            word, pn = query["word"][0], int(query["pn"][0])
            return self.pages.get((word, pn), RealWorkerIntegrationTests._page()), None

    def test_real_worker_drives_the_full_event_stream(self):
        import tempfile
        from datetime import date
        from lxds.collect import CollectWorker

        today = date.today().isoformat()
        words = ["云边有个小卖部", "长安的荔枝"]
        pages = {("求%s百度网盘链接" % word, 0): self._page([("%d1" % index, "求《%s》百度网盘链接" % word, today)])
                 for index, word in enumerate(words)}
        details = {"%d1" % index: self._detail("1Abfixture%d" % index) for index in range(len(words))}
        fetcher = self._Fetcher(pages, details)

        channel = RecordingChannel()
        with tempfile.TemporaryDirectory(prefix="collect-helper-") as temporary:
            def factory(**kwargs):
                kwargs["checkpoint_path"] = str(Path(temporary) / "checkpoint.json")
                return CollectWorker(fetcher=fetcher, pool=self._Pool(), **kwargs)

            session = main.Session(channel, worker_factory=factory)
            session.start(protocol.parse_start({
                "cmd": "start", "mode": "precise", "keywords": words,
                "accounts": [{"name": "知道甲", "cookie": "BDUSS=x"}]}))
            session.join(timeout=30)

        self.assertFalse(session.running, "采集应已结束")
        self.assertIsNone(channel.first("error"), "不应出现错误事件")

        items = channel.all("item")
        self.assertEqual(len(words), len(items), "两个关键词各应采到一条")
        self.assertCountEqual(words, [item["name"] for item in items])
        for item in items:
            self.assertTrue(item["link"].startswith("https://pan.baidu.com/s/"), item["link"])
            self.assertEqual("8k2m", item["pwd"])
            self.assertIn("zhidao.baidu.com/question/", item["source"])

        progress = channel.all("progress")
        self.assertEqual(len(words), progress[-1]["found"])

        done = channel.first("done")
        self.assertEqual(len(words), done["found"])
        self.assertFalse(done["stopped"])
        self.assertEqual(len(words), done["stats"]["found"])

    def test_real_worker_stops_on_request_and_still_reports_done(self):
        import tempfile
        from datetime import date
        from lxds.collect import CollectWorker

        today = date.today().isoformat()
        words = ["测试词%d" % index for index in range(40)]
        pages = {("求%s百度网盘链接" % word, 0): self._page([("%d1" % index, "求《%s》百度网盘链接" % word, today)])
                 for index, word in enumerate(words)}
        details = {"%d1" % index: self._detail("1Abfixture%d" % index) for index in range(len(words))}

        channel = RecordingChannel()
        holder = {}

        class StoppingFetcher(RealWorkerIntegrationTests._Fetcher):
            def get(self, url, **kwargs):
                if len(channel.all("item")) >= 3 and "session" in holder:
                    holder["session"].stop()
                return super().get(url, **kwargs)

        with tempfile.TemporaryDirectory(prefix="collect-helper-") as temporary:
            def factory(**kwargs):
                kwargs["checkpoint_path"] = str(Path(temporary) / "checkpoint.json")
                return CollectWorker(fetcher=StoppingFetcher(pages, details),
                                     pool=self._Pool(), **kwargs)

            session = main.Session(channel, worker_factory=factory)
            holder["session"] = session
            session.start(protocol.parse_start({
                "cmd": "start", "mode": "precise", "keywords": words,
                "accounts": [{"name": "知道甲", "cookie": "BDUSS=x"}]}))
            session.join(timeout=30)

        done = channel.first("done")
        self.assertIsNotNone(done, "停止后仍须给出 done")
        self.assertTrue(done["stopped"], "done 应标记为被停止")
        self.assertLess(len(channel.all("item")), len(words), "应当没有跑完全部关键词")


if __name__ == "__main__":
    unittest.main()
