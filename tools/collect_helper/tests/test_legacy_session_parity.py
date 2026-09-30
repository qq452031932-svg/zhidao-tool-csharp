"""Original risk prompt semantics through the unchanged host protocol."""
from pathlib import Path
import sys
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import main


class LegacySessionParityTests(unittest.TestCase):
    def test_failure_preserves_count_and_stats_of_already_delivered_items(self):
        channel = Mock()
        worker = Mock(items=[{'资源名称':'甲书'}], stats={'ok':1})
        worker.run.side_effect = RuntimeError('offline fixture')
        with patch('main.sys.stderr'):
            main.Session(channel)._run(worker)
        self.assertEqual('done', channel.send.call_args.args[0])
        self.assertEqual({'found':1, 'stopped':True, 'stats':{'ok':1}}, channel.send.call_args.kwargs)

    def test_direct_worker_risk_wait_is_bounded_and_stale_choice_cannot_continue(self):
        from lxds.collect import CollectWorker
        worker = CollectWorker(keyword=['fixture'], fetcher=Mock(), pool=Mock())
        worker._resume_event = Mock()
        worker._resume_event.wait.side_effect = [False, AssertionError('wait must not loop forever')]
        worker._resume_choice = True
        self.assertFalse(worker._pause_for_risk({'message':'fixture'}))
        worker._resume_event.wait.assert_called_once_with(1800)

    def test_original_message_reaches_existing_reason_field_and_wait_is_bounded(self):
        channel = Mock()
        session = main.Session(channel)
        session._answer = Mock()
        session._answer.wait.return_value = False
        session._answer_value = True  # A stale previous answer must not beat timeout.
        self.assertFalse(session._on_ask({'kind':'enable_proxy','message':'请开启 Clash 后继续'}))
        self.assertEqual('请开启 Clash 后继续', channel.send.call_args.kwargs['reason'])
        session._answer.wait.assert_called_once_with(1800)


if __name__=='__main__':
    unittest.main()
