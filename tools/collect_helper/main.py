"""百度知道采集助手。

宿主启动本进程后，按行写入命令，本进程按行写回事件（见 protocol.py）。
采集跑在后台线程，主线程只负责读命令，因此 stop 和 ask 的答复随时能进来。

进程自身不落地任何结果文件，也不打印 Cookie；采到的每一条都即时回传，
由宿主决定怎么存。
"""

from __future__ import annotations

import sys
import threading
import traceback
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import protocol  # noqa: E402
from lxds.collect import CollectWorker  # noqa: E402


class Channel:
    """串行化的事件输出通道。stdout 只走协议帧。"""

    def __init__(self, stream):
        self._stream = stream
        self._lock = threading.Lock()

    def send(self, event, **fields):
        line = protocol.encode(event, **fields)
        with self._lock:
            self._stream.write(line + "\n")
            self._stream.flush()


class Session:
    """一次采集任务的生命周期。"""

    def __init__(self, channel, worker_factory=CollectWorker):
        self._channel = channel
        self._worker_factory = worker_factory
        self._worker = None
        self._thread = None
        self._answer = threading.Event()
        self._stop_requested = threading.Event()
        self._pause_requested = threading.Event()
        self._answer_value = False
        self._target = 0
        self._lock = threading.Lock()

    @property
    def running(self):
        thread = self._thread
        return thread is not None and thread.is_alive()

    def start(self, options):
        if self.running:
            raise protocol.ProtocolError("上一次采集尚未结束")
        self._stop_requested.clear()
        self._pause_requested.clear()
        self._answer.clear()
        self._answer_value = False
        worker = self._worker_factory(
            keyword=options["keywords"],
            count=options["count"],
            time_range=options["time_range"],
            accounts=options["accounts"],
            detail_mode=options["detail_mode"],
            dedupe_names=options["dedupe_names"],
            mode=options["mode"],
            resume=options["resume"],
            checkpoint_path=options["checkpoint_path"],
            on_item=self._on_item,
            on_progress=self._on_progress,
            on_status=self._on_status,
            on_log=self._on_log,
            pause_ask=self._on_ask,
            on_restored=self._on_restored,
        )
        # 精准模式不限总量，进度的分母取关键词个数，否则界面只能显示 N/0
        self._target = options["count"] or (
            len(options["keywords"]) if options["mode"] == "precise" else 0)
        with self._lock:
            self._worker = worker
        self._thread = threading.Thread(target=self._run, args=(worker,),
                                        name="collect", daemon=True)
        # started 必须先于任何采集事件发出，否则界面会在未开始状态下收到数据
        self._channel.send("started", mode=options["mode"],
                           keywords=len(options["keywords"]),
                           target=self._target)
        self._thread.start()

    def stop(self):
        self._stop_requested.set()
        with self._lock:
            worker = self._worker
        if worker is not None:
            worker.stop()
        # 若正卡在 ask 上，用「不继续」把它放行，避免停不下来
        self._answer_value = False
        self._answer.set()

    def pause(self):
        """用户主动暂停：与停止走同一条收尾路径，断点保留，done 标记 paused。"""
        if not self.running:
            return
        self._pause_requested.set()
        with self._lock:
            worker = self._worker
        if worker is not None:
            worker.stop()
        # 正卡在风控 ask 上时同样放行，暂停不替用户答复继续
        self._answer_value = False
        self._answer.set()

    def answer(self, should_continue):
        self._answer_value = bool(should_continue)
        self._answer.set()

    def join(self, timeout=None):
        thread = self._thread
        if thread is not None:
            thread.join(timeout)

    # --- CollectWorker 的回调 --------------------------------------------
    def _on_item(self, row):
        self._channel.send("item",
                           name=row.get("资源名称", ""),
                           link=row.get("资源链接", ""),
                           pwd=row.get("提取码", ""),
                           source=row.get("来源链接", ""))

    def _on_restored(self, rows):
        """续采时把断点里的结果重新交给宿主，界面以断点为准重建列表。"""
        for row in rows:
            self._channel.send("item",
                               name=row.get("资源名称", ""),
                               link=row.get("资源链接", ""),
                               pwd=row.get("提取码", ""),
                               source=row.get("来源链接", ""),
                               restored=True)
        self._channel.send("progress", found=len(rows), target=self._target)

    def _on_progress(self, progress):
        self._channel.send("progress",
                           found=int(progress.get("found", 0)),
                           target=int(progress.get("target", 0)) or self._target)

    def _on_status(self, text):
        self._channel.send("status", text=str(text))

    def _on_log(self, text):
        self._channel.send("log", text=str(text))

    def _on_ask(self, payload):
        """撞上风控：回报宿主并阻塞，直到宿主给出答复或要求停止。"""
        self._answer.clear()
        # stop 可能在进入此回调之前发生；不能靠会被 clear 的答复事件保存停止状态。
        if self._stop_requested.is_set() or self._pause_requested.is_set():
            return False
        detail = payload if type(payload) is dict else {"reason": str(payload)}
        self._channel.send("ask", reason=str(detail.get("reason") or detail.get("message") or "采集账号受限"),
                           detail=detail)
        answered = self._answer.wait(1800)
        return (answered and bool(self._answer_value) and not self._stop_requested.is_set()
                and not self._pause_requested.is_set())

    def _paused(self):
        # 暂停之后又点了停止，按停止算
        return self._pause_requested.is_set() and not self._stop_requested.is_set()

    def _run(self, worker):
        try:
            result = worker.run()
        except Exception as error:  # 采集线程不得把进程带走
            print(traceback.format_exc(), file=sys.stderr, flush=True)
            self._channel.send("error", message="%s: %s" % (type(error).__name__, error))
            # failed 让宿主区分出错中断与用户停止：续采任务出错时不应丢掉可继续的进度
            self._channel.send("done", found=len(getattr(worker, "items", [])), stopped=True,
                               paused=self._paused(), failed=True,
                               stats=dict(getattr(worker, "stats", {}) or {}))
            return
        stopped = bool(result.get("stopped", False))
        self._channel.send("done",
                           found=int(result.get("found", 0)),
                           stopped=stopped,
                           paused=stopped and self._paused(),
                           failed=False,
                           stats=result.get("stats") or {})


