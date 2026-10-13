"""用户主动暂停后续采：任意请求处暂停再继续，结果应与一口气采完相同。"""
from pathlib import Path
import json
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import main
import protocol
from lxds.checkpoint import checkpoint_path
from lxds.collect import CollectWorker
from lxds.collection_rules import KEYWORD_VARIANTS
from test_fuzzy_recent_budget import dated
from test_legacy_collect_parity import Fetcher, Pool, question, share
import test_collect_helper as helper_tests


class Tripwire:
    """数到第 at 次外部请求时请求停止，模拟用户恰好在此刻点了暂停。"""

    def __init__(self, at=None):
        self.at, self.calls, self.worker = at, 0, None

    def hit(self):
        self.calls += 1
        if self.calls == self.at:
            self.worker.stop()


class CountingFetcher(Fetcher):
    def __init__(self, respond, tripwire):
        super().__init__(respond)
        self.tripwire = tripwire

    def get(self, url, **kwargs):
        self.tripwire.hit()
        return super().get(url, **kwargs)


class PauseResumeEngineTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        for target, value in (('lxds.collect.pick_every', lambda *a: 100000),
                              ('lxds.collect.shuffle_blocks', lambda words, *a: list(words))):
            patcher = patch(target, value)
            patcher.start()
            self.addCleanup(patcher.stop)

    def _run(self, name, build, at=None, resume=False):
        tripwire = Tripwire(at)
        emitted, restored = [], []
        worker = build(tripwire, checkpoint_path=str(Path(self.temp.name) / f'{name}.json'), resume=resume,
                       on_item=lambda row: emitted.append(row['资源链接']),
                       on_restored=lambda rows: restored.extend(row['资源链接'] for row in rows))
        tripwire.worker = worker
        result = worker.run()
        return result, tripwire.calls, emitted, restored

    def _assert_pause_anywhere_matches_uninterrupted(self, build):
        full, total_calls, _, _ = self._run('full', build)
        self.assertFalse(full['stopped'])
        expected = [row['资源链接'] for row in full['items']]
        self.assertTrue(expected)
        for at in range(1, total_calls + 1):
            with self.subTest(pause_at_request=at):
                first, _, first_emitted, _ = self._run(f'paused-{at}', build, at=at)
                self.assertTrue(first['stopped'])
                second, _, second_emitted, restored = self._run(f'paused-{at}', build, resume=True)
                self.assertFalse(second['stopped'])
                links = [row['资源链接'] for row in second['items']]
                self.assertEqual(sorted(expected), sorted(links))
                self.assertEqual(len(links), len(set(links)))
                self.assertEqual(first_emitted, restored, '续采应把暂停前已采的结果原样交还')
                self.assertEqual(links, restored + second_emitted, '续采只新报暂停后采到的结果')
                self.assertFalse((Path(self.temp.name) / f'paused-{at}.json').exists(), '采完后断点应清除')

    def test_fuzzy_single_keyword_resumes_inside_a_multi_page_query(self):
        query = KEYWORD_VARIANTS[0].format(kw='甲词')

        def respond(word, pn):
            page = pn // 10
            if word != query or page > 3:
                return '没有找到相关结果'
            return (dated(1000 + page * 2, f'近期藏书{page}', 5, f'1recentaa{page}')
                    + dated(1001 + page * 2, f'历史藏书{page}', 60, f'1olderbbb{page}',
                            next_pn=pn + 10 if page < 3 else None))

        def build(tripwire, **kwargs):
            return CollectWorker(keyword=['甲词'], mode='fuzzy', count=8, pool=Pool(),
                                 fetcher=CountingFetcher(respond, tripwire), **kwargs)

        self._assert_pause_anywhere_matches_uninterrupted(build)

    def test_fuzzy_multiple_keywords_keep_coverage_and_total(self):
        def respond(word, pn):
            for index, owner in enumerate(('甲词', '乙词', '丙词')):
                if word == KEYWORD_VARIANTS[0].format(kw=owner) and pn <= 10:
                    base = 2000 + index * 100 + pn
                    return (dated(base, f'{owner}藏书{pn}a', 5 if owner != '丙词' else 60, f'1cover{index}a{pn:02d}')
                            + dated(base + 1, f'{owner}藏书{pn}b', 60, f'1cover{index}b{pn:02d}',
                                    next_pn=pn + 10 if pn == 0 else None))
            return '没有找到相关结果'

        def build(tripwire, **kwargs):
            return CollectWorker(keyword=['甲词', '乙词', '丙词'], mode='fuzzy', count=9, pool=Pool(),
                                 fetcher=CountingFetcher(respond, tripwire), **kwargs)

        self._assert_pause_anywhere_matches_uninterrupted(build)

    def test_precise_resumes_pending_keywords_and_unfinished_supplement(self):
        words = [f'词{index}' for index in range(5)]

        def respond(word, pn):
            for index in (0, 2, 4):
                if word == KEYWORD_VARIANTS[0].format(kw=words[index]):
                    return question(3000 + index, f'精准书{index}', share(f'1precise{index}'))
            return '没有找到相关结果'

        def build(tripwire, **kwargs):
            class Supplement:
                def search(self, term, log):
                    tripwire.hit()
                    if term != '词1':
                        return []
                    return [{'title': '求《补量书》百度网盘链接', 'source_url': 'https://zhidao.baidu.com/question/9.html',
                             'links': [{'link': 'https://pan.baidu.com/s/1supplemt', 'pwd': 'a123'}]}]

            return CollectWorker(keyword=words, mode='precise', pool=Pool(),
                                 fetcher=CountingFetcher(respond, tripwire),
                                 supplement_factory=lambda *a: Supplement(), **kwargs)

        full, calls, _, _ = self._run('probe', build)
        self.assertEqual(4, full['found'], '三个词直接命中，一个词靠补量')
        self.assertEqual(7, calls, '五次搜索加两次补量，暂停点需覆盖补量阶段')
        self._assert_pause_anywhere_matches_uninterrupted(build)

    def test_changed_keywords_do_not_reuse_the_old_progress(self):
        path = Path(self.temp.name) / 'changed.json'
        path.write_text(json.dumps({'mode': 'precise', 'keywords': ['旧词'], 'done_keywords': ['新词'],
                                    'items': [{'资源名称': '旧书', '资源链接': 'https://pan.baidu.com/s/1oldoldold',
                                               '提取码': 'a123', '来源链接': ''}]}), encoding='utf-8')
        logs = []
        fetcher = Fetcher(lambda word, pn: question(4000, '新书', share('1newnewnew')))
        result = CollectWorker(keyword=['新词'], mode='precise', pool=Pool(), fetcher=fetcher,
                               checkpoint_path=str(path), resume=True, on_log=logs.append).run()
        self.assertEqual(['https://pan.baidu.com/s/1newnewnew'], [row['资源链接'] for row in result['items']])
        self.assertTrue(any('不一致' in text for text in logs))

    def test_default_checkpoint_path_accepts_a_full_keyword_batch(self):
        words = [f'云边有个小卖部{index}' for index in range(protocol.MAX_KEYWORDS)]
        first = checkpoint_path('precise:' + '|'.join(words), 'collect')
        self.assertEqual(first, checkpoint_path('precise:' + '|'.join(words), 'collect'))
        self.assertNotEqual(first, checkpoint_path('precise:' + '|'.join(words[:-1]), 'collect'))
        CollectWorker(keyword=words, mode='precise', pool=Pool(), fetcher=Fetcher())


