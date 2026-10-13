"""Fixed-response contracts from v1.0.4 collect_worker, not live search tests."""
from pathlib import Path
import json
import sys
import tempfile
import unittest
from unittest.mock import patch
from urllib.parse import parse_qs, urlsplit

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from lxds.collect import CollectWorker
from lxds.pool import PoolError
from lxds.collection_rules import KEYWORD_VARIANTS
import protocol


def question(qid=1001, name='甲书', body='', next_pn=None):
    result = f'<dl><dt><a href="https://zhidao.baidu.com/question/{qid}.html?fr=search">求《{name}》百度网盘链接</a></dt><dd>{body}</dd></dl>'
    if next_pn is not None:
        result += f'<a class="pager-next" href="/search?pn={next_pn}">下一页</a>'
    return result


def share(identity='1abcdefgh', pwd='a123'):
    return f'https://pan.baidu.com/s/{identity}' + (f' 提取码：{pwd}' if pwd else '')


def detail(name='甲书', identity='1abcdefgh', pwd='a123'):
    return f'<title>求《{name}》百度网盘链接_百度知道</title><div class="best-text">{share(identity,pwd)}</div><!--' + 'x' * 2100 + '-->'


class Pool:
    slots = [type('Slot', (), {'exit_kind':'direct'})()]
    def __init__(self, limited=False):
        self.probes = []
        self.limited = limited
    def start(self):
        pass
    def on_silent_risk(self, keywords=None, page=False):
        self.probes.append((keywords, page))
        return self.limited


class Fetcher:
    def __init__(self, respond=None):
        self.respond = respond or (lambda word, pn: '没有找到相关结果')
        self.searches = []
        self.details = []
        self.detail_pages = {}
    def get(self, url, **kwargs):
        if '/question/' in url:
            qid = url.split('/question/',1)[1].split('.',1)[0]
            self.details.append(qid)
            return self.detail_pages.get(qid, detail()), None
        params = parse_qs(urlsplit(url).query)
        word, pn = params['word'][0], int(params['pn'][0])
        self.searches.append((word,pn))
        return self.respond(word,pn), None


