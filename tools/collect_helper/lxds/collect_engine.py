"""v1.0.4 的采集决策；宿主回调、请求入口及断点存储由 CollectWorker 适配。"""
from collections import deque
from datetime import date
import random
from urllib.parse import quote

from lxds.collection_rules import (
    KEYWORD_VARIANTS, DOMAIN_EXTRA_KEYWORDS, PRECISE_MAX_PAGES, PRECISE_MAX_DETAIL,
    SILENT_RISK_MISS, MAX_SILENT_RETRY, ABNORMAL_RISK_HITS, FUZZY_DATE_BUCKETS,
    FUZZY_BARREN_PAGES, FUZZY_IDLE_PAGES, FUZZY_MAX_BARREN_PAGES,
    FILLER_EVERY_RANGE, OPEN_EVERY_RANGE, SUPPLEMENT_MAX_BARREN,
)
from lxds.pool import CollectCancelled, PoolError, PinnedExitFetcher
from lxds.page_cache import CachedFetcher
from lxds.fuzzy_coverage import FuzzyCoverage
from lxds.zhidao_parse import (extract_collect_entries, extract_resource_name,
    guess_resource_name, has_concrete_name, parse_detail_record, within_range)

SEARCH_URL = "https://zhidao.baidu.com/search?lm=0&rn=10&pn={}&fr=search&dyTabStr=null&word={}"
RECENT_PRIORITY_ROUNDS = 3
FUZZY_MAX_AGE_DAYS = 90


