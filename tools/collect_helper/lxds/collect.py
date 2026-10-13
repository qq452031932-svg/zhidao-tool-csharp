"""百度知道采集调度；网络、解析、风控和断点由各自模块负责。"""

from __future__ import annotations

from pathlib import Path
from threading import Event
from urllib.parse import quote

from lxds.behavior import pick_common_question, pick_every, shuffle_blocks
from lxds.checkpoint import checkpoint_path as default_checkpoint_path, load, save, clear
from lxds.pool import CollectCancelled, PooledFetcher, SlotPool, is_risk_html
from lxds.websearch import BaiduSiteSearchCollector
from lxds.zhidao_parse import classify_search_html, collect_link_key, parse_search_page
from lxds.collect_engine import CollectionEngine
from lxds.collection_rules import SITE_MAX_VARIANTS
from lxds.page_cache import CachedFetcher, TaskPageCache


_SEARCH_URL = "https://zhidao.baidu.com/search?lm=0&rn=10&pn={}&fr=search&dyTabStr=null&word={}"
_STATS = ("found", "ok", "risk", "skipped_generic", "no_link", "no_pwd", "dead",
          "detail_empty", "parse_warn", "barren_pages", "abnormal_pages", "idle_pages", "filtered_date")

class CollectWorker(CollectionEngine):
    """以普通 callback 驱动精准或模糊采集任务。"""

    def __init__(self, *, keyword=None, txt_paths=None, count=0,
                 time_range="auto", accounts=None, detail_mode=True,
                 dedupe_names=True, mode="precise", resume=False,
                 checkpoint_path=None, on_item=None, on_progress=None,
                 on_status=None, on_log=None, pause_ask=None,
                 fetcher=None, pool=None, supplement_factory=None, on_restored=None):
        if mode not in ("precise", "fuzzy"):
            raise ValueError("mode 必须是 precise 或 fuzzy")
        self.keyword = keyword
        self.txt_paths = txt_paths
        self.count = max(0, int(count))
        self.time_range = time_range
        self.detail_mode = ("auto" if detail_mode else "off") if isinstance(detail_mode, bool) else detail_mode
        if self.detail_mode not in ("auto", "off", "always"):
            self.detail_mode = "auto"
        self.dedupe_names = dedupe_names
        self.mode = mode
        self.resume = resume
        source = "|".join(str(path) for path in (txt_paths if isinstance(txt_paths, (list, tuple)) else [txt_paths]) if path)
        if not source:
            source = "|".join(str(value) for value in (keyword if isinstance(keyword, (list, tuple)) else [keyword]) if value)
        self.checkpoint_path = checkpoint_path or default_checkpoint_path(f"{mode}:{source}", "collect")
        self.on_item = on_item or (lambda item: None)
        self.on_progress = on_progress or (lambda progress: None)
        self.on_status = on_status or (lambda status: None)
        self.on_log = on_log or (lambda message: None)
        self.pause_ask = pause_ask
        self.on_restored = on_restored or (lambda items: None)
        self.stop_event = Event()
        self._resume_event = Event()
        self._resume_choice = False
        self.pool = pool or SlotPool(accounts or [], self._pause_for_risk, self.on_log,
                                    self.stop_event, self.on_status)
        if pool is not None and hasattr(pool, "stop_event"):
            pool.stop_event = self.stop_event
        self._page_cache = TaskPageCache(self.stop_event, self.pool)
        self.fetcher = CachedFetcher(fetcher or PooledFetcher(self.pool), self._page_cache)
        self.supplement_factory = supplement_factory or (lambda pinned, event: BaiduSiteSearchCollector(
            pinned, event, max_variants=1 if self.mode == "precise" else SITE_MAX_VARIANTS))
        self._supplement_enabled = supplement_factory is not None or fetcher is None
        self.items: list[dict] = []
        self._item_dates: dict[str, str] = {}
        self._item_keywords: dict[str, str] = {}
        self._item_coverage_pages: dict[str, str] = {}
        self.seen_keys: set[str] = set()
        self.seen_names: set[str] = set()
        self.done_keywords: set[str] = set()
        self.found_keywords: set[str] = set()
        self.done_queries: set[str] = set()
        # 续采用：未翻完的查询下一页位置，以及已做过补量的词。
        self._query_cursor: dict[str, int] = {}
        self.supplement_done: set[str] = set()
        self.stats = dict.fromkeys(_STATS, 0)
        self._used_common: set[str] = set()
        self._source_words: list[str] = []

    @staticmethod
    def _pick_every(bounds):
        return pick_every(bounds)

    @staticmethod
    def _pick_common(used):
        return pick_common_question(used)

    @staticmethod
    def _shuffle(words):
        return shuffle_blocks(words, 10)

    def _clear_checkpoint(self):
        if self.checkpoint_path:
            clear(self.checkpoint_path)

    def stop(self) -> None:
        self.stop_event.set()
        self._resume_event.set()

    def resume_after_risk(self, do_continue: bool) -> None:
        self._resume_choice = bool(do_continue)
        self._resume_event.set()

    def _pause_for_risk(self, payload: dict) -> bool:
        if self.pause_ask is not None:
            return bool(self.pause_ask(payload))
        self._resume_event.clear()
        if self.stop_event.is_set():
            return False
        self.on_status("采集账号受限，请换节点后继续或停止")
        answered = self._resume_event.wait(1800)
        return answered and self._resume_choice and not self.stop_event.is_set()

    def _keywords(self) -> list[str]:
        words = []
        if self.keyword is not None:
            words.extend(self.keyword if isinstance(self.keyword, (list, tuple)) else [self.keyword])
        paths = self.txt_paths or []
        if isinstance(paths, (str, Path)):
            paths = [paths]
        for path in paths:
            raw = Path(path).read_bytes()
            try:
                content = raw.decode("utf-8-sig")
            except UnicodeDecodeError:
                content = raw.decode("gb18030")
            words.extend(content.splitlines())
        return list(dict.fromkeys(str(word).strip() for word in words if str(word).strip()))

    def _restore(self) -> None:
        if not self.resume or not self.checkpoint_path:
            return
        payload = load(self.checkpoint_path)
        if payload.get("mode") != self.mode:
            return
        saved_words = payload.get("keywords")
        if isinstance(saved_words, list) and saved_words != self._source_words:
            self.on_log("关键词与断点记录不一致，不沿用旧进度，从头开始采集")
            return
        self.items = [{"资源名称": row.get("资源名称", row.get("name", "")),
                       "资源链接": row.get("资源链接", row.get("link", "")),
                       "提取码": row.get("提取码", row.get("pwd", "")),
                       "来源链接": row.get("来源链接", row.get("source_url", ""))}
                      for row in payload.get("items", [])]
        self._item_dates = payload.get("item_dates", {})
        self._item_keywords = payload.get("item_keywords", {})
        self._item_coverage_pages = payload.get("item_coverage_pages", {})
        if not isinstance(self._item_coverage_pages, dict):
            self._item_coverage_pages = {}
        if not isinstance(self._item_keywords, dict):
            self._item_keywords = {}
        if not isinstance(self._item_dates, dict):
            self._item_dates = {}
        if self._has_date_cap():
            before = len(self.items)
            self.items = [row for row in self.items if self._known_date_within(
                self._item_dates.get(collect_link_key(row["资源链接"]), ""))]
            if len(self.items) != before:
                self.on_log(f"断点中 {before - len(self.items)} 条结果超出90天或无法确认日期，已排除并重新搜索")
                # 原断点的去重和页标记可能阻止重新获取符合当前日期限制的资源。
                payload = {}
            self._item_dates = {collect_link_key(row["资源链接"]): self._item_dates[collect_link_key(row["资源链接"])]
                                for row in self.items}
            if len(self._source_words) > 1:
                before = len(self.items)
                self.items = [row for row in self.items
                              if self._item_keywords.get(collect_link_key(row["资源链接"])) in self._source_words]
                if len(self.items) != before:
                    self.on_log(f"断点中 {before - len(self.items)} 条结果无可靠关键词归属，重新搜索以优先覆盖各关键词")
                    payload = {}
        keys = {collect_link_key(row["资源链接"]) for row in self.items}
        self._item_keywords = {key: word for key, word in self._item_keywords.items() if key in keys}
        self._item_coverage_pages = {key: page for key, page in self._item_coverage_pages.items() if key in keys}
        self._item_dates = {key: value for key, value in self._item_dates.items() if key in keys}
        old_keys = payload.get("seen_links", payload.get("seen_keys", []))
        self.seen_keys = {collect_link_key(key) if "://" in key else key for key in old_keys}
        self.seen_keys.update(collect_link_key(row["资源链接"]) for row in self.items)
        self.seen_names = {str(name).strip().lower() for name in payload.get("seen_names", [])}
        self.seen_names.update(row["资源名称"].strip().lower() for row in self.items)
        self.done_keywords = set(payload.get("done_keywords", []))
        self.found_keywords = set(payload.get("found_keywords", []))
        self.done_queries = set(payload.get("done_queries", []))
        cursor = payload.get("query_cursor", {})
        self._query_cursor = ({key: value for key, value in cursor.items() if isinstance(value, int)}
                              if isinstance(cursor, dict) else {})
        self.supplement_done = set(payload.get("supplement_done", []))
        self.stats.update({key: int(value) for key, value in payload.get("stats", {}).items() if key in self.stats})
        self.stats["found"] = len(self.items)

    def _save(self) -> None:
        if not self.checkpoint_path:
            return
        payload = {
            "mode": self.mode, "items": self.items,
            "item_dates": self._item_dates,
            "item_keywords": self._item_keywords,
            "item_coverage_pages": self._item_coverage_pages,
            "seen_keys": sorted(self.seen_keys), "seen_links": sorted(self.seen_keys),
            "seen_names": sorted(self.seen_names), "rules_version": "1.0.4",
            "found": len(self.items), "stats": self.stats, "keywords": self._source_words,
            "supplement_done": sorted(self.supplement_done),
        }
        if self.mode == "precise":
            payload.update(done_keywords=sorted(self.done_keywords),
                           found_keywords=sorted(self.found_keywords))
        else:
            payload.update(done_queries=sorted(self.done_queries), query_cursor=self._query_cursor)
        save(self.checkpoint_path, payload)

    def _add(self, name: str, link: str, pwd: str, source_url: str, *, item_date: str = "",
             item_keyword: str = "", coverage_page: str = "") -> bool:
        if self._has_date_cap() and not self._known_date_within(item_date):
            return False
        if not (name or "").strip() or not (pwd or "").strip():
            return False
        key = collect_link_key(link)
        if key in self.seen_keys:
            self.on_log(f"跳过重复链接：{link}")
            return False
        self.seen_keys.add(key)
        name_key = name.strip().lower()
        if self.dedupe_names and name_key in self.seen_names:
            self.on_log(f"跳过重复名称：{name}")
            return False
        row = {"资源名称": name, "资源链接": link, "提取码": pwd,
               "来源链接": source_url}
        self.items.append(row)
        if item_date:
            self._item_dates[key] = item_date
        if item_keyword:
            self._item_keywords[key] = item_keyword
        if coverage_page:
            self._item_coverage_pages[key] = coverage_page
        if self.dedupe_names and name_key:
            self.seen_names.add(name_key)
        self.stats["found"] = len(self.items)
        self.on_item(row)
        self.on_progress({"found": len(self.items), "target": self.count})
        self._save()
        return True


    def _search(self, word: str, pn: int) -> tuple[list[dict], int | None, str]:
        if self.stop_event.is_set():
            raise CollectCancelled("采集已停止")
        url = _SEARCH_URL.format(pn, quote(word))
        page = pn // 10 + 1
        self.on_status(f"{getattr(self, '_current_step', '搜索')} · 第 {page} 页：{word}")
        html, _slot = self.fetcher.get(url, purpose="search")
        if is_risk_html(html):
            self.on_log(f"第 {page} 页触发安全验证，按账号风控策略处理")
            self.stats["abnormal_pages"] += 1
            return [], None, "unknown"
        candidates, next_pn = parse_search_page(html)
        status = classify_search_html(html, candidates)
        self.on_log(f"第 {page} 页已返回：{len(candidates)} 个候选问题，页面状态 {status}")
        if status == "unknown":
            self.stats["abnormal_pages"] += 1
        elif status == "empty":
            self.stats["barren_pages"] += 1
        return candidates, next_pn, status
