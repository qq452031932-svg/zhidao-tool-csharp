"""Behavior contracts transcribed from v1.0.4 collect_behavior bytecode."""
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from lxds import behavior


class LegacyBehaviorParityTests(unittest.TestCase):
    def test_normal_gap_includes_reading_and_returns_rounded_gap(self):
        pacer = behavior.HumanPacer((6, 6), (8, 8), (12, 12))
        with patch.object(behavior.time, "time", return_value=100):
            self.assertEqual((False, 14.3), pacer.note_request(14.26))
        self.assertAlmostEqual(14.26, pacer.wait_time(100))

    def test_periodic_break_replaces_even_longer_read_time(self):
        pacer = behavior.HumanPacer((6, 6), (1, 1), (12.34, 12.34))
        with patch.object(behavior.time, "time", return_value=100):
            self.assertEqual((True, 12.3), pacer.note_request(40))
        self.assertAlmostEqual(12.34, pacer.wait_time(100))

    def test_successful_request_replaces_prior_cooldown(self):
        pacer = behavior.HumanPacer((6, 6), (8, 8), (12, 12))
        with patch.object(behavior.time, "time", return_value=100):
            pacer.cool_down(90)
            pacer.note_request(3)
        self.assertEqual(6, pacer.wait_time(100))

    def test_reset_clears_total_and_next_break_counters(self):
        pacer = behavior.HumanPacer((6, 6), (8, 8), (12, 12))
        pacer.note_request(3)
        self.assertEqual(1, getattr(pacer, "total", None))
        pacer.reset()
        self.assertEqual((0, 0, 0, 8), (pacer.total, pacer.next_at, pacer.count_since_break, pacer.next_break_after))

    def test_common_question_selection_does_not_mutate_used(self):
        used = set()
        question = behavior.pick_common_question(used)
        self.assertEqual(set(), used)
        self.assertIn(question, behavior._COMMON_QUESTIONS)

    def test_exhausted_questions_remain_used_and_original_thirty_are_available(self):
        self.assertEqual(30, len(behavior._COMMON_QUESTIONS))
        used = set(behavior._COMMON_QUESTIONS)
        before = used.copy()
        self.assertIn(behavior.pick_common_question(used), used)
        self.assertEqual(before, used)
        self.assertEqual("明天天气怎么样", behavior._COMMON_QUESTIONS[0])
        self.assertEqual("快递一直不更新物流怎么办", behavior._COMMON_QUESTIONS[-1])

    def test_shuffle_has_default_ten_and_preserves_input(self):
        source = list(range(23))
        try:
            with patch.object(behavior.random, "shuffle", side_effect=lambda block: block.reverse()):
                shuffled = behavior.shuffle_blocks(source)
        except TypeError:
            self.fail("shuffle_blocks must default to blocks of ten")
        self.assertEqual(list(range(9, -1, -1)) + list(range(19, 9, -1)) + [22, 21, 20], shuffled)
        self.assertEqual(list(range(23)), source)

    def test_profile_fields_match_original_and_are_copied(self):
        with patch.object(behavior.random, "choice", side_effect=lambda profiles: profiles[-1]):
            profile = behavior.pick_profile()
        self.assertEqual("", profile.get("platform"))
        self.assertEqual("", profile.get("sec_ch_ua"))
        self.assertEqual("", profile.get("mobile"))
        profile["ua"] = "changed"
        self.assertNotEqual("changed", behavior._PROFILES[-1]["ua"])


if __name__ == "__main__":
    unittest.main()
