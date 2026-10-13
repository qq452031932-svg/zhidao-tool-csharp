"""采集、转存和模板任务的 JSON 断点存储。"""

from __future__ import annotations

from hashlib import sha256
import json
import os
from pathlib import Path
import re
import tempfile
import time


def _base_dir() -> Path:
    local_appdata = os.environ.get("LOCALAPPDATA")
    root = Path(local_appdata) if local_appdata else Path.home() / ".config"
    return root / "lxds" / "checkpoints"


def _safe_kind(kind: str) -> str:
    if not re.fullmatch(r"[A-Za-z0-9_-]+", kind):
        raise ValueError("无效的断点类型")
    return kind


def _source_key(source: str) -> str:
    try:
        normalized = str(Path(source).resolve()).casefold()
    except (OSError, ValueError):
        # 来源可能是拼接的关键词而非路径，过长时无法解析，直接取原文。
        normalized = source.casefold()
    return sha256(normalized.encode("utf-8")).hexdigest()[:16]


def checkpoint_path(source: str, kind: str) -> str:
    """返回稳定的任务断点路径，来源文件名不会直接进入路径。"""
    return str(_base_dir() / f"{_safe_kind(kind)}-{_source_key(source)}.json")


def operation_path(source: str, kind: str) -> str:
    """返回长期操作记录路径。"""
    return str(_base_dir() / "operations" / f"{_safe_kind(kind)}-{_source_key(source)}.json")


def find_operations(kind: str) -> list[dict]:
    """列出某种操作的可读取记录，损坏记录自动跳过。"""
    directory = _base_dir() / "operations"
    records: list[dict] = []
    for path in sorted(directory.glob(f"{_safe_kind(kind)}-*.json")):
        payload = load(str(path))
        if payload:
            records.append({**payload, "path": str(path)})
    return records


def load(path: str) -> dict:
    """文件缺失或 JSON 损坏时返回空字典，保证恢复流程可继续。"""
    try:
        with Path(path).open("r", encoding="utf-8") as file:
            payload = json.load(file)
    except (FileNotFoundError, json.JSONDecodeError, UnicodeDecodeError):
        return {}
    return payload if isinstance(payload, dict) else {}


def save(path: str, payload: dict) -> None:
    """先写同目录临时文件，再原子替换断点。"""
    target = Path(path)
    target.parent.mkdir(parents=True, exist_ok=True)
    temporary_path: Path | None = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="w", encoding="utf-8", dir=target.parent,
            prefix=".checkpoint-", suffix=".tmp", delete=False,
        ) as file:
            temporary_path = Path(file.name)
            json.dump(payload, file, ensure_ascii=False)
            file.flush()
            os.fsync(file.fileno())
        os.replace(temporary_path, target)
    finally:
        if temporary_path is not None:
            temporary_path.unlink(missing_ok=True)


def clear(path: str) -> None:
    Path(path).unlink(missing_ok=True)


def clear_collect() -> None:
    for path in _base_dir().glob("collect-*.json"):
        clear(str(path))


def clear_operations(kind: str) -> None:
    for path in (_base_dir() / "operations").glob(f"{_safe_kind(kind)}-*.json"):
        clear(str(path))


def clear_all() -> None:
    for path in _base_dir().rglob("*.json"):
        clear(str(path))


def prune(max_age_days: int = 7, keep: int = 50) -> None:
    """删除过期记录，并在剩余记录中仅保留最新的 keep 个。"""
    now = time.time()
    survivors: list[Path] = []
    for path in _base_dir().rglob("*.json"):
        if now - path.stat().st_mtime > max_age_days * 86400:
            clear(str(path))
        else:
            survivors.append(path)
    for path in sorted(survivors, key=lambda item: item.stat().st_mtime, reverse=True)[keep:]:
        clear(str(path))
