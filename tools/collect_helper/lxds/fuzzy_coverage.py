"""Default fuzzy mode: reserve the first results for distinct input keywords."""
from collections import deque

from lxds.collection_rules import (
    KEYWORD_VARIANTS, DOMAIN_EXTRA_KEYWORDS, FUZZY_BARREN_PAGES,
    FUZZY_IDLE_PAGES, ABNORMAL_RISK_HITS,
)


class FuzzyCoverage:
    def __init__(self, worker, words, recent_rounds, max_days):
        self.worker = worker
        self.words = words
        self.rounds = recent_rounds
        self.max_days = max_days
        self.groups = {word: list(dict.fromkeys([word] + DOMAIN_EXTRA_KEYWORDS.get(word, [])))
                       for word in words}
        self.covered = set(worker._item_keywords.values()).intersection(words)
        self.idle = self.abnormal = 0

    def _done(self):
        self.worker._check_stop()
        return len(self.worker.items) >= self.worker.count or len(self.covered) == len(self.words)

    def _terms(self):
        # Original inputs get a turn before any domain expansion does.
        for index in range(max(map(len, self.groups.values()))):
            for owner in self.words:
                if owner not in self.covered and index < len(self.groups[owner]):
                    yield owner, self.groups[owner][index]

    def _visit(self, owner, query, pn, days, label):
        worker = self.worker
        worker._current_step = f"关键词覆盖 · {owner} · {label} · {query}"
        before = len(worker.items)
        before_date = worker.stats['filtered_date']
        candidates, next_pn, kind = worker._search(query, pn)
        self.abnormal = self.abnormal + 1 if not candidates and kind == 'unknown' else 0
        if self.abnormal >= ABNORMAL_RISK_HITS:
            self.abnormal = self.idle = 0
            worker.pool.on_silent_risk(None, page=True)
        stage = "最近一月" if days == 30 else "最近三个月"
        remaining = worker._process_fuzzy_page(candidates, days, keyword=owner, max_additions=1,
                                               coverage_page=f"{stage}|{query}|{pn}")
        added = len(worker.items) > before
        if added:
            self.covered.add(owner)
            worker.on_log(f"关键词「{owner}」已采到首条结果，优先继续覆盖其他关键词")
        filtered = worker.stats['filtered_date'] - before_date
        if filtered:
            worker.on_log(f"关键词「{owner}」本页日期超出范围或无法确认，过滤 {filtered} 条")
        worker._after_search(candidates)
        worker._progress()
        self.idle = 0 if added else self.idle + 1
        worker.stats.update(idle_pages=self.idle, barren_pages=self.idle)
        if self.idle >= FUZZY_IDLE_PAGES:
            self.idle = 0
            worker.pool.on_silent_risk(None, page=True)
        return next_pn, remaining, added

    def _recent(self):
        worker = self.worker
        for round_number, variant in enumerate(KEYWORD_VARIANTS[:self.rounds], 1):
            for owner, term in self._terms():
                if self._done():
                    return
                query = variant.format(kw=term)
                marker = f"最近一月|{query}|0"
                if marker in worker.done_queries or f"最近一月:{query}" in worker.done_queries:
                    continue
                _, remaining, _ = self._visit(owner, query, 0, 30,
                    f"最近一月第 {round_number}/{self.rounds} 轮")
                # Leave a partly consumed normal page available for cached filling.
                if not remaining:
                    worker.done_queries.add(marker)
                worker._save()

    def _three_months(self):
        worker = self.worker
        budget = len(KEYWORD_VARIANTS) * FUZZY_BARREN_PAGES
        used = dict.fromkeys(self.words, 0)
        queue = deque((owner, variant.format(kw=term), 0, 0)
                      for variant in KEYWORD_VARIANTS for owner, term in self._terms())
        while queue and not self._done():
            owner, query, pn, pages = queue.popleft()
            if owner in self.covered or used[owner] >= budget:
                continue
            completed = f"最近三个月:{query}"
            if completed in worker.done_queries:
                continue
            used[owner] += 1
            next_pn, remaining, added = self._visit(owner, query, pn, self.max_days, "最近三个月")
            pages += 1
            if next_pn is not None and next_pn <= pn:
                worker.on_log(f"分页 pn={next_pn} 未继续前进，停止该查询翻页")
                next_pn = None
            if not remaining and (next_pn is None or (not added and pages >= FUZZY_BARREN_PAGES)):
                worker.done_queries.add(completed)
            elif not added and pages < FUZZY_BARREN_PAGES and next_pn is not None:
                queue.append((owner, query, next_pn, pages))
            worker._save()

    def run(self):
        worker = self.worker
        worker.on_log(f"先覆盖 {len(self.words)} 个关键词，每词优先 1 条，再补足总目标 {worker.count} 条")
        if worker.count < len(self.words):
            worker.on_log("目标条数少于关键词数，按输入顺序优先覆盖不同词，达到总目标即停止")
        if not self._done():
            self._recent()
        if not self._done():
            worker.on_status(f"最近一月已完成 {self.rounds} 轮，优先在最近三个月（90天）内补齐未覆盖关键词")
            self._three_months()
        worker._check_stop()
        if len(worker.items) >= worker.count:
            return
        for word in self.words:
            if word not in self.covered:
                worker.on_log(f"关键词「{word}」在本轮90天内搜索预算中未找到合格且不重复的结果，由其他关键词补足")
        worker.on_log(f"关键词覆盖阶段结束：{len(self.covered)}/{len(self.words)} 个词已命中；继续补足总数，复用已获取页面")
        worker._run_fuzzy_fill(self.words)
