"""采集助手与宿主之间的行协议。

宿主按行写入命令，助手按行写回事件，一行一个 JSON 对象，全程 UTF-8。
stdout 只承载协议帧；任何诊断输出一律走 stderr，否则会污染信道。

命令（宿主 -> 助手）：
    start   开始一次采集；携带模式、关键词、目标数量与账号
    stop    请求停止；助手会在下一个可中断点收尾并发回 done
    pause   用户主动暂停；同样收尾并发回 done（paused 为 true），进度留在断点里，
            之后用 resume 为 true 的 start 接着采
    resume  回答一次风控 ask；continue 为 false 等同于停止（与 pause 后的续采无关）
    ping    连通性探测，助手回 pong
    read_keywords  读取一个关键词文件并回传；编码交给助手判别

事件（助手 -> 宿主）：
    ready     启动完成，可以接收命令
    started   本次采集已开始
    item      采到一条资源
    progress  已采数量与目标数量
    status    面向用户的状态文字
    log       诊断文字
    ask       撞上风控，等待宿主答复；在收到 resume 前助手会阻塞
    done      本次采集结束；stopped 表示未自然采完，paused 表示因用户暂停，failed 表示出错中断
    error     本次命令失败，助手仍然存活
    pong      ping 的回应
    keywords  read_keywords 的结果，含去重后的关键词与原始行数
"""

from __future__ import annotations

import json

PROTOCOL_VERSION = 1

COMMANDS = ("start", "stop", "pause", "resume", "ping", "read_keywords")
EVENTS = ("ready", "started", "item", "progress", "status", "log",
          "ask", "done", "error", "pong", "keywords")

MODES = ("precise", "fuzzy")
MAX_FRAME_BYTES = 1_048_576
MAX_KEYWORDS = 10_000
MAX_KEYWORD_FILE_BYTES = 8 * 1024 * 1024


class ProtocolError(Exception):
    """收到的帧不符合协议。"""


def encode(event: str, **fields) -> str:
    """把一个事件编码成一行 JSON。"""
    if event not in EVENTS:
        raise ProtocolError("未知事件：%s" % event)
    frame = {"ev": event}
    frame.update(fields)
    return json.dumps(frame, ensure_ascii=False, separators=(",", ":"))


def decode(line: str) -> dict:
    """把一行文本解析成命令帧，并做最小必要校验。"""
    if len(line.encode("utf-8")) > MAX_FRAME_BYTES:
        raise ProtocolError("命令帧超过长度上限")
    try:
        frame = json.loads(line)
    except (TypeError, ValueError) as error:
        raise ProtocolError("命令不是合法 JSON") from error
    if type(frame) is not dict:
        raise ProtocolError("命令必须是 JSON 对象")
    command = frame.get("cmd")
    if command not in COMMANDS:
        raise ProtocolError("未知命令：%r" % (command,))
    return frame


def parse_start(frame: dict) -> dict:
    """校验 start 命令并整理成 CollectWorker 需要的参数。"""
    mode = frame.get("mode")
    if mode not in MODES:
        raise ProtocolError("mode 必须是 precise 或 fuzzy")

    keywords = frame.get("keywords") or []
    if type(keywords) is not list:
        raise ProtocolError("keywords 必须是数组")
    keywords = [str(word).strip() for word in keywords if str(word).strip()]
    if not keywords:
        raise ProtocolError("keywords 不能为空")
    if len(keywords) > MAX_KEYWORDS:
        raise ProtocolError("keywords 数量超过上限 %d" % MAX_KEYWORDS)

    try:
        count = int(frame.get("count") or 0)
    except (TypeError, ValueError) as error:
        raise ProtocolError("count 必须是整数") from error
    if count < 0:
        raise ProtocolError("count 不能为负数")
    if mode == "fuzzy" and count <= 0:
        raise ProtocolError("模糊模式需要正整数 count")

    accounts = frame.get("accounts") or []
    if type(accounts) is not list or not accounts:
        raise ProtocolError("至少需要一个采集账号")
    normalized = []
    for index, account in enumerate(accounts):
        if type(account) is not dict:
            raise ProtocolError("账号项必须是对象")
        cookie = str(account.get("cookie") or "").strip()
        if not cookie:
            raise ProtocolError("第 %d 个账号缺少 cookie" % (index + 1))
        name = str(account.get("name") or "").strip() or ("账号%d" % (index + 1))
        exit_kind = account.get("exit_kind")
        entry = {"name": name, "cookie": cookie}
        if exit_kind:
            entry["exit_kind"] = str(exit_kind)
        normalized.append(entry)

    return {
        "mode": mode,
        "keywords": keywords,
        "count": count,
        "accounts": normalized,
        "time_range": str(frame.get("time_range") or "auto"),
        "detail_mode": normalize_detail_mode(frame.get("detail_mode", True)),
        "dedupe_names": bool(frame.get("dedupe_names", True)),
        "resume": bool(frame.get("resume", False)),
        "checkpoint_path": frame.get("checkpoint_path") or None,
    }


def normalize_detail_mode(value):
    """保留宿主 bool 协议，并映射到原版三种详情模式。"""
    if isinstance(value, bool):
        return "auto" if value else "off"
    return value if value in ("auto", "off", "always") else "auto"


def parse_read_keywords(frame: dict) -> str:
    """校验 read_keywords 命令，返回要读取的路径。"""
    path = str(frame.get("path") or "").strip()
    if not path:
        raise ProtocolError("read_keywords 需要 path")
    return path


def read_keyword_file(path) -> dict:
    """读取关键词列；保留原协议入口与上限。"""
    from keyword_files import KeywordFileError, read_keyword_file as read
    try:
        return read(path, max_bytes=MAX_KEYWORD_FILE_BYTES, max_keywords=MAX_KEYWORDS)
    except (KeywordFileError, OSError) as error:
        raise ProtocolError(str(error)) from error