class PauseSessionTests(unittest.TestCase):
    def _session(self, configure=None):
        channel = helper_tests.RecordingChannel()
        created = {}

        def factory(**kwargs):
            created['worker'] = worker = helper_tests.ScriptedWorker(**kwargs)
            if configure:
                configure(worker)
            return worker

        session = main.Session(channel, worker_factory=factory)
        return channel, session, created

    @staticmethod
    def _options(**extra):
        return protocol.parse_start({'cmd': 'start', 'mode': 'precise', 'keywords': ['甲', '乙'],
                                     'accounts': [{'cookie': 'BDUSS=x'}], **extra})

    def test_pause_is_a_known_command(self):
        self.assertEqual('pause', protocol.decode(json.dumps({'cmd': 'pause'}))['cmd'])

    def _start_blocked(self):
        gate = threading.Event()

        def configure(worker):
            worker.ask_answer = False
            worker.wait_before_finish = gate

        channel, session, created = self._session(configure)
        session.start(self._options())
        return channel, session, created, gate

    def test_pause_stops_the_worker_and_marks_done_as_paused(self):
        channel, session, created, gate = self._start_blocked()
        session.pause()
        gate.set()
        session.join(timeout=10)
        self.assertTrue(created['worker'].stopped)
        done = channel.first('done')
        self.assertTrue(done['stopped'])
        self.assertTrue(done['paused'])

    def test_stop_after_pause_counts_as_stop(self):
        channel, session, _, gate = self._start_blocked()
        session.pause()
        session.stop()
        gate.set()
        session.join(timeout=10)
        self.assertTrue(channel.first('done')['stopped'])
        self.assertFalse(channel.first('done')['paused'])

    def test_plain_stop_and_natural_completion_are_not_paused(self):
        channel, session, _, gate = self._start_blocked()
        session.stop()
        gate.set()
        session.join(timeout=10)
        self.assertFalse(channel.first('done')['paused'])

        channel, session, _ = self._session(lambda worker: setattr(worker, 'ask_answer', False))
        session.start(self._options())
        session.join(timeout=10)
        self.assertFalse(channel.first('done')['stopped'])
        self.assertFalse(channel.first('done')['paused'])
        self.assertFalse(channel.first('done')['failed'])

    def test_worker_failure_is_flagged_so_the_host_can_keep_a_resumable_task(self):
        def configure(worker):
            worker.raise_error = RuntimeError('账号全部失效')

        channel, session, _ = self._session(configure)
        with patch('main.sys.stderr'):
            session.start(self._options(resume=True))
            session.join(timeout=10)
        done = channel.first('done')
        self.assertTrue(done['stopped'])
        self.assertTrue(done['failed'])
        self.assertFalse(done['paused'])

    def test_pause_releases_a_risk_prompt_without_answering_continue(self):
        channel, session, created = self._session()
        session.start(self._options())
        for _ in range(200):
            if channel.first('ask') is not None:
                break
            threading.Event().wait(0.01)
        self.assertIsNotNone(channel.first('ask'))
        session.pause()
        session.join(timeout=10)
        self.assertFalse(created['worker'].ask_answer)
        self.assertTrue(channel.first('done')['paused'])

    def test_pause_without_a_running_task_is_ignored_and_does_not_leak_into_the_next_run(self):
        channel, session, _ = self._session(lambda worker: setattr(worker, 'ask_answer', False))
        session.pause()
        self.assertEqual([], channel.names())
        session.start(self._options())
        session.join(timeout=10)
        self.assertFalse(channel.first('done')['paused'])

    def test_real_worker_paused_then_continued_by_a_new_helper_session(self):
        words = [f'测试词{index}' for index in range(6)]
        pages = {KEYWORD_VARIANTS[0].format(kw=word): question(5000 + index, f'续采书{index}', share(f'1resume0{index}'))
                 for index, word in enumerate(words)}
        frame = {'cmd': 'start', 'mode': 'precise', 'keywords': words,
                 'accounts': [{'name': '知道甲', 'cookie': 'BDUSS=x'}]}
        with tempfile.TemporaryDirectory(prefix='collect-pause-') as temporary, \
                patch('lxds.collect.pick_every', lambda *a: 100000):
            def launch(resume, pause_after=None):
                channel = helper_tests.RecordingChannel()
                holder = {}

                class PausingFetcher(Fetcher):
                    def get(self, url, **kwargs):
                        if pause_after is not None and len(channel.all('item')) >= pause_after:
                            holder['session'].pause()
                        return super().get(url, **kwargs)

                def factory(**kwargs):
                    kwargs['checkpoint_path'] = str(Path(temporary) / 'checkpoint.json')
                    return CollectWorker(fetcher=PausingFetcher(lambda word, pn: pages.get(word, '没有找到相关结果')),
                                         pool=Pool(), **kwargs)

                holder['session'] = session = main.Session(channel, worker_factory=factory)
                session.start(protocol.parse_start({**frame, 'resume': resume}))
                session.join(timeout=30)
                return channel

            first = launch(resume=False, pause_after=2)
            self.assertTrue(first.first('done')['paused'])
            paused_links = [item['link'] for item in first.all('item')]
            self.assertEqual(2, len(paused_links))
            self.assertFalse(any(item.get('restored') for item in first.all('item')))

            second = launch(resume=True)
            done = second.first('done')
            self.assertFalse(done['stopped'])
            self.assertFalse(done['paused'])
            self.assertEqual(len(words), done['found'])
            items = second.all('item')
            self.assertEqual(paused_links, [item['link'] for item in items if item.get('restored')])
            self.assertEqual(paused_links, [item['link'] for item in items[:2]], '恢复的结果先于新结果到达')
            self.assertEqual(len(words), len({item['link'] for item in items}))
            self.assertTrue(any('从断点继续' in log['text'] for log in second.all('log')))


if __name__ == '__main__':
    unittest.main()
