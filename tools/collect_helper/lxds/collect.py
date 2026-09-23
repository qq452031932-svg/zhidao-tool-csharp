"""百度知道采集调度；网络、解析、风控和断点由各自模块负责。"""

from __future__ import annotations

from pathlib import Path
from threading import Event
from urllib.parse import quote

from lxds.behavior import pick_common_question, pick_every
from lxds.checkpoint import checkpoint_path as default_checkpoint_path, load, save
from lxds.pool import (CollectCancelled, PinnedExitFetcher, PooledFetcher,
                       PoolError, SlotPool, is_risk_html)
from lxds.websearch import BaiduWebCollector
from lxds.zhidao_parse import (classify_search_html, extract_pan_entries,
                               guess_resource_name, has_concrete_name, link_key,
                               parse_detail_page, parse_search_page, within_range)


_SEARCH_URL = "https://zhidao.baidu.com/search?lm=0&rn=10&pn={}&fr=search&dyTabStr=null&word={}"
_STAGES = (("最近一月", 30), ("最近一年", 365), ("全部", None))
_STATS = ("found", "skipped_generic", "no_link", "no_pwd", "dead",
          "detail_empty", "parse_warn", "barren_pages", "abnormal_pages", "idle_pages")


class _DirectOnlyFetcher(PinnedExitFetcher):
    """给补量保留 PinnedExitFetcher 接口，但禁止回退到代理槽。"""

    def _select_slot(self):
        import time

        self.pool.start()
        direct = [slot for slot in self.pool.slots if slot.exit_kind == "direct"]
        if not direct:
            raise PoolError("补量缺少直连账号")
        while True:
            if self.pool.stop_event.is_set():
                raise CollectCancelled("采集已停止")
            now = time.monotonic()
            for slot in direct:
                if slot.unavailable_until <= now and slot.pacer.wait_time(now) <= 0:
                    return slot
            delay = min(max(slot.unavailable_until - now, slot.pacer.wait_time(now)) for slot in direct)
            self.pool._interruptible_wait(max(0.01, delay))