class CollectionEngine:
    """按原执行顺序保留 precise/fuzzy 与 supplement 的不同筛选和停止条件。"""

    def _check_stop(self):
        if self.stop_event.is_set():
            raise CollectCancelled("采集已停止")

    def _progress(self):
        # 宿主 found 字段表示实际表格行数，原版 no_pwd 命中数单独保存在 stats。
        self.on_progress({"found": len(self.items), "target": self.count})

    def _fetch_detail(self, item, warn=True):
        self._check_stop()
        try:
            html, _ = self.fetcher.get(item["url"], referer="https://zhidao.baidu.com/", purpose="detail")
        except CollectCancelled:
            raise
        except Exception:
            self._check_stop()
            return "", "", "empty"
        title, text, state = parse_detail_record(html)
        if warn and state == "fallback":
            self.stats["parse_warn"] += 1
            if self.stats["parse_warn"] <= 5:
                self.on_log(f"详情页正文结构未匹配，已改用整页兜底：{item['url']}")
        return title, text, state

    def _detail(self, item):
        title, text = item.get("title", ""), item.get("text", "")
        entries = extract_collect_entries(text)
        if entries and self.detail_mode == "auto":
            return title, text, entries
        if self.detail_mode in ("auto", "always"):
            self.on_status(f"{getattr(self, '_current_step', '采集')} · 读取问题：{title}")
            real_title, body, state = self._fetch_detail(item)
            if state in ("dead", "empty"):
                self.stats["dead" if state == "dead" else "detail_empty"] += 1
                return title, text, entries
            return real_title or title, body or text, extract_collect_entries(body)
        return title, text, entries

    def _after_search(self, items):
        self._check_stop()
        if len(self.items) >= self.count:
            return
        self._filler_left -= 1
        self._open_left -= 1
        if self._filler_left <= 0:
            self._filler_left = self._pick_every(FILLER_EVERY_RANGE)
            question = self._pick_common(self._used_common)
            self._used_common.add(question)
            self.on_log(f"常规浏览：搜索「{question}」（结果不入表）")
            try:
                self.fetcher.get(SEARCH_URL.format(0, quote(question)), purpose="search")
            except CollectCancelled:
                raise
            except Exception:
                self._check_stop()
                self.on_log("常规问题浏览失败，继续采集")
        if self._open_left <= 0:
            self._open_left = self._pick_every(OPEN_EVERY_RANGE)
            candidates = [item for item in items if item.get("url")]
            if candidates:
                self.on_log("常规浏览：打开一条回答页（结果不入表）")
                self._fetch_detail(random.choice(candidates[:8]), warn=False)

    def _search_one_keyword(self, word):
        query = KEYWORD_VARIANTS[0].format(kw=word)
        recent, kinds = [], []
        details_used = 0
        saw_link = saw_pwd = False
        got_one = False
        pn = 0
        for _ in range(PRECISE_MAX_PAGES):
            candidates, next_pn, kind = self._search(query, pn)
            kinds.append(kind)
            for item in candidates:
                recent.append(item)
                self._check_stop()
                if got_one or len(self.items) >= self.count:
                    break
                title = item.get("title", "")
                entries = extract_collect_entries(item.get("text", ""))
                if not entries:
                    if details_used >= PRECISE_MAX_DETAIL:
                        continue
                    details_used += 1
                    title, _, entries = self._detail(item)
                if not entries:
                    continue
                saw_link = True
                saw_pwd = saw_pwd or any(entry.get("pwd") for entry in entries)
                name = extract_resource_name(title) or word
                for entry in entries:
                    self._check_stop()
                    if self._add(name, entry["link"], entry["pwd"], item["url"]):
                        got_one = True
                        break
            if got_one or next_pn is None:
                break
            if next_pn <= pn:
                self.on_log(f"分页 pn={next_pn} 未继续前进，停止该查询翻页")
                break
            pn = next_pn
        return got_one, recent, kinds, saw_link, saw_pwd

    def _run_precise(self, words):
        self.count = len(words)
        pending = deque(self._shuffle([word for word in words if word not in self.done_keywords]))
        self._filler_left = self._pick_every(FILLER_EVERY_RANGE)
        self._open_left = self._pick_every(OPEN_EVERY_RANGE)
        # 原版恢复时，已处理的词（包括未命中）不再进入补量。
        resolved = set(self.done_keywords)
        retries, miss_window, empty_window = {}, [], []
        self.on_log(f"精准采集：每词最多 1 条，最多 {PRECISE_MAX_PAGES} 页 / {PRECISE_MAX_DETAIL} 个详情")
        while pending and len(self.items) < self.count:
            self._check_stop()
            word = pending.popleft()
            self._current_step = f"精准 · {len(self.done_keywords) + 1}/{len(words)}：{word}"
            got, recent, kinds, saw_link, saw_pwd = self._search_one_keyword(word)
            self.done_keywords.add(word)
            if got:
                resolved.add(word)
                self.found_keywords.add(word)
                self.stats["ok"] += 1
            self._save()
            self._progress()
            self.on_log(f"关键词完成：{word}，新增 {int(got)} 条，累计 {len(self.items)} 条")
            self._after_search(recent)
            if got:
                miss_window.clear()
                empty_window.clear()
                continue
            if not saw_link:
                self.stats["no_link"] += 1
            elif not saw_pwd:
                self.stats["no_pwd"] += 1
            if kinds and all(kind == "unknown" for kind in kinds):
                empty_window.append(word)
                if len(empty_window) < ABNORMAL_RISK_HITS:
                    continue
                window, empty_window = empty_window, []
            else:
                empty_window.clear()
                miss_window.append(word)
                if len(miss_window) < SILENT_RISK_MISS:
                    continue
                window, miss_window = miss_window, []
            if min(retries.get(term, 0) for term in window) >= MAX_SILENT_RETRY:
                self.on_log(f"连续 {len(window)} 个词已重搜一次仍无结果，继续后面的词")
                continue
            if not self.pool.on_silent_risk(window, page=False):
                self.on_log("健康探针正常，判定为确实没有资源，不重搜")
                continue
            self.stats["risk"] += 1
            for term in reversed(window):
                pending.appendleft(term)
                retries[term] = retries.get(term, 0) + 1
            self.on_log(f"恢复后重新搜索刚才的 {len(window)} 个关键词")
        self._save()
        if len(self.items) < self.count:
            self._supplement([word for word in words if word not in resolved])

    def _stage_days(self):
        # auto 的近月优先阶段由 _run_recent_rounds 限额，随后最多近三个月。
        # 保留调用端已有的显式阶段限制。
        stages = [(label, days or None) for days, label in FUZZY_DATE_BUCKETS]
        if self.time_range == "auto":
            return [("最近三个月", FUZZY_MAX_AGE_DAYS)]
        if self.time_range in (None, "全部"):
            return [("全部", None)]
        for label, days in stages:
            if self.time_range == label:
                return [(label, days)]
        raise ValueError("模糊模式 time_range 必须为 auto、最近一月、最近一年或全部")

    def _has_date_cap(self):
        return self.mode == "fuzzy" and self.time_range == "auto"

    @staticmethod
    def _known_date_within(item_date, days=FUZZY_MAX_AGE_DAYS):
        try:
            age = (date.today() - date.fromisoformat(item_date)).days
        except (TypeError, ValueError):
            return False
        return 0 <= age <= days

    def _process_fuzzy_page(self, candidates, days, *, keyword="", max_additions=None, coverage_page=""):
        before = len(self.items)
        for index, item in enumerate(candidates):
            self._check_stop()
            if len(self.items) >= self.count:
                break
            item_date = item.get("date", "")
            in_range = (self._known_date_within(item_date, days or FUZZY_MAX_AGE_DAYS)
                        if self._has_date_cap() else within_range(item_date, days))
            if not in_range:
                self.stats["filtered_date"] += 1
                continue
            title = item.get("title", "")
            if not has_concrete_name(title):
                self.stats["skipped_generic"] += 1
                continue
            real_title, text, entries = self._detail(item)
            if not entries:
                self.stats["no_link"] += 1
                continue
            if not any(entry.get("pwd") for entry in entries):
                self.stats["no_pwd"] += 1
            name = extract_resource_name(real_title or title)
            if not name:
                name = guess_resource_name(real_title or title, text, entries[0].get("link", ""))
            if not name:
                continue
            for entry in entries:
                self._check_stop()
                if self._add(name, entry["link"], entry["pwd"], item["url"],
                             item_date=item_date, item_keyword=keyword, coverage_page=coverage_page):
                    if max_additions is not None and len(self.items) - before >= max_additions:
                        return index + 1 < len(candidates)
                    break  # 原版每个问题只收录首个成功结果。
        return False

    def _run_recent_rounds(self, words):
        """每轮每词仅查一个句式的首页；三轮未采足才放宽至近三个月。"""
        idle = abnormal = 0
        for round_number, variant in enumerate(KEYWORD_VARIANTS[:RECENT_PRIORITY_ROUNDS], 1):
            for word in words:
                self._check_stop()
                if len(self.items) >= self.count:
                    return
                query = variant.format(kw=word)
                marker = f"最近一月|{query}|0"
                if marker in self.done_queries or f"最近一月:{query}" in self.done_queries:
                    continue
                self.done_queries.add(marker)
                self._current_step = f"最近一月第 {round_number}/{RECENT_PRIORITY_ROUNDS} 轮 · {query}"
                before = len(self.items)
                before_hits = before + self.stats["no_pwd"]
                before_date = self.stats["filtered_date"]
                candidates, _, kind = self._search(query, 0)
                abnormal = abnormal + 1 if not candidates and kind == "unknown" else 0
                if abnormal >= ABNORMAL_RISK_HITS:
                    abnormal = idle = 0
                    self.pool.on_silent_risk(None, page=True)
                self._process_fuzzy_page(candidates, 30, keyword=self._fuzzy_term_owners.get(word, word))
                self._after_search(candidates)
                self._progress()
                added = len(self.items) - before
                filtered = self.stats["filtered_date"] - before_date
                self.on_log(f"近月第 {round_number}/{RECENT_PRIORITY_ROUNDS} 轮 · {word}：新增 {added} 条"
                            + (f"；日期超出范围或无法确认，过滤 {filtered} 条" if filtered else ""))
                idle = 0 if len(self.items) + self.stats["no_pwd"] > before_hits else idle + 1
                self.stats.update(idle_pages=idle, barren_pages=idle)
                self._save()
                if idle >= FUZZY_IDLE_PAGES:
                    idle = 0
                    self.pool.on_silent_risk(None, page=True)

    def _run_fuzzy(self, words):
        self._filler_left = self._pick_every(FILLER_EVERY_RANGE)
        self._open_left = self._pick_every(OPEN_EVERY_RANGE)
        self._fuzzy_term_owners = {word: word for word in words}
        for word in words:
            for term in DOMAIN_EXTRA_KEYWORDS.get(word, []):
                self._fuzzy_term_owners.setdefault(term, word)
        if self._has_date_cap() and len(words) > 1:
            FuzzyCoverage(self, words, RECENT_PRIORITY_ROUNDS, FUZZY_MAX_AGE_DAYS).run()
        else:
            self._run_fuzzy_fill(words)

    def _run_fuzzy_fill(self, words):
        coverage_pages = set(self._item_coverage_pages.values()) if self._has_date_cap() else set()
        expanded = []
        for word in words:
            for term in [word] + DOMAIN_EXTRA_KEYWORDS.get(word, []):
                if term not in expanded:
                    expanded.append(term)
        if self.time_range == "auto":
            self.on_log(f"优先最近一月，最多 {RECENT_PRIORITY_ROUNDS} 轮，每轮每个关键词查一页；未采足最多放宽到最近三个月（90天）")
            self._run_recent_rounds(expanded)
            self._check_stop()
            if len(self.items) < self.count:
                message = (f"最近一月已完成 {RECENT_PRIORITY_ROUNDS} 轮，已采 {len(self.items)}/{self.count} 条；"
                           "放宽到最近三个月（90天）继续采集，不再扩大时间范围…")
                self.on_status(message)
                self.on_log(message)
        idle = barren = abnormal = 0
        for stage_index, (stage, days) in enumerate(self._stage_days()):
            if len(self.items) >= self.count or barren >= FUZZY_MAX_BARREN_PAGES:
                break
            if stage_index:
                self.on_status(f"时间范围自动放宽到「{stage}」继续采集…")
            for word in expanded:
                for variant in KEYWORD_VARIANTS:
                    self._check_stop()
                    if len(self.items) >= self.count or barren >= FUZZY_MAX_BARREN_PAGES:
                        break
                    query = variant.format(kw=word)
                    pn, query_barren = 0, 0
                    while pn is not None:
                        self._check_stop()
                        if len(self.items) >= self.count or barren >= FUZZY_MAX_BARREN_PAGES:
                            break
                        marker = f"{stage}|{query}|{pn}"
                        if marker in self.done_queries or f"{stage}:{query}" in self.done_queries:
                            break
                        self.done_queries.add(marker)
                        self._current_step = f"{stage} · {query}"
                        before = len(self.items)
                        before_hits = before + self.stats["no_pwd"]
                        before_date = self.stats["filtered_date"]
                        candidates, next_pn, kind = self._search(query, pn)
                        abnormal = abnormal + 1 if not candidates and kind == "unknown" else 0
                        if abnormal >= ABNORMAL_RISK_HITS:
                            abnormal = idle = 0
                            self.pool.on_silent_risk(None, page=True)
                        self._process_fuzzy_page(candidates, days, keyword=self._fuzzy_term_owners.get(word, word))
                        self._after_search(candidates)
                        self._progress()
                        added = len(self.items) - before
                        filtered = self.stats["filtered_date"] - before_date
                        self.on_log(f"第 {pn // 10 + 1} 页处理完成：新增 {added} 条"
                                    + (f"；日期超出范围或无法确认，过滤 {filtered} 条" if filtered else ""))
                        if len(self.items) + self.stats["no_pwd"] > before_hits or marker in coverage_pages:
                            # 覆盖阶段已在此页入表，重放时虽去重，仍须保留其后续分页。
                            idle = barren = query_barren = 0
                        else:
                            idle += 1
                            barren += 1
                            query_barren += 1
                        self.stats.update(idle_pages=idle, barren_pages=barren)
                        self._save()
                        if query_barren >= FUZZY_BARREN_PAGES:
                            self.on_log(f"当前查询连续 {query_barren} 页没有新增结果，结束本轮翻页")
                            break
                        if idle >= FUZZY_IDLE_PAGES:
                            idle = 0
                            self.pool.on_silent_risk(None, page=True)
                        if next_pn is not None and next_pn <= pn:
                            self.on_log(f"分页 pn={next_pn} 未继续前进，停止该查询翻页")
                            break
                        pn = next_pn
        self._save()
        if len(self.items) < self.count:
            self._supplement(expanded)
            if self._has_date_cap():
                self.on_log(f"最近三个月（90天）内共采到 {len(self.items)}/{self.count} 条，不再扩大时间范围")

    def _supplement(self, terms):
        if not terms or not self._supplement_enabled or self.stop_event.is_set() or len(self.items) >= self.count:
            return
        if self._has_date_cap():
            self.on_log("网页补量无法确认发布日期，本次跳过，确保只采集最近三个月内的结果")
            return
        self.pool.start()
        collector = self.supplement_factory(
            CachedFetcher(PinnedExitFetcher(self.pool, "direct"), self._page_cache), self.stop_event)
        barren = 0
        for term in terms:
            self._check_stop()
            if len(self.items) >= self.count or getattr(collector, "stop_supplement", False):
                break
            self.on_status(f"正在网页补量：{term}，已采 {len(self.items)} 条")
            before, stream = len(self.items), None
            try:
                search = getattr(collector, "iter_search", None) or collector.search
                stream = search(term, self.on_log)
                for record in stream:
                    self._check_stop()
                    name = extract_resource_name(record.get("title", "")) or term
                    for entry in record.get("links", []):
                        self._check_stop()
                        if self._add(name, entry.get("link", ""), entry.get("pwd", ""), record.get("source_url", "")):
                            if self.mode == "precise":
                                self.found_keywords.add(term)
                                self._save()
                                break
                        if len(self.items) >= self.count:
                            break
                    self._check_stop()
                    if len(self.items) >= self.count or (self.mode == "precise" and len(self.items) > before):
                        break
            except CollectCancelled:
                raise
            except PoolError as exc:
                self.on_log(f"补量提前结束：{exc}")
                raise
            finally:
                close = getattr(stream, "close", None)
                if close:
                    close()
            barren = 0 if len(self.items) > before else barren + 1
            if barren >= SUPPLEMENT_MAX_BARREN:
                self.on_log(f"网页补量连续 {barren} 个关键词无新增，停止补量")
                break

    def run(self):
        self._page_cache.reset()
        words = self._keywords()
        if not words:
            raise ValueError("采集需要关键词")
        self.count = len(words) if self.mode == "precise" else max(1, self.count)
        self._source_words = words
        self._restore()
        self.on_log(f"开始{'精准' if self.mode == 'precise' else '模糊'}采集：共 {len(words)} 个关键词，当前已采 {len(self.items)} 条")
        completed = False
        try:
            self._check_stop()
            self.pool.start()
            if self.mode == "precise":
                self._run_precise(words)
            else:
                self._run_fuzzy(words)
            completed = not self.stop_event.is_set()
        except CollectCancelled:
            self.stop_event.set()
        finally:
            try:
                self._save()
                if completed:
                    self._clear_checkpoint()
                if self._page_cache.hits:
                    self.on_log(f"本轮复用已获取页面 {self._page_cache.hits} 次，减少相同次数的重复页面请求。")
            finally:
                self._page_cache.clear()
        return {"items": list(self.items), "found": len(self.items),
                "stats": dict(self.stats), "stopped": self.stop_event.is_set()}
