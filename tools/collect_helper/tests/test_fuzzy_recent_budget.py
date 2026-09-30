"""User's capped recency preference: three shallow rounds, then at most 90 days."""
from datetime import date, timedelta
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from lxds.collect import CollectWorker
from lxds.collection_rules import KEYWORD_VARIANTS
from test_legacy_collect_parity import Fetcher, Pool, question, share


def dated(qid, name, age, identity, next_pn=None):
    when = (date.today() - timedelta(days=age)).isoformat()
    return question(qid, name, share(identity), next_pn).replace('</dd>', f'</dd><dd>{when}</dd>')


class RecentBudgetTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = str(Path(self.temp.name)/'checkpoint.json')
        self.logs, self.status = [], []
        pacing = patch('lxds.collect.pick_every', return_value=100000)
        pacing.start()
        self.addCleanup(pacing.stop)
        shuffle = patch('lxds.collect.shuffle_blocks', lambda words, *args: list(words))
        shuffle.start()
        self.addCleanup(shuffle.stop)

    def worker(self, fetcher, **kwargs):
        return CollectWorker(keyword=kwargs.pop('keyword', ['甲词','乙词']), mode=kwargs.pop('mode','fuzzy'),
            count=kwargs.pop('count',2), fetcher=fetcher, pool=Pool(), checkpoint_path=self.path,
            on_log=self.logs.append, on_status=self.status.append, **kwargs)

    def test_three_rounds_visit_each_word_once_then_widen_to_three_months(self):
        searches = []
        def response(word, pn):
            return dated(1001,'历史藏书',60,'1abcdefgh',next_pn=pn+10)
        worker = self.worker(Fetcher(response), count=1)
        original_search = worker._search
        def recorded(word,pn):
            searches.append((worker._current_step,word,pn))
            return original_search(word,pn)
        worker._search = recorded
        result = worker.run()
        self.assertEqual(1,result['found'])
        self.assertEqual([(v.format(kw=w),0) for v in KEYWORD_VARIANTS[:3] for w in ('甲词','乙词')],
                         [(word,pn) for step,word,pn in searches[:6]])
        self.assertEqual(7,len(searches))
        self.assertIn('最近三个月',searches[-1][0])
        self.assertFalse(any('一年' in text for text in self.status))

    def test_partial_recent_results_are_retained_and_older_rows_fill_remaining_target(self):
        body=dated(1001,'近期藏书',5,'1abcdefgh')+dated(1002,'历史藏书',60,'1ijklmnop')
        worker=self.worker(Fetcher(lambda w,p:body))
        result=worker.run()
        self.assertEqual(['近期藏书','历史藏书'],[row['资源名称'] for row in result['items']])
        self.assertTrue(any('3 轮' in text and '最近三个月' in text for text in self.status))

    def test_target_in_first_round_does_not_search_later_words_or_widen(self):
        fetcher=Fetcher(lambda w,p:dated(1001,'近期藏书',5,'1abcdefgh'))
        result=self.worker(fetcher,count=1).run()
        self.assertEqual(1,result['found'])
        self.assertEqual([(KEYWORD_VARIANTS[0].format(kw='甲词'),0)],fetcher.searches)
        self.assertFalse(any('不限' in text for text in self.status))

    def test_five_words_with_nine_recent_hits_still_widen_after_fifteen_pages(self):
        body = ''.join(dated(1000+i, f'近期藏书{i}', 5, f'1recent{i:03}') for i in range(9))
        body += dated(1010, '历史藏书', 60, '1abcdefgh', next_pn=10)
        fetcher = Fetcher(lambda w,p:body)
        words = ['甲词', '乙词', '丙词', '丁词', '戊词']
        worker = self.worker(fetcher, keyword=words, count=10)
        stages = []
        search = worker._search
        def recorded(word, pn):
            stages.append((worker._current_step, word, pn))
            return search(word, pn)
        worker._search = recorded
        result = worker.run()
        self.assertEqual(10, result['found'])
        self.assertEqual('历史藏书', result['items'][-1]['资源名称'])
        # Coverage and filling may read the same cached page separately, without
        # increasing the three-round set of queries or actual page requests.
        self.assertEqual(15,len({(word,pn) for step,word,pn in stages if '最近一月' in step}))
        self.assertTrue(all('最近一月' in step and pn == 0 for step,word,pn in stages[:-1]))
        self.assertIn('最近三个月', stages[-1][0])
        self.assertEqual(15, len(fetcher.searches))  # The 90-day stage reuses the task cache.

    def test_explicit_month_does_not_widen_and_keeps_original_five_page_limit(self):
        fetcher=Fetcher(lambda w,p:dated(1001,'历史藏书',700,'1abcdefgh',p+10))
        worker=self.worker(fetcher,keyword=['甲词'],time_range='最近一月',count=1)
        result=worker.run()
        self.assertEqual(0,result['found'])
        self.assertEqual(30,len(fetcher.searches))
        self.assertFalse(any('不限' in text for text in self.status))

    def test_precise_still_accepts_old_result_and_keeps_one_per_word(self):
        fetcher=Fetcher(lambda w,p:dated(1001,'历史藏书',700,'1abcdefgh'))
        result=self.worker(fetcher,keyword=['甲词'],mode='precise').run()
        self.assertEqual(1,result['found'])
        self.assertEqual(1,len(fetcher.searches))

    def test_stop_after_recent_hit_preserves_result_and_does_not_widen(self):
        fetcher=Fetcher(lambda w,p:dated(1001,'近期藏书',5,'1abcdefgh'))
        worker=self.worker(fetcher)
        worker.on_item=lambda row:worker.stop()
        result=worker.run()
        self.assertTrue(result['stopped'])
        self.assertEqual(1,result['found'])
        self.assertEqual(1,len(json.loads(Path(self.path).read_text(encoding='utf-8'))['items']))
        self.assertFalse(any('不限' in text for text in self.status))

    def test_resumed_recent_page_marker_is_not_requested_again(self):
        query=KEYWORD_VARIANTS[0].format(kw='甲词')
        Path(self.path).write_text(json.dumps({'mode':'fuzzy','done_queries':[f'最近一月|{query}|0']}),encoding='utf-8')
        fetcher=Fetcher(lambda w,p:dated(1001,'近期藏书',5,'1abcdefgh'))
        self.worker(fetcher,resume=True,count=1).run()
        self.assertEqual([(KEYWORD_VARIANTS[0].format(kw='乙词'),0)],fetcher.searches)


if __name__=='__main__':
    unittest.main()
