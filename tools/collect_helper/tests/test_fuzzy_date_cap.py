"""Default fuzzy collection must never relax beyond 90 days."""
from datetime import date, timedelta
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from lxds.collect import CollectWorker
from lxds.zhidao_parse import collect_link_key
from test_legacy_collect_parity import Fetcher, Pool, question, share
from test_fuzzy_recent_budget import dated


class FuzzyDateCapTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.path = str(Path(temporary.name) / 'checkpoint.json')
        self.logs, self.status = [], []
        pacing = patch('lxds.collect.pick_every', return_value=100000)
        pacing.start()
        self.addCleanup(pacing.stop)

    def worker(self, fetcher, **options):
        return CollectWorker(keyword=['甲词'], mode='fuzzy', count=options.pop('count', 10),
            fetcher=fetcher, pool=Pool(), checkpoint_path=self.path,
            on_log=self.logs.append, on_status=self.status.append, **options)

    def test_three_rounds_then_ninety_days_preserve_new_and_reject_older_rows(self):
        body = (dated(1001, '本月书', 5, '1abcdefgh')
                + dated(1002, '上月书', 60, '1ijklmnop')
                + dated(1003, '九十天书', 90, '1qrstuvwx')
                + dated(1004, '过期书', 91, '1yzabcdef'))
        result = self.worker(Fetcher(lambda w,p: body), count=4).run()
        self.assertEqual(['本月书', '上月书', '九十天书'], [row['资源名称'] for row in result['items']])
        self.assertTrue(any('最近三个月' in line for line in self.status))
        self.assertFalse(any('不限发布时间' in line for line in self.logs + self.status))
        self.assertTrue(any('3/4' in line and '不再扩大' in line for line in self.logs))

    def test_unknown_invalid_and_future_dates_cannot_be_accepted(self):
        invalid = question(1002, '异常日期书', share('1ijklmnop')).replace('</dd>', '</dd><dd>2026-99-99</dd>')
        body = (question(1001, '无日期书', share('1abcdefgh')) + invalid
                + dated(1003, '未来书', -1, '1qrstuvwx')
                + dated(1004, '当天书', 0, '1yzabcdef'))
        result = self.worker(Fetcher(lambda w,p: body)).run()
        self.assertEqual(['当天书'], [row['资源名称'] for row in result['items']])

    def test_undated_supplement_is_not_started_under_strict_cap(self):
        started = []
        class Supplement:
            def iter_search(self, term, log):
                started.append(term)
                yield {'title': '过期书', 'source_url': 'fixture',
                       'links': [{'link':'https://pan.baidu.com/s/1abcdefgh', 'pwd':'a123'}]}
        result = self.worker(Fetcher(), supplement_factory=lambda *args: Supplement()).run()
        self.assertEqual(0, result['found'])
        self.assertEqual([], started)
        self.assertTrue(any('补量' in line and '日期' in line for line in self.logs))

    def test_resume_discards_legacy_undated_rows_and_researches(self):
        row = {'资源名称':'旧断点书', '资源链接':'https://pan.baidu.com/s/1abcdefgh',
               '提取码':'a123', '来源链接':'fixture'}
        query = '求甲词百度网盘链接'
        Path(self.path).write_text(json.dumps({'mode':'fuzzy', 'items':[row],
            'seen_names':['旧断点书'], 'seen_links':[row['资源链接']],
            'done_queries':[f'最近一月|{query}|0']}), encoding='utf-8')
        result = self.worker(Fetcher(lambda w,p:dated(1001, '近期书', 5, '1abcdefgh')),
                             resume=True, count=1).run()
        self.assertEqual(['近期书'], [item['资源名称'] for item in result['items']])

    def test_stopped_row_keeps_date_and_can_resume_without_reemitting_it(self):
        first = self.worker(Fetcher(lambda w,p:dated(1001, '近期书', 5, '1abcdefgh')), count=2)
        first.on_item = lambda row:first.stop()
        self.assertEqual(1, first.run()['found'])
        payload = json.loads(Path(self.path).read_text(encoding='utf-8'))
        key = collect_link_key(payload['items'][0]['资源链接'])
        self.assertEqual((date.today()-timedelta(days=5)).isoformat(), payload.get('item_dates', {}).get(key))
        second = self.worker(Fetcher(lambda w,p:dated(1002, '上月书', 60, '1ijklmnop')), resume=True, count=2)
        self.assertEqual(['近期书', '上月书'], [row['资源名称'] for row in second.run()['items']])

    def test_previously_valid_checkpoint_row_ages_out(self):
        row = {'资源名称':'已过期书', '资源链接':'https://pan.baidu.com/s/1abcdefgh',
               '提取码':'a123', '来源链接':'fixture'}
        Path(self.path).write_text(json.dumps({'mode':'fuzzy', 'items':[row],
            'item_dates':{collect_link_key(row['资源链接']):(date.today()-timedelta(days=91)).isoformat()}}),encoding='utf-8')
        self.assertEqual(0, self.worker(Fetcher(), resume=True).run()['found'])


if __name__ == '__main__':
    unittest.main()
