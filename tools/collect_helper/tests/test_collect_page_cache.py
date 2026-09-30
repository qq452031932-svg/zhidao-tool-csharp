"""Task-local reuse must reduce requests without changing accepted resources."""
from datetime import date, timedelta
from pathlib import Path
import sys
import tempfile
from threading import Event
import unittest
from unittest.mock import patch
from urllib.parse import parse_qs, urlsplit

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from lxds.collect import CollectWorker
from lxds.pool import CollectCancelled, PoolError, SlotPool
from lxds.page_cache import TaskPageCache, CachedFetcher
from test_legacy_collect_parity import Pool, Fetcher, question, share, detail


class PageReuseTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.pace = patch('lxds.collect.pick_every', return_value=100000)
        self.pace.start()
        self.addCleanup(self.pace.stop)

    def worker(self, fetcher, **options):
        return CollectWorker(keyword=['taptap'], mode='fuzzy', count=10, fetcher=fetcher,
            pool=Pool(), checkpoint_path=str(Path(self.temp.name) / 'cp.json'), **options)

    def test_same_normal_search_response_reused_across_date_stages(self):
        recent = (date.today() - timedelta(days=40)).isoformat()
        old = (date.today() - timedelta(days=60)).isoformat()
        pages = (question(1001, '一年内书', share('1abcdefgh')).replace('</dd>', f'</dd><dd>{recent}</dd>')
                 + question(1002, '历史藏书', share('1ijklmnop')).replace('</dd>', f'</dd><dd>{old}</dd>'))
        fetcher = Fetcher(lambda word, pn: pages)
        worker = self.worker(fetcher)
        worker.count = 2
        rows = worker.run()['items']
        self.assertEqual(['一年内书', '历史藏书'], [row['资源名称'] for row in rows])
        # Three recent rounds; the 90-day stage reuses the same normal page.
        self.assertEqual(3, len(fetcher.searches))

    def test_same_detail_reused_across_variants_and_new_worker_refetches(self):
        fetcher = Fetcher(lambda word, pn: question())
        worker = self.worker(fetcher, time_range='全部')
        self.assertEqual(1, worker.run()['found'])
        self.assertEqual(['1001'], fetcher.details)
        another = self.worker(fetcher, time_range='全部')
        self.assertEqual(1, another.run()['found'])
        self.assertEqual(['1001', '1001'], fetcher.details)

    def test_repeated_normal_empty_and_unknown_searches_are_not_cached(self):
        for body in ('没有找到相关结果', '结构异常'):
            with self.subTest(body=body):
                fetcher = Fetcher(lambda word, pn: body)
                worker = self.worker(fetcher)
                worker._search('taptap', 0)
                worker._search('taptap', 0)
                self.assertEqual(2, len(fetcher.searches))

    def test_detail_url_forms_share_response_without_reusing_other_question(self):
        fetcher = Fetcher()
        worker = self.worker(fetcher)
        first = worker._fetch_detail({'url':'https://zhidao.baidu.com/question/1001.html?fr=search'})
        self.assertEqual(first, worker._fetch_detail({'url':'https://zhidao.baidu.com/question/1001'}))
        worker._fetch_detail({'url':'https://zhidao.baidu.com/question/1002'})
        self.assertEqual(2, len(fetcher.details))

    def test_recovered_pool_generation_forces_fresh_normal_page(self):
        fetcher = Fetcher(lambda word, pn: question())
        worker = self.worker(fetcher)
        worker.pool.cache_generation = 0
        worker._search('taptap', 0)
        worker._search('taptap', 0)
        worker.pool.cache_generation += 1
        worker._search('taptap', 0)
        self.assertEqual(2, len(fetcher.searches))

    def test_stop_is_checked_before_cache_hit(self):
        fetcher = Fetcher(lambda word, pn: question())
        worker = self.worker(fetcher)
        worker._search('taptap', 0)
        worker.stop()
        with self.assertRaises(CollectCancelled):
            worker._search('taptap', 0)
        self.assertEqual(1, len(fetcher.searches))

    def test_target_ten_is_global_with_coverage_and_no_auxiliary_after_completion(self):
        words = ['首词', '第二词', '第三词', '第四词', '第五词']
        pages = ''.join(question(1000+i, f'测试藏书{i}', share(f'1resource{i}')) for i in range(10))
        pages = pages.replace('</dd>', f'</dd><dd>{date.today().isoformat()}</dd>')
        fetcher = Fetcher(lambda word, pn: pages)
        worker = self.worker(fetcher)
        worker.keyword = words
        worker._pick_every = lambda bounds: 1  # Both auxiliary actions due now.
        counts_at_target = []
        def on_item(row):
            if len(worker.items) == 10:
                counts_at_target.append((len(fetcher.searches),len(fetcher.details)))
        worker.on_item = on_item
        result = worker.run()
        self.assertEqual(10, result['found'])
        queries = [f'求{word}百度网盘链接' for word in words]
        self.assertEqual([(query,0) for query in queries],[(query,pn) for query,pn in fetcher.searches if query in queries])
        self.assertEqual([(len(fetcher.searches),len(fetcher.details))],counts_at_target)

    def test_precise_target_also_skips_due_auxiliary_requests(self):
        fetcher = Fetcher(lambda word, pn: question(body=share()))
        worker = self.worker(fetcher)
        worker.mode = 'precise'
        worker._pick_every = lambda bounds: 1
        self.assertEqual(1, worker.run()['found'])
        self.assertEqual([('求taptap百度网盘链接', 0)], fetcher.searches)
        self.assertEqual([], fetcher.details)

    def test_finished_task_releases_cache_even_if_same_worker_reused(self):
        fetcher = Fetcher(lambda word, pn: question())
        worker = self.worker(fetcher, time_range='全部')
        worker.run()
        worker._fetch_detail({'url':'https://zhidao.baidu.com/question/1001.html'})
        self.assertEqual(2, len(fetcher.details))

    def test_main_and_site_supplement_share_normal_detail_page(self):
        fetcher = Fetcher(lambda word, pn: question())
        supplement_fetcher = Fetcher()
        class Supplement:
            def __init__(self, wrapped, stop):
                self.wrapped = wrapped
            def search(self, term, log):
                self.wrapped.get('https://zhidao.baidu.com/question/1001', purpose='search')
                return []
        worker = self.worker(fetcher, time_range='全部', supplement_factory=Supplement)
        with patch('lxds.collect_engine.PinnedExitFetcher', return_value=supplement_fetcher):
            self.assertEqual(1, worker.run()['found'])
        self.assertEqual([], supplement_fetcher.details)

    def test_pool_marks_both_explicit_and_silent_risk_generations(self):
        from unittest.mock import Mock
        pool = SlotPool([{'cookie':'offline'}], stop_event=Event())
        self.addCleanup(pool.slots[0].session.close)
        before = getattr(pool, 'cache_generation', 0)
        pool.on_risk(pool.slots[0])
        self.assertGreater(getattr(pool, 'cache_generation', 0), before)
        before = pool.cache_generation
        pool.probe_slot = Mock(return_value=True)
        pool.on_silent_risk()
        self.assertGreater(pool.cache_generation, before)


