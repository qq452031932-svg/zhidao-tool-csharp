"""Regression cases for behavior verified in the supplied v1.0.4 package."""
from datetime import date, timedelta
from pathlib import Path
import sys
import tempfile
import unittest
from urllib.parse import parse_qs, urlsplit

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from lxds.collect import CollectWorker
from lxds.zhidao_parse import parse_search_page, within_range


def result_block(qid, title, text='', when=''):
    # Real result markup can put the date much farther than 350 characters after the title.
    return (f'<dl><dt><a href="https://zhidao.baidu.com/question/{qid}.html?fr=search">{title}</a></dt>'
            f'<dd class="{"layout-class-" * 70}">{text}</dd><dd>{when}</dd></dl>')


class Pool:
    def start(self):
        pass

    def on_silent_risk(self, *args, **kwargs):
        pass


class Fetcher:
    def __init__(self, pages, details=None):
        self.pages = pages
        self.details = details or {}
        self.searches = []
        self.detail_requests = []

    def get(self, url, **kwargs):
        if '/question/' in url:
            self.detail_requests.append(url)
            qid = url.split('/question/')[1].split('.')[0]
            body = self.details.get(qid, '该问题已被删除')
            # v1.0.4 rejects truncated (<2000 character) detail responses.
            return body + '<!--' + 'x' * 2100 + '-->', None
        query = parse_qs(urlsplit(url).query)
        word, pn = query['word'][0], int(query['pn'][0])
        self.searches.append((word, pn))
        return self.pages.get(pn, '没有找到相关结果'), None


class CollectionCompatibilityTests(unittest.TestCase):
    def run_worker(self, fetcher, **kwargs):
        with tempfile.TemporaryDirectory(prefix='collect-compat-') as temporary:
            worker = CollectWorker(keyword='植物大战僵尸', mode='fuzzy', count=1,
                                   fetcher=fetcher, pool=Pool(),
                                   checkpoint_path=str(Path(temporary) / 'checkpoint.json'), **kwargs)
            return worker.run()

    def test_search_keeps_full_result_date_and_snippet_without_crossing_into_next_result(self):
        today = date.today().isoformat()
        html = (result_block('101', '求《植物大战僵尸》链接', '分享 https://pan.baidu.com/s/dummy-one 提取码：ab12', today)
                + result_block('102', '无日期的结果')
                + result_block('103', '有日期的另一条', when='2001-01-01'))
        items, _ = parse_search_page(html)
        self.assertEqual(today, items[0]['date'])
        self.assertIn('https://pan.baidu.com/s/dummy-one', items[0].get('text', ''))
        self.assertEqual('', items[1]['date'])
        self.assertEqual('2001-01-01', items[2]['date'])
        self.assertNotIn('有日期的另一条', items[1].get('text', ''))

    def test_missing_or_unreadable_date_remains_eligible_like_legacy(self):
        for value in ('', 'unknown', '2026-99-99'):
            with self.subTest(value=value):
                self.assertTrue(within_range(value, 30))
        old = (date.today() - timedelta(days=120)).isoformat()
        self.assertFalse(within_range(old, 30))
        self.assertTrue(within_range(old, 0))

    def test_result_summary_does_not_take_links_or_dates_from_outside_its_container(self):
        first = result_block('101', '求《植物大战僵尸》链接')
        for trailing in (
            '<aside>推荐 https://pan.baidu.com/s/dummy-other 2001-01-01</aside>',
            '<dl><dt><a href="https://zhidao.baidu.com/question/102.html">另一结果</a></dt>'
            '<dd>https://pan.baidu.com/s/dummy-other 2001-01-01</dd></dl>',
        ):
            for block in (first, first.replace('<dl>', '<div class="result">').replace('</dl>', '</div>')):
                with self.subTest(trailing=trailing, block=block):
                    items, _ = parse_search_page(block + trailing)
                    self.assertEqual('', items[0]['date'])
                    self.assertNotIn('dummy-other', items[0]['text'])

    def test_title_date_does_not_replace_result_publication_date(self):
        today = date.today().isoformat()
        html = result_block('101', '求《2001-01-01录播》链接', when=today)
        items, _ = parse_search_page(html)
        self.assertEqual(today, items[0]['date'])

    def test_undated_candidate_is_read_in_first_stage(self):
        fetcher = Fetcher({0: result_block('101', '求《植物大战僵尸》链接')},
                          {'101': '<div class="best-text">https://pan.baidu.com/s/dummy-one 提取码：ab12</div>'})
        result = self.run_worker(fetcher, time_range='最近一月')
        self.assertEqual(1, result['found'])
        self.assertEqual(1, len(fetcher.detail_requests))

    def test_search_snippet_link_survives_without_fetching_deleted_detail(self):
        today = date.today().isoformat()
        for detail_mode in (True, False):
            with self.subTest(detail_mode=detail_mode):
                fetcher = Fetcher({0: result_block('101', '求《植物大战僵尸》链接',
                    '分享 https://pan.baidu.com/s/dummy-one 提取码：ab12', today)})
                result = self.run_worker(fetcher, detail_mode=detail_mode)
                self.assertEqual(1, result['found'])
                self.assertEqual('ab12', result['items'][0]['提取码'])
                self.assertEqual('植物大战僵尸', result['items'][0]['资源名称'])
                self.assertEqual([], fetcher.detail_requests)

    def test_three_recent_rounds_widen_without_deep_pagination(self):
        old = (date.today() - timedelta(days=60)).isoformat()
        pages = {pn: result_block('101', '求《植物大战僵尸》链接', when=old)
                 + (f'<a class="pager-next" href="/search?word=x&pn={pn + 10}">下一页</a>' if pn < 90 else '')
                 for pn in range(0, 100, 10)}
        fetcher = Fetcher(pages, {'101': '<div class="best-text">https://pan.baidu.com/s/dummy-one 提取码：ab12</div>'})
        logs = []
        result = self.run_worker(fetcher, on_log=logs.append)
        searches = [pn for word, pn in fetcher.searches if word == '求植物大战僵尸百度网盘链接']
        # Widening reprocesses the first page under the new date range from cache.
        self.assertEqual([0], searches)
        self.assertEqual(3, len(fetcher.searches))
        self.assertEqual(1, result['found'])
        self.assertTrue(any('日期' in line and '过滤' in line for line in logs))
        self.assertTrue(any('3 轮' in line and '最近三个月' in line for line in logs))
        self.assertTrue(any('复用已获取页面' in line for line in logs))


if __name__ == '__main__':
    unittest.main()