class CollectWorker:
    """以普通 callback 驱动精准或模糊采集任务。"""

    def __init__(self, *, keyword=None, txt_paths=None, count=0,
                 time_range="auto", accounts=None, detail_mode=True,
                 dedupe_names=True, mode="precise", resume=False,
                 checkpoint_path=None, on_item=None, on_progress=None,
                 on_status=None, on_log=None, pause_ask=None,
                 fetcher=None, pool=None, supplement_factory=None):
        if mode not in ("precise", "fuzzy"):
            raise ValueError("mode 必须是 precise 或 fuzzy")
        self.keyword = keyword
        self.txt_paths = txt_paths
        self.count = max(0, int(count))
        self.time_range = time_range
        self.detail_mode = detail_mode
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
        self.stop_event = Event()
        self._resume_event = Event()
        self._resume_choice = False
        self.pool = pool or SlotPool(accounts or [], self._pause_for_risk, self.on_log,
                                    self.stop_event, self.on_status)
        if pool is not None and hasattr(pool, "stop_event"):
            pool.stop_event = self.stop_event
        self.fetcher = fetcher or PooledFetcher(self.pool)
        self.supplement_factory = supplement_factory or (lambda pinned, event: BaiduWebCollector(pinned, event))
        self._supplement_enabled = supplement_factory is not None or fetcher is None
        self.items: list[dict] = []
        self.seen_keys: set[str] = set()
        self.seen_names: set[str] = set()
        self.done_keywords: set[str] = set()
        self.found_keywords: set[str] = set()
        self.done_queries: set[str] = set()
        self.stats = dict.fromkeys(_STATS, 0)
        self._used_common: set[str] = set()
        self._real_since_common = 0
        self._next_common = pick_every((3, 6))
        self._empty_words = 0
        self._empty_pages = 0
        self._source_words: list[str] = []

    def stop(self) -> None:
        self.stop_event.set()
        self._resume_event.set()

    def resume_after_risk(self, do_continue: bool) -> None:
        self._resume_choice = bool(do_continue)
        self._resume_event.set()

    def _pause_for_risk(self, payload: dict) -> bool:
        if self.pause_ask is not None:
            return bool(self.pause_ask(payload))
        self.on_status("采集账号受限，请换节点后继续或停止")
        while not self.stop_event.is_set():
            if self._resume_event.wait(0.2):
                self._resume_event.clear()
                return self._resume_choice and not self.stop_event.is_set()
        return False

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
        self.items = list(payload.get("items", []))
        self.seen_keys = set(payload.get("seen_keys", []))
        self.seen_names = set(payload.get("seen_names", []))
        self.done_keywords = set(payload.get("done_keywords", []))
        self.found_keywords = set(payload.get("found_keywords", []))
        self.done_queries = set(payload.get("done_queries", []))
        self.stats.update({key: int(value) for key, value in payload.get("stats", {}).items() if key in self.stats})
        self.stats["found"] = len(self.items)

    def _save(self) -> None:
        if not self.checkpoint_path:
            return
        payload = {
            "mode": self.mode, "items": self.items,
            "seen_keys": sorted(self.seen_keys), "seen_names": sorted(self.seen_names),
            "found": len(self.items), "stats": self.stats,
        }
        if self.mode == "precise":
            payload.update(done_keywords=sorted(self.done_keywords),
                           found_keywords=sorted(self.found_keywords), keywords=self._source_words)
        else:
            payload.update(done_queries=sorted(self.done_queries))
        save(self.checkpoint_path, payload)

    def _add(self, name: str, link: str, pwd: str, source_url: str) -> bool:
        key = link_key(link)
        if key in self.seen_keys:
            self.on_log(f"跳过重复链接：{link}")
            return False
        if self.dedupe_names and name in self.seen_names:
            self.on_log(f"跳过重复名称：{name}")
            return False
        row = {"资源名称": name, "资源链接": link, "提取码": pwd,
               "来源链接": source_url}
        self.items.append(row)
        self.seen_keys.add(key)
        self.seen_names.add(name)
        self.stats["found"] = len(self.items)
        if not pwd:
            self.stats["no_pwd"] += 1
        self.on_item(row)
        self.on_progress({"found": len(self.items), "target": self.count})
        self._save()
        return True

    def _maybe_common(self) -> None:
        if self._real_since_common < self._next_common or self.stop_event.is_set():
            return
        question = pick_common_question(self._used_common)
        self._real_since_common = 0
        self._next_common = pick_every((3, 6))
        self.fetcher.get(_SEARCH_URL.format(0, quote(question)), purpose="search")

    def _search(self, word: str, pn: int) -> tuple[list[dict], int | None, str]:
        if self.stop_event.is_set():
            raise CollectCancelled("采集已停止")
        url = _SEARCH_URL.format(pn, quote(word))
        html, _slot = self.fetcher.get(url, purpose="search")
        self._real_since_common += 1
        self._maybe_common()
        if is_risk_html(html):
            self.stats["abnormal_pages"] += 1
            return [], None, "unknown"
        candidates, next_pn = parse_search_page(html)
        status = classify_search_html(html, candidates)
        if status == "unknown":
            self.stats["abnormal_pages"] += 1
        elif status == "empty":
            self.stats["barren_pages"] += 1
        return candidates, next_pn, status

    def _process_candidates(self, candidates: list[dict], search_url: str,
                            days: int | None, limit: int | None) -> int:
        added = 0
        for candidate in candidates:
            if self.stop_event.is_set():
                raise CollectCancelled("采集已停止")
            if days is not None and not within_range(candidate["date"], days):
                continue
            title = candidate["title"]
            if self.mode == "fuzzy" and not has_concrete_name(title):
                self.stats["skipped_generic"] += 1
                continue
            if self.detail_mode:
                html, _slot = self.fetcher.get(candidate["url"], referer=search_url,
                                                purpose="detail")
                body, state = parse_detail_page(html)
                if state == "dead":
                    self.stats["dead"] += 1
                    continue
                if state == "empty":
                    self.stats["detail_empty"] += 1
                    continue
                if state == "fallback":
                    self.stats["parse_warn"] += 1
            else:
                body = title
            entries = extract_pan_entries(body)
            if not entries:
                self.stats["no_link"] += 1
                continue
            for entry in entries:
                name = guess_resource_name(title, body, entry["link"])
                if self._add(name, entry["link"], entry["pwd"], candidate["url"]):
                    added += 1
                if limit is not None and added >= limit:
                    return added
                if self.mode == "fuzzy" and self.count and len(self.items) >= self.count:
                    return added
        return added

    def _query(self, word: str, days: int | None, limit: int | None) -> int:
        pn = 0
        added_total = 0
        while not self.stop_event.is_set():
            candidates, next_pn, status = self._search(word, pn)
            if not candidates:
                self._empty_pages += 1
                if self._empty_pages >= 5:
                    self.pool.on_silent_risk(word, pn)
                    self._empty_pages = 0
                    self._empty_words = 0
            else:
                self._empty_pages = 0
            if status == "unknown":
                break
            search_url = _SEARCH_URL.format(pn, quote(word))
            added = self._process_candidates(candidates, search_url, days,
                                             None if limit is None else limit - added_total)
            added_total += added
            if candidates and not added:
                self.stats["idle_pages"] += 1
            if (limit is not None and added_total >= limit) or (self.mode == "fuzzy" and self.count and len(self.items) >= self.count):
                break
            if next_pn is None:
                break
            if next_pn <= pn:
                self.on_log(f"分页 pn={next_pn} 未继续前进，停止该查询翻页")
                break
            pn = next_pn
        return added_total

    def _stage_days(self) -> list[tuple[str, int | None]]:
        if self.mode == "precise":
            if self.time_range == "auto" or self.time_range in (None, "全部"):
                return [("全部", None)]
            if isinstance(self.time_range, int) and self.time_range > 0:
                return [(str(self.time_range), self.time_range)]
            raise ValueError("精准模式 time_range 必须为正整数天数或 auto/全部")
        if self.time_range == "auto":
            return list(_STAGES)
        if self.time_range in dict(_STAGES):
            return [(self.time_range, dict(_STAGES)[self.time_range])]
        raise ValueError("模糊模式 time_range 必须为 auto、最近一月、最近一年或全部")

    def _supplement(self, terms: list[str]) -> None:
        if not self._supplement_enabled or self.stop_event.is_set() or (self.mode == "fuzzy" and self.count and len(self.items) >= self.count):
            return
        self.pool.start()
        if not any(slot.exit_kind == "direct" for slot in self.pool.slots):
            self.on_log("补量缺少直连账号，跳过网页补量")
            return
        collector = self.supplement_factory(_DirectOnlyFetcher(self.pool, "direct"), self.stop_event)
        for term in terms:
            if self.stop_event.is_set():
                return
            if self.mode == "fuzzy" and self.count and len(self.items) >= self.count:
                return
            risk = False

            def collect_log(message: str) -> None:
                nonlocal risk
                self.on_log(message)
                if "搜索失败" in message or "安全验证" in message:
                    risk = True

            try:
                records = collector.search(term, collect_log)
            except (PoolError, CollectCancelled) as exc:
                self.on_log(f"补量提前结束：{exc}")
                return
            for record in records:
                if self.stop_event.is_set():
                    return
                title = record.get("title") or term
                if self.mode == "fuzzy" and not has_concrete_name(title):
                    self.stats["skipped_generic"] += 1
                    continue
                for entry in record.get("links", []):
                    link = entry["link"] if isinstance(entry, dict) else entry
                    pwd = entry.get("pwd", "") if isinstance(entry, dict) else ""
                    name = guess_resource_name(title, title, link)
                    added = self._add(name, link, pwd, record.get("source_url", ""))
                    if added and self.mode == "precise":
                        self.found_keywords.add(term)
                        self._save()
                        break
                    if self.count and len(self.items) >= self.count:
                        break
                if self.mode == "precise" and term in self.found_keywords:
                    break
            if risk or getattr(collector, "stop_supplement", False):
                self.on_log("补量遇到风控，保留已采结果")
                return

    def run(self) -> dict:
        words = self._keywords()
        if self.mode == "fuzzy" and not words:
            raise ValueError("模糊模式需要领域关键词")
        self._source_words = words
        self._restore()
        stopped = False
        try:
            for stage_index, (stage, days) in enumerate(self._stage_days()):
                if stage_index:
                    self.on_status(f"时间范围自动放宽到「{stage}」继续采集…")
                for word in words:
                    if self.stop_event.is_set():
                        raise CollectCancelled("采集已停止")
                    query = word if self.mode == "precise" else f"求{word}百度网盘链接"
                    marker = word if self.mode == "precise" else f"{stage}:{query}"
                    done = self.done_keywords if self.mode == "precise" else self.done_queries
                    if marker in done:
                        continue
                    before = len(self.items)
                    added = self._query(query, days, 1 if self.mode == "precise" else None)
                    if self.mode == "precise" and added:
                        self.found_keywords.add(word)
                    if len(self.items) == before:
                        self._empty_words += 1
                        if self._empty_words >= 5:
                            self.pool.on_silent_risk(word, 0)
                            self._empty_words = 0
                    else:
                        self._empty_words = 0
                    done.add(marker)
                    self._save()
                    if self.mode == "fuzzy" and self.count and len(self.items) >= self.count:
                        break
                if self.mode == "precise" or (self.count and len(self.items) >= self.count):
                    break
            if self.mode == "fuzzy" and self.count and len(self.items) < self.count:
                self._supplement(words)
            elif self.mode == "precise":
                missing = [word for word in words if word not in self.found_keywords]
                if missing:
                    self._supplement(missing)
        except CollectCancelled:
            stopped = True
        finally:
            self._save()
        return {"items": list(self.items), "found": len(self.items),
                "stats": dict(self.stats), "stopped": stopped or self.stop_event.is_set()}