class LegacyCollectParityTests(unittest.TestCase):
    def test_precise_shuffles_before_drawing_auxiliary_countdowns(self):
        calls = []
        worker = self.worker()
        worker.pool.start = lambda: calls.append('pool-start')
        worker._shuffle = lambda words: calls.append('shuffle') or words
        worker._pick_every = lambda bounds: calls.append('countdown') or 100000
        worker.run()
        self.assertEqual(['pool-start', 'shuffle', 'countdown', 'countdown'], calls)

    def test_precise_keeps_next_candidate_for_auxiliary_browse_without_fetching(self):
        fetcher = Fetcher(lambda word, pn: question(body=share()) + question(1002, '乙书'))
        worker = self.worker(fetcher, count=1)
        got, recent, *_ = worker._search_one_keyword('taptap')
        self.assertTrue(got)
        self.assertEqual(2, len(recent))
        self.assertEqual([], fetcher.details)

    def test_supplement_failure_preserves_checkpoint_and_accepted_results(self):
        class Supplement:
            def iter_search(self, term, log):
                yield {'title':'《甲书》', 'source_url':'fixture',
                       'links':[{'link':'https://pan.baidu.com/s/1abcdefgh', 'pwd':'a123'}]}
                raise PoolError('offline fixture')
        worker = self.worker(mode='fuzzy', count=2, time_range='全部',
                             supplement_factory=lambda *args: Supplement())
        with self.assertRaises(PoolError):
            worker.run()
        self.assertEqual(1, len(json.loads(Path(self.path).read_text(encoding='utf-8'))['items']))

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = str(Path(self.temp.name) / 'checkpoint.json')
        self.shuffle = patch('lxds.collect.shuffle_blocks', lambda words, *args: list(words), create=True)
        self.shuffle.start()
        self.addCleanup(self.shuffle.stop)
        self.pace = patch('lxds.collect.pick_every', return_value=100000)
        self.pace.start()
        self.addCleanup(self.pace.stop)

    def worker(self, fetcher=None, **kwargs):
        return CollectWorker(keyword=kwargs.pop('keyword', ['taptap']),
            fetcher=fetcher or Fetcher(), pool=kwargs.pop('pool', Pool()),
            checkpoint_path=self.path, **kwargs)

    def test_precise_expands_query_and_uses_keyword_count_target(self):
        fetcher = Fetcher(lambda word,pn: question(body=share()))
        worker = self.worker(fetcher)
        result = worker.run()
        self.assertEqual([('求taptap百度网盘链接',0)], fetcher.searches)
        self.assertEqual(1, worker.count)
        self.assertEqual(1, result['found'])

    def test_precise_has_two_page_and_three_detail_budget(self):
        fetcher = Fetcher(lambda word,pn: ''.join(question(1000+i, next_pn=pn+10 if i==4 and pn<40 else None) for i in range(5)))
        fetcher.detail_pages = {str(1000+i): '' for i in range(5)}
        result = self.worker(fetcher).run()
        self.assertEqual(0,result['found'])
        self.assertEqual([0,10],[pn for _,pn in fetcher.searches])
        self.assertEqual(3,len(fetcher.details))

    def test_precise_continues_past_passwordless_hit(self):
        html = question(1001,body=share(pwd='')) + question(1002,'乙书',share('1ijklmnop','b234'))
        result = self.worker(Fetcher(lambda w,p:html)).run()
        self.assertEqual(['乙书'],[row['资源名称'] for row in result['items']])

    def test_precise_uses_real_detail_title_and_keyword_fallback(self):
        fetcher = Fetcher(lambda w,p:question(name='搜索标题'))
        fetcher.detail_pages['1001'] = detail(name='详情书名')
        self.assertEqual('详情书名',self.worker(fetcher).run()['items'][0]['资源名称'])
        fetcher = Fetcher(lambda w,p:question(name='免费小说',body=share()))
        self.assertEqual('taptap',self.worker(fetcher).run()['items'][0]['资源名称'])

    def test_link_is_consumed_before_case_insensitive_name_dedupe(self):
        worker = self.worker()
        self.assertTrue(worker._add('TapTap','https://pan.baidu.com/s/1abcdefgh','a123',''))
        self.assertFalse(worker._add(' taptap ','https://pan.baidu.com/s/1ijklmnop','a123',''))
        self.assertFalse(worker._add('别的名称','http://pan.baidu.com/s/1ijklmnop','a123',''))
        self.assertFalse(worker._add('无提取码','https://pan.baidu.com/s/1qrstuvwx','',''))

    def test_fuzzy_auto_caps_recent_variants_then_keeps_six_three_month_variants(self):
        fetcher = Fetcher()
        result = self.worker(fetcher, mode='fuzzy', count=2, keyword='taptap').run()
        self.assertEqual(0,result['found'])
        self.assertEqual([(variant.format(kw='taptap'),0)
                          for variant in (*KEYWORD_VARIANTS[:3], *KEYWORD_VARIANTS)], fetcher.searches)

    def test_fuzzy_expands_domain_words_in_order(self):
        fetcher = Fetcher()
        self.worker(fetcher,mode='fuzzy',count=1,keyword='游戏',time_range='最近一月').run()
        self.assertEqual(['求游戏百度网盘链接','求热门游戏百度网盘链接','求最新游戏百度网盘链接',
            '求免费游戏百度网盘链接','求破解游戏百度网盘链接','求单机游戏百度网盘链接'],[w for w,p in fetcher.searches[::6]])

    def test_fuzzy_one_link_per_candidate_even_name_dedupe_disabled(self):
        body = share('1abcdefgh') + ' x'*200 + share('1ijklmnop')
        fetcher = Fetcher(lambda w,p: question(body=body) if w==KEYWORD_VARIANTS[0].format(kw='taptap') else '没有找到相关结果')
        self.assertEqual(1,self.worker(fetcher,mode='fuzzy',count=2,dedupe_names=False,time_range='全部').run()['found'])

    def test_precise_fifteen_normal_misses_probe_once_no_retry_if_healthy(self):
        pool, fetcher = Pool(), Fetcher()
        worker = self.worker(fetcher,keyword=[f'空词{i}' for i in range(15)],pool=pool)
        worker.run()
        self.assertEqual(1,len(pool.probes))
        self.assertEqual(15,len(fetcher.searches))

    def test_precise_two_abnormal_words_retried_once_after_recovery(self):
        pool, fetcher = Pool(limited=True), Fetcher(lambda w,p:'页面结构异常')
        self.worker(fetcher,keyword=['甲词','乙词'],pool=pool).run()
        self.assertEqual(1,len(pool.probes))
        self.assertEqual(['求甲词百度网盘链接','求乙词百度网盘链接']*2,[w for w,p in fetcher.searches])

    def test_fuzzy_passwordless_hits_reset_barren_counter_but_never_complete_target(self):
        fetcher = Fetcher(lambda w,p:question(body=share(pwd=''),next_pn=p+10 if p<60 else None))
        result=self.worker(fetcher,mode='fuzzy',count=1,time_range='全部').run()
        self.assertEqual(0,result['found'])
        self.assertEqual(42,result['stats']['no_pwd'])
        self.assertEqual(42,len(fetcher.searches))

    def test_supplement_uses_keyword_fallback_for_generic_fuzzy_title(self):
        calls=[]
        class Supplement:
            def iter_search(self,term,log):
                calls.append(term)
                yield {'title':'免费小说','source_url':'https://zhidao.baidu.com/question/1001','links':[{'link':'https://pan.baidu.com/s/1abcdefgh','pwd':'a123'}]}
        worker=self.worker(mode='fuzzy',count=1,time_range='全部',supplement_factory=lambda *a:Supplement())
        self.assertEqual('taptap',worker.run()['items'][0]['资源名称'])

    def test_supplement_stops_after_ten_barren_terms(self):
        calls=[]
        class Supplement:
            def search(self,term,log):
                calls.append(term)
                return []
        self.worker(keyword=[f'词{i}' for i in range(12)],supplement_factory=lambda *a:Supplement()).run()
        self.assertEqual(10,len(calls))

    def test_default_supplement_selects_site_collector_precise_one_variant(self):
        with patch('lxds.collect.BaiduSiteSearchCollector',create=True) as collector:
            collector.return_value.iter_search.return_value=iter(())
            worker=self.worker()
            worker._supplement_enabled=True
            worker.run()
            self.assertTrue(collector.called)
            self.assertEqual(1,collector.call_args.kwargs['max_variants'])

    def test_resume_supplements_each_missed_keyword_once_and_completion_clears_checkpoint(self):
        # 原版恢复后不再补量；用户主动暂停再继续不应比一次采完少，故只跳过已补过的词。
        calls=[]
        class Supplement:
            def search(self,term,log):
                calls.append(term)
                return []
        for supplemented,expected in (([],['taptap']),(['taptap'],[])):
            with self.subTest(supplemented=supplemented):
                calls.clear()
                Path(self.path).write_text(json.dumps({'mode':'precise','done_keywords':['taptap'],
                    'supplement_done':supplemented,'items':[]}),encoding='utf-8')
                self.worker(resume=True,supplement_factory=lambda *a:Supplement()).run()
                self.assertEqual(expected,calls)
                self.assertFalse(Path(self.path).exists())

    def test_protocol_boolean_detail_mode_maps_to_original_auto_or_off(self):
        frame={'mode':'precise','keywords':['taptap'],'accounts':[{'cookie':'offline'}]}
        for supplied,expected in ((True,'auto'),(False,'off'),('always','always'),('off','off')):
            self.assertEqual(expected,protocol.parse_start({**frame,'detail_mode':supplied})['detail_mode'])


if __name__=='__main__':
    unittest.main()
