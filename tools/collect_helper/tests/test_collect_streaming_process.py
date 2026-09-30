"""Real buffered subprocess proof: first item/progress is readable before done."""

import json
from pathlib import Path
import queue
import subprocess
import sys
import tempfile
import threading
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parent))

import main
from lxds.collect import CollectWorker
from test_collect_streaming import BlockingSecondFetcher, OfflinePool


def run_probe(checkpoint):
    main._configure_streams()
    channel = main.Channel(sys.stdout)
    fetcher = BlockingSecondFetcher(
        on_block=lambda: channel.send("status", text="offline-second-fetch-blocked")
    )

    def factory(**kwargs):
        return CollectWorker(fetcher=fetcher, pool=OfflinePool(), **kwargs)

    session = main.Session(channel, worker_factory=factory)
    session.start({
        "keywords": ["资源"], "count": 2, "mode": "fuzzy", "time_range": "全部",
        "accounts": [], "detail_mode": True, "dedupe_names": True, "resume": False,
        "checkpoint_path": checkpoint,
    })
    try:
        sys.stdin.readline()
    finally:
        fetcher.release.set()
        session.join(5)
        if session.running:
            session.stop()
            raise AssertionError("Offline worker did not finish after response release")


class CollectStreamingProcessTests(unittest.TestCase):
    def test_item_progress_and_checkpoint_arrive_before_done_with_buffered_stdout(self):
        with tempfile.TemporaryDirectory(prefix="stream-process-") as temporary:
            checkpoint = Path(temporary) / "checkpoint.json"
            # Deliberately omit -u/PYTHONUNBUFFERED: production Channel.send must flush.
            process = subprocess.Popen(
                [sys.executable, "-B", "-E", "-s", "-X", "utf8", str(Path(__file__).resolve()),
                 "--streaming-probe", str(checkpoint)],
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                text=True, encoding="utf-8",
            )
            lines = queue.Queue()

            def read_stdout():
                for line in process.stdout:
                    lines.put(line)

            reader = threading.Thread(target=read_stdout, daemon=True)
            reader.start()
            frames = []
            try:
                while True:
                    frame = json.loads(lines.get(timeout=5))
                    frames.append(frame)
                    if frame.get("text") == "offline-second-fetch-blocked":
                        break
                self.assertIsNone(process.poll(), "Process must still be waiting for the next response")
                self.assertNotIn("done", [frame["ev"] for frame in frames])
                items = [frame for frame in frames if frame["ev"] == "item"]
                self.assertEqual(1, len(items))
                self.assertEqual("第一本", items[0]["name"])
                progress = [frame for frame in frames if frame["ev"] == "progress"]
                self.assertEqual((1, 2), (progress[-1]["found"], progress[-1]["target"]))
                saved = json.loads(checkpoint.read_text(encoding="utf-8"))
                self.assertEqual(1, saved["found"])
                self.assertEqual("第一本", saved["items"][0]["资源名称"])

                process.stdin.write("release\n")
                process.stdin.flush()
                while frames[-1]["ev"] != "done":
                    frames.append(json.loads(lines.get(timeout=5)))
                self.assertEqual(2, frames[-1]["found"])
                self.assertEqual(2, sum(frame["ev"] == "item" for frame in frames))
                self.assertNotIn("error", [frame["ev"] for frame in frames])
                self.assertEqual(0, process.wait(timeout=5))
                self.assertEqual("", process.stderr.read())
            finally:
                if process.poll() is None:
                    process.kill()
                    process.wait(timeout=5)
                reader.join(5)
                for stream in (process.stdin, process.stdout, process.stderr):
                    stream.close()


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--streaming-probe":
        run_probe(sys.argv[2])
    else:
        unittest.main()