class CacheBoundaryTests(unittest.TestCase):
    def setUp(self):
        self.stop = Event()
        self.pool = Pool()
        self.pool.cache_generation = 0
        self.cache = TaskPageCache(self.stop, self.pool)
        self.url = 'https://zhidao.baidu.com/question/1001.html'

    def fetcher(self, respond):
        class Recorded:
            def __init__(inner):
                inner.calls = []
            def get(inner, url, **kwargs):
                inner.calls.append((url, kwargs))
                return respond(url), None
        raw = Recorded()
        return raw, CachedFetcher(raw, self.cache)

    def test_abnormal_risk_dead_and_truncated_detail_never_cached(self):
        for body in ('', '没有数据', '百度安全验证' + 'x'*3000,
                     '<div class="best-text">请输入验证码</div>' + 'x'*3000,
                     '该问题已被删除' + 'x'*3000, '<body>' + 'x'*3000 + '</body>'):
            with self.subTest(body=body[:30]):
                raw, fetcher = self.fetcher(lambda url: body)
                fetcher.get(self.url)
                fetcher.get(self.url)
                self.assertEqual(2, len(raw.calls))

    def test_site_search_and_detail_use_url_type_even_with_search_purpose(self):
        raw, fetcher = self.fetcher(lambda url: detail() if '/question/' in url
                                  else 'https://zhidao.baidu.com/question/1001')
        search = 'https://www.baidu.com/s?wd=test&pn=0'
        for url in (search, search, self.url, self.url):
            fetcher.get(url, purpose='search', referer='https://www.baidu.com/', timeout=12)
        self.assertEqual(2, len(raw.calls))
        self.assertEqual({'purpose':'search', 'referer':'https://www.baidu.com/', 'timeout':12}, raw.calls[1][1])

    def test_unrecognised_host_and_search_pages_with_different_queries_do_not_collide(self):
        raw, fetcher = self.fetcher(lambda url: question())
        urls = ['https://zhidao.baidu.com/search?word=a&pn=0',
                'https://zhidao.baidu.com/search?word=b&pn=0',
                'https://zhidao.baidu.com/search?word=a&pn=10']
        for url in urls*2:
            fetcher.get(url)
        self.assertEqual(3, len(raw.calls))
        for _ in range(2):
            fetcher.get('https://zhidao.baidu.com.evil.test/search?word=a&pn=0')
        self.assertEqual(5, len(raw.calls))

    def test_lru_capacity_evicts_oldest_but_keeps_recent_page(self):
        self.cache = TaskPageCache(self.stop, self.pool, max_entries=2)
        raw, fetcher = self.fetcher(lambda url: detail())
        urls = [f'https://zhidao.baidu.com/question/{i}' for i in range(3)]
        for index in (0, 1, 0, 2, 0, 1):
            fetcher.get(urls[index])
        self.assertEqual([urls[i] for i in (0, 1, 2, 1)], [url for url, _ in raw.calls])

    def test_size_limit_bounds_total_and_bypasses_oversized_page(self):
        size = len(detail())
        self.cache = TaskPageCache(self.stop, self.pool, max_chars=size)
        raw, fetcher = self.fetcher(lambda url: detail())
        for url in (self.url, self.url+'2', self.url):
            fetcher.get(url)
        # The middle URL has no valid question path, so it must not evict the first.
        self.assertEqual(2, len(raw.calls))
        fetcher.get('https://zhidao.baidu.com/question/1002')
        fetcher.get(self.url)
        self.assertEqual(4, len(raw.calls))
        self.cache = TaskPageCache(self.stop, self.pool, max_chars=size-1)
        raw, fetcher = self.fetcher(lambda url: detail())
        fetcher.get(self.url)
        fetcher.get(self.url)
        self.assertEqual(2, len(raw.calls))

    def test_error_and_stop_during_request_cannot_store_page(self):
        def failing(url):
            raise PoolError('offline fixture')
        raw, fetcher = self.fetcher(failing)
        for _ in range(2):
            with self.assertRaises(PoolError):
                fetcher.get(self.url)
        self.assertEqual(2, len(raw.calls))
        def stopped(url):
            self.stop.set()
            return detail()
        raw, fetcher = self.fetcher(stopped)
        with self.assertRaises(CollectCancelled):
            fetcher.get(self.url)
        self.stop.clear()
        raw2, healthy = self.fetcher(lambda url: detail())
        healthy.get(self.url)
        self.assertEqual(1, len(raw2.calls))

    def test_risk_during_underlying_request_discards_previous_pages(self):
        def respond(url):
            if url.endswith('/2002'):
                self.pool.cache_generation += 1
            return detail()
        raw, fetcher = self.fetcher(respond)
        for url in (self.url, 'https://zhidao.baidu.com/question/2002', self.url):
            fetcher.get(url)
        self.assertEqual(3, len(raw.calls))

    def test_cache_hit_observes_stop_without_network(self):
        raw, fetcher = self.fetcher(lambda url: detail())
        fetcher.get(self.url)
        self.stop.set()
        with self.assertRaises(CollectCancelled):
            fetcher.get(self.url)
        self.assertEqual(1, len(raw.calls))


if __name__ == '__main__':
    unittest.main()