def _configure_streams():
    """协议信道固定 UTF-8，不受宿主控制台代码页影响。"""
    for stream_name in ("stdin", "stdout", "stderr"):
        stream = getattr(sys, stream_name)
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is not None:
            reconfigure(encoding="utf-8", errors="replace",
                        newline="\n" if stream_name != "stdin" else None)


def main(argv=None):
    _configure_streams()
    channel = Channel(sys.stdout)
    session = Session(channel)
    channel.send("ready", protocolVersion=protocol.PROTOCOL_VERSION)

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            frame = protocol.decode(line)
        except protocol.ProtocolError as error:
            channel.send("error", message=str(error))
            continue

        command = frame["cmd"]
        try:
            if command == "ping":
                channel.send("pong", running=session.running)
            elif command == "start":
                session.start(protocol.parse_start(frame))
            elif command == "stop":
                session.stop()
            elif command == "pause":
                session.pause()
            elif command == "resume":
                session.answer(frame.get("continue", False))
            elif command == "read_keywords":
                result = protocol.read_keyword_file(protocol.parse_read_keywords(frame))
                channel.send("keywords", **result)
        except protocol.ProtocolError as error:
            channel.send("error", message=str(error), command=command, running=session.running)
        except Exception as error:  # noqa: BLE001 - 主循环必须活着
            print(traceback.format_exc(), file=sys.stderr, flush=True)
            channel.send("error", message="%s: %s" % (type(error).__name__, error),
                         command=command, running=session.running)

    # stdin 关闭即宿主退出：停掉采集并等它收尾
    session.stop()
    session.join(timeout=15)
    return 0


if __name__ == "__main__":
    sys.exit(main())
