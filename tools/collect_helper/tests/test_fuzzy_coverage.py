"""Cover distinct input keywords before filling the global fuzzy target."""
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from lxds.collect import CollectWorker
from lxds.zhidao_parse import collect_link_key
from test_legacy_collect_parity import Fetcher, Pool
from test_fuzzy_recent_budget import dated

WORDS = ['甲词', '乙词', '丙词', '丁词', '戊词']


def books(word, index, age=5, count=10, next_pn=None):
    return ''.join(dated(10000+index*100+i, f'{word}藏书{i}', age, f'1word{index:02}{i:03}',
                        next_pn if i == count-1 else None) for i in range(count))


class FuzzyCoverageTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.path = str(Path(temporary.name)/'cp.json')
        self.logs = []
        pacing = patch('lxds.collect.pick_every', return_value=100000)
        pacing.start()
        self.addCleanup(pacing.stop)

    def worker(self, fetcher, **options):
        return CollectWorker(keyword=options.pop('keyword', WORDS), count=options.pop('count', 10),
            mode='fuzzy', fetcher=fetcher, pool=Pool(), checkpoint_path=self.path,
            on_log=self.logs.append, **options)

    @staticmethod
    def response(query, pn):
        for index, word in enumerate(WORDS):
            if word in query:
                return books(word,index)
        return '没有找到相关结果'

    def test_five_words_each_get_one_before_global_target_is_filled(self):
        fetcher = Fetcher(self.response)
        worker = self.worker(fetcher)
        rows = worker.run()['items']
        self.assertEqual(10,len(rows))
        self.assertEqual([f'{word}藏书0' for word in WORDS], [row['资源名称'] for row in rows[:5]])
        self.assertEqual([f'甲词藏书{i}' for i in range(1,6)], [row['资源名称'] for row in rows[5:]])
        self.assertEqual(5,len(fetcher.searches))  # Fill reuses the first word's fetched page.

    def test_missing_word_is_tried_before_others_supply_remaining_results(self):
        visited = []
        def response(query,pn):
            visited.append(query)
            return '没有找到相关结果' if '乙词' in query else books('甲词',0)
        worker = self.worker(Fetcher(response), keyword=WORDS[:2], count=3)
        accepted_after_searches = []
        worker.on_item = lambda row:accepted_after_searches.append(list(visited))
        result = worker.run()
        self.assertEqual(3,result['found'])
        self.assertTrue(any('乙词' in query for query in accepted_after_searches[1]))
        self.assertTrue(any('乙词' in line and '未找到' in line for line in self.logs))

    def test_older_but_within_ninety_days_covers_missing_word_before_extras(self):
        def response(query,pn):
            return books('乙词',1,age=60) if '乙词' in query else books('甲词',0)
        rows = self.worker(Fetcher(response),keyword=WORDS[:2],count=3).run()['items']
        self.assertEqual(['甲词藏书0','乙词藏书0','甲词藏书1'],[row['资源名称'] for row in rows])

    def test_duplicates_do_not_count_as_coverage_for_a_second_word(self):
        common = books('甲词',0,count=1)
        def response(query,pn):
            return common + books('乙词',1,count=1) if '乙词' in query else books('甲词',0)
        worker = self.worker(Fetcher(response),keyword=WORDS[:2],count=3)
        rows = worker.run()['items']
        self.assertEqual(['甲词藏书0','乙词藏书0'],[row['资源名称'] for row in rows[:2]])

    def test_lower_target_still_prefers_distinct_words_without_exceeding_count(self):
        rows = self.worker(Fetcher(self.response),count=3).run()['items']
        self.assertEqual([f'{word}藏书0' for word in WORDS[:3]],[row['资源名称'] for row in rows])

    def test_expansion_belongs_to_input_word_and_does_not_take_another_quota(self):
        def response(query,pn):
            if '热门游戏' in query:
                return books('游戏',0)
            if '乙词' in query:
                return books('乙词',1)
            return '没有找到相关结果'
        worker = self.worker(Fetcher(response),keyword=['游戏','乙词'],count=3)
        rows = worker.run()['items']
        self.assertEqual({'乙词藏书0','游戏藏书0'},{row['资源名称'] for row in rows[:2]})
        self.assertEqual({'游戏','乙词'},set(worker._item_keywords.values()))

    def test_stop_and_resume_keeps_keyword_ownership_before_item_callback(self):
        first = self.worker(Fetcher(self.response))
        first.on_item = lambda row:first.stop() if len(first.items)==2 else None
        stopped = first.run()
        self.assertTrue(stopped['stopped'])
        payload = json.loads(Path(self.path).read_text(encoding='utf-8'))
        owners = payload.get('item_keywords',{})
        self.assertEqual(WORDS[:2],[owners.get(collect_link_key(row['资源链接'])) for row in stopped['items']])
        second = self.worker(Fetcher(self.response),resume=True)
        result = second.run()
        self.assertEqual(10,result['found'])
        self.assertEqual([f'{word}藏书0' for word in WORDS],[row['资源名称'] for row in result['items'][:5]])

    def test_older_than_ninety_days_never_satisfies_a_keyword(self):
        def response(query,pn):
            return books('乙词',1,age=91) if '乙词' in query else books('甲词',0)
        rows = self.worker(Fetcher(response),keyword=WORDS[:2],count=3).run()['items']
        self.assertEqual(3,len(rows))
        self.assertTrue(all(row['资源名称'].startswith('甲词') for row in rows))
        self.assertTrue(any('乙词' in line and '未找到' in line for line in self.logs))

    def test_fill_can_continue_past_the_page_that_satisfied_coverage(self):
        def response(query,pn):
            if '乙词' in query:
                return books('乙词' if pn == 0 else '乙词续卷', 1 if pn == 0 else 2, age=60, count=1,
                             next_pn=10 if pn == 0 else None)
            return books('甲词',0,count=1)
        rows = self.worker(Fetcher(response),keyword=WORDS[:2],count=3).run()['items']
        self.assertEqual(3,len(rows))
        self.assertEqual('甲词藏书0',rows[0]['资源名称'])
        self.assertTrue(all(row['资源名称'].startswith('乙词') for row in rows[1:]))

    def test_missing_word_has_a_finite_three_month_page_budget(self):
        def response(query,pn):
            if '乙词' in query:
                return books('过期书',1,age=91,count=1,next_pn=pn+10)
            return books('甲词',0)
        worker = self.worker(Fetcher(response),keyword=WORDS[:2],count=3)
        visits = []
        search = worker._search
        def recorded(query,pn):
            visits.append((worker._current_step,query,pn))
            return search(query,pn)
        worker._search = recorded
        self.assertEqual(3,worker.run()['found'])
        capped = [row for row in visits if '关键词覆盖' in row[0] and '最近三个月' in row[0]]
        self.assertEqual(30,len(capped))

    def test_legacy_rows_without_keyword_ownership_do_not_block_new_coverage(self):
        first = self.worker(Fetcher(self.response))
        first.on_item = lambda row:first.stop()
        first.run()
        payload = json.loads(Path(self.path).read_text(encoding='utf-8'))
        payload.pop('item_keywords',None)
        Path(self.path).write_text(json.dumps(payload),encoding='utf-8')
        second = self.worker(Fetcher(self.response),resume=True,count=5)
        rows = second.run()['items']
        self.assertEqual([f'{word}藏书0' for word in WORDS],[row['资源名称'] for row in rows])
        self.assertTrue(any('归属' in line for line in self.logs))

    def test_fifth_page_coverage_hit_does_not_hide_sixth_page_during_fill_or_resume(self):
        def response(query,pn):
            if '乙词' not in query:
                return books('甲词',0,count=1)
            if query != '求乙词百度网盘链接':
                return '没有找到相关结果'
            if pn < 40:
                return books('过期书',3,age=91,count=1,next_pn=pn+10)
            return books('乙词首卷' if pn == 40 else '乙词续卷',4 if pn == 40 else 5,
                         age=60,count=1,next_pn=50 if pn == 40 else None)
        for resume in (False, True):
            with self.subTest(resume=resume):
                worker = self.worker(Fetcher(response),keyword=WORDS[:2],count=3)
                if resume:
                    worker.on_item = lambda row:worker.stop() if len(worker.items)==2 else None
                    self.assertTrue(worker.run()['stopped'])
                    worker = self.worker(Fetcher(response),keyword=WORDS[:2],count=3,resume=True)
                result = worker.run()
                self.assertEqual(['甲词藏书0','乙词首卷藏书0','乙词续卷藏书0'],
                                 [row['资源名称'] for row in result['items']])


if __name__ == '__main__':
    unittest.main()
