"""Offline request and risk decisions from v1.0.4 collect_pool bytecode.

All sessions/proxy probes are replaced locally; no live accounts or network.
"""
from pathlib import Path
import sys
import threading
import unittest
from unittest.mock import Mock, call, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import requests
from lxds import pool as module
from lxds.pool import AccountSlot, CollectCancelled, PinnedExitFetcher, PoolError, PooledFetcher, SlotPool


def response(text="正文", status=200):
    result = requests.Response()
    result.status_code = status
    result._content = text.encode("utf-8")
    result.headers["Content-Type"] = "text/html"
    return result


class LegacyPoolParityTests(unittest.TestCase):
    def setUp(self):
        self.proxy = patch.object(module, "probe_clash_detail", create=True, return_value=(7890, True, ""))
        self.probe = self.proxy.start()
        self.addCleanup(self.proxy.stop)
        self.env = patch.dict(module.os.environ, {}, clear=True)
        self.env.start()
        self.addCleanup(self.env.stop)

    def make_pool(self, count=1):
        pool = SlotPool([{"name": str(i), "cookie": ""} for i in range(count)],
                        Mock(return_value=True), Mock(), threading.Event(), Mock())
        pool.start()
        for slot in pool.slots:
            self.addCleanup(slot.session.close)
        return pool

    def fetch_without_waiting(self, pool, results):
        slot = pool.slots[0]
        pool.acquire = Mock(return_value=slot)
        slot.session.get = Mock(side_effect=results)
        slot.pacer.cool_down = Mock()
        pool.note_request = Mock()
        return PooledFetcher(pool), slot

    def test_transport_non_200_and_risk_retry_the_same_url_until_success(self):
        pool = self.make_pool()
        fetcher, slot = self.fetch_without_waiting(pool, [requests.ConnectionError("offline"),
                                                        response(status=503), response("安全验证"), response("完成")])
        pool.on_risk = Mock(return_value=True)
        with patch.object(module.random, "uniform", side_effect=lambda low, high: low):
            self.assertEqual(("完成", slot), fetcher.get("https://example.test/search"))
        self.assertEqual(["https://example.test/search"] * 4, [c.args[0] for c in slot.session.get.call_args_list])
        self.assertEqual([12] * 4, [c.kwargs["timeout"] for c in slot.session.get.call_args_list])
        self.assertEqual([call(2.0), call(2.0)], slot.pacer.cool_down.call_args_list)
        pool.on_risk.assert_called_once_with(slot, "安全验证页")
        pool.note_request.assert_called_once_with(slot, 3.0)

    def test_retry_cap_is_thirty_and_never_marks_failed_requests_successful(self):
        pool = self.make_pool()
        fetcher, slot = self.fetch_without_waiting(pool, [response(status=503)] * 31)
        with self.assertRaises(PoolError):
            fetcher.get("https://example.test/search")
        self.assertEqual(30, slot.session.get.call_count)
        pool.note_request.assert_not_called()

    def test_detail_uses_original_read_range_and_explicit_timeout(self):
        pool = self.make_pool()
        fetcher, slot = self.fetch_without_waiting(pool, [response()])
        with patch.object(module.random, "uniform", side_effect=lambda low, high: low):
            fetcher.get("https://example.test/detail", timeout=4, purpose="detail")
        pool.note_request.assert_called_once_with(slot, 2.5)
        self.assertEqual(4, slot.session.get.call_args.kwargs["timeout"])

    def test_fetch_boundary_remains_injectable_and_cancelled_is_not_retried(self):
        pool = self.make_pool()
        pool.acquire = Mock(return_value=pool.slots[0])
        pool.fetch = Mock(side_effect=CollectCancelled("stop"))
        with self.assertRaises(CollectCancelled):
            PooledFetcher(pool).get("https://example.test")
        pool.fetch.assert_called_once()

    def test_start_caps_accounts_randomizes_two_exits_and_staggers_only_proxy(self):
        with patch.object(module.random, "shuffle", side_effect=lambda slots: slots.reverse()), \
                patch.object(module.random, "uniform", side_effect=lambda low, high: low), \
                patch.object(module.time, "time", return_value=100):
            pool = self.make_pool(3)
        self.assertEqual(2, len(pool.slots))
        self.assertEqual(["proxy", "direct"], [slot.exit_kind for slot in pool.slots])
        self.assertEqual([3, 0], [slot.pacer.wait_time(100) for slot in pool.slots])
        self.probe.assert_called_once()
        pool.start()
        self.probe.assert_called_once()

    def test_two_accounts_require_available_proxy_while_single_starts_direct(self):
        self.probe.return_value = (0, False, "offline")
        single = self.make_pool()
        self.assertEqual("direct", single.slots[0].exit_kind)
        with self.assertRaises(PoolError):
            self.make_pool(2)

    def test_acquire_chooses_earliest_ready_not_round_robin_and_resumes_flags(self):
        pool = self.make_pool(2)
        for slot, next_at in zip(pool.slots, [10, 20]):
            slot.pacer.next_at = next_at
            slot.flagged = True
        with patch.object(module.time, "time", return_value=100):
            self.assertIs(pool.slots[0], pool.acquire())
            self.assertIs(pool.slots[0], pool.acquire())
        self.assertEqual([False, False], [s.flagged for s in pool.slots])

    def test_pinned_exit_ignores_pacing_and_missing_exit_falls_back(self):
        pool = self.make_pool()
        slot = pool.slots[0]
        slot.pacer.cool_down(90)
        pool.acquire = Mock(return_value=object())
        self.assertIs(slot, PinnedExitFetcher(pool, "direct")._select_slot())
        pool.acquire.assert_not_called()
        self.assertIs(pool.acquire.return_value, PinnedExitFetcher(pool, "proxy")._select_slot())

    def test_pinned_fetch_still_observes_stop_before_network(self):
        pool = self.make_pool()
        pool.stop_event.set()
        pool.fetch = Mock()
        with self.assertRaises(CollectCancelled):
            PinnedExitFetcher(pool, "direct").get("https://example.test")
        pool.fetch.assert_not_called()

    def test_silent_all_healthy_returns_false_and_probes_every_slot(self):
        pool = self.make_pool(2)
        pool.probe_slot = Mock(return_value=True)
        self.assertFalse(pool.on_silent_risk(["term"], False))
        self.assertEqual([call(slot) for slot in pool.slots], pool.probe_slot.call_args_list)
        pool.pause_ask.assert_not_called()

    def test_silent_some_healthy_retries_and_only_cools_unhealthy(self):
        pool = self.make_pool(2)
        pool.probe_slot = Mock(side_effect=[True, False])
        for slot in pool.slots:
            slot.pacer.cool_down = Mock()
        with patch.object(module.random, "uniform", return_value=75):
            self.assertTrue(pool.on_silent_risk(["term"], True))
        pool.slots[0].pacer.cool_down.assert_not_called()
        pool.slots[1].pacer.cool_down.assert_called_once_with(75)
        self.assertTrue(pool.slots[1].flagged)
        pool.pause_ask.assert_not_called()

    def test_silent_single_schedules_one_round_per_call_then_asks(self):
        pool = self.make_pool()
        pool.probe_slot = Mock(return_value=False)
        pool._interruptible_wait = Mock()
        slot = pool.slots[0]
        slot.pacer.cool_down = Mock()
        with patch.object(module.random, "uniform", side_effect=lambda low, high: low):
            for number in range(1, 4):
                self.assertTrue(pool.on_silent_risk(["term"], True))
                self.assertEqual(number, pool.silent_rounds)
                pool.pause_ask.assert_not_called()
            self.assertEqual([call(60.0), call(90.0), call(120.0)], slot.pacer.cool_down.call_args_list)
            self.assertTrue(pool.on_silent_risk(["term"], True))
        pool._interruptible_wait.assert_not_called()
        self.assertEqual("proxy", slot.exit_kind)
        self.assertEqual("enable_proxy", pool.pause_ask.call_args.args[0]["kind"])
        self.assertIn("reason", pool.pause_ask.call_args.args[0])

    def test_success_resets_silent_rounds_and_streak_but_not_flag_history(self):
        pool = self.make_pool()
        slot = pool.slots[0]
        pool.silent_rounds = 2
        slot.risk_streak = 2
        slot.flagged = True
        slot.flag_count = 5
        pool.note_request(slot, 3)
        self.assertEqual((0, 0, True, 5), (pool.silent_rounds, slot.risk_streak, slot.flagged, slot.flag_count))

    def test_third_explicit_risk_uses_first_ready_health_probe_not_hit_slot(self):
        pool = self.make_pool(2)
        for slot in pool.slots:
            slot.pacer.next_at = 0
        hit = pool.slots[1]
        hit.risk_streak = 2
        pool.probe_slot = Mock(return_value=True)
        self.assertTrue(pool.on_risk(hit, "安全验证页"))
        pool.probe_slot.assert_called_once_with(pool.slots[0])
        self.assertEqual(0, hit.risk_streak)
        self.assertTrue(hit.flagged)

    def test_explicit_risk_multi_all_unhealthy_uses_single_risk_path(self):
        pool = self.make_pool(2)
        hit = pool.slots[0]
        hit.risk_streak = 2
        for slot in pool.slots:
            slot.flagged = True
            slot.pacer.cool_down(90)
        pool.probe_slot = Mock(return_value=False)
        pool._single_risk = Mock(return_value=True)
        self.assertTrue(pool.on_risk(hit, "安全验证页"))
        pool._single_risk.assert_called_once_with(hit)
        self.assertEqual(3, pool.probe_slot.call_count)

    def test_both_blocked_resets_states_pacers_and_preserves_exits(self):
        pool = self.make_pool(2)
        before = [slot.exit_kind for slot in pool.slots]
        pool.probe_slot = Mock(return_value=False)
        pool.silent_rounds = 2
        for slot in pool.slots:
            slot.flag_count = 4
            slot.risk_streak = 2
            slot.pacer.note_request(3)
        with patch.object(module.random, "uniform", side_effect=lambda low, high: low):
            self.assertTrue(pool.on_silent_risk(["term"], True))
        self.assertEqual(before, [slot.exit_kind for slot in pool.slots])
        self.assertEqual(0, pool.silent_rounds)
        self.assertEqual([(False, 0, 0, 0)] * 2,
                         [(s.flagged, s.flag_count, s.risk_streak, s.pacer.total) for s in pool.slots])

    def test_healthy_probe_records_success_even_for_legitimate_empty_results(self):
        pool = self.make_pool()
        pool.fetch = Mock(return_value="没有找到相关结果")
        pool.note_request = Mock()
        self.assertTrue(pool.probe_slot(pool.slots[0]))
        pool.note_request.assert_called_once_with(pool.slots[0])
        self.assertEqual(1, len(pool._used_common_questions))

    def test_account_headers_omit_chromium_hints_for_firefox(self):
        profile = {"ua": "Firefox", "sec_ch_ua": "", "mobile": "", "platform": ""}
        with patch.object(module, "pick_profile", return_value=profile):
            slot = AccountSlot("", "  cookie=value  ", "direct")
        self.addCleanup(slot.session.close)
        self.assertEqual("百度知道账号", slot.name)
        self.assertEqual("cookie=value", slot.cookie)
        headers = self.make_pool()._headers(slot)
        self.assertNotIn("sec-ch-ua-platform", headers)
        self.assertNotIn("sec-ch-ua", headers)
        self.assertEqual("?1", headers["Sec-Fetch-User"])

    def test_proxy_candidates_append_system_port_once(self):
        self.assertTrue(callable(getattr(module, "candidate_ports", None)))
        with patch.object(module, "system_proxy_port", return_value=8888):
            self.assertEqual([7890, 7891, 7897, 8888], module.candidate_ports())
        with patch.object(module, "system_proxy_port", return_value=7897):
            self.assertEqual([7890, 7891, 7897], module.candidate_ports())

    def test_proxy_http_uses_original_probe_order_and_200_or_204(self):
        self.assertTrue(callable(getattr(module, "probe_clash_http", None)))
        with patch.object(module.requests, "get", side_effect=[response(status=302), response(status=204)]) as get:
            self.assertTrue(module.probe_clash_http(7897))
        self.assertEqual(["https://zhidao.baidu.com/robots.txt", "http://www.gstatic.com/generate_204"],
                         [c.args[0] for c in get.call_args_list])
        self.assertEqual({"http": "http://127.0.0.1:7897", "https": "http://127.0.0.1:7897"},
                         get.call_args.kwargs["proxies"])
        self.assertFalse(get.call_args.kwargs["allow_redirects"])

    def test_start_stopped_during_http_does_not_probe_more_or_mark_started(self):
        self.proxy.stop()
        for status in (200, 503):
            with self.subTest(status=status):
                stop = threading.Event()
                pool = SlotPool([{"name": "offline", "cookie": ""}], stop_event=stop)
                self.addCleanup(pool.slots[0].session.close)

                def stop_on_http(*args, **kwargs):
                    stop.set()
                    return response(status=status)

                with patch.object(module, "candidate_ports", return_value=[7890]), \
                        patch.object(module, "_tcp_alive", return_value=True), \
                        patch.object(module.requests, "get", side_effect=stop_on_http) as get, \
                        patch.object(module.time, "sleep"):
                    with self.assertRaises(CollectCancelled):
                        pool.start()
                self.assertEqual(1, get.call_count)
                self.assertFalse(pool._started)
                self.assertEqual(0, pool.proxy_port)

    def test_start_stopped_during_tcp_never_attempts_http_or_next_port(self):
        self.proxy.stop()
        stop = threading.Event()
        pool = SlotPool([{"name": "offline", "cookie": ""}], stop_event=stop)
        self.addCleanup(pool.slots[0].session.close)

        def stop_on_tcp(*args):
            stop.set()
            return True

        with patch.object(module, "candidate_ports", return_value=[7890, 7897]), \
                patch.object(module, "_tcp_alive", side_effect=stop_on_tcp) as tcp, \
                patch.object(module.requests, "get", return_value=response()) as get:
            with self.assertRaises(CollectCancelled):
                pool.start()
        tcp.assert_called_once()
        get.assert_not_called()
        self.assertFalse(pool._started)

    def test_stop_between_probe_rounds_interrupts_event_wait(self):
        self.proxy.stop()
        stop = threading.Event()
        pool = SlotPool([{"name": "offline", "cookie": ""}], stop_event=stop)
        self.addCleanup(pool.slots[0].session.close)

        def stop_on_wait(seconds):
            stop.set()
            return True

        with patch.object(module, "candidate_ports", return_value=[7890]), \
                patch.object(module, "_tcp_alive", return_value=False) as tcp, \
                patch.object(stop, "wait", side_effect=stop_on_wait) as wait, \
                patch.object(module.time, "sleep") as sleep:
            with self.assertRaises(CollectCancelled):
                pool.start()
        tcp.assert_called_once()
        wait.assert_called_once_with(0.5)
        sleep.assert_not_called()

    def test_refresh_stopped_during_http_preserves_slot_exit_and_risk_state(self):
        pool = self.make_pool()
        self.proxy.stop()
        slot = pool.slots[0]
        slot.flagged = True
        slot.risk_streak = 2
        slot.pacer.cool_down(90)
        before = (pool.proxy_port, slot.exit_kind, slot.flagged, slot.risk_streak, slot.pacer.next_at)

        def stop_on_http(*args, **kwargs):
            pool.stop_event.set()
            return response()

        with patch.object(module, "candidate_ports", return_value=[7897]), \
                patch.object(module, "_tcp_alive", return_value=True), \
                patch.object(module.requests, "get", side_effect=stop_on_http) as get:
            with self.assertRaises(CollectCancelled):
                pool._single_risk(slot)
        get.assert_called_once()
        self.assertEqual(before, (pool.proxy_port, slot.exit_kind, slot.flagged, slot.risk_streak, slot.pacer.next_at))

    def test_start_rechecks_stop_after_injected_probe_returns_success(self):
        stop = threading.Event()
        pool = SlotPool([{"name": "offline", "cookie": ""}], stop_event=stop)
        self.addCleanup(pool.slots[0].session.close)

        def stop_on_probe(**kwargs):
            stop.set()
            return 7897, True, "offline"

        self.probe.side_effect = stop_on_probe
        with self.assertRaises(CollectCancelled):
            pool.start()
        self.assertFalse(pool._started)
        self.assertEqual(0, pool.proxy_port)


if __name__ == "__main__":
    unittest.main()
