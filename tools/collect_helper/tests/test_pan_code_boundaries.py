"""Offline regressions for share URL and extraction-code boundaries."""

from pathlib import Path
import sys
import threading
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from lxds.zhidao_parse import extract_pan_entries, extract_pwd, normalize_pan_url
from lxds.websearch import BaiduWebCollector


LINK = "https://pan.baidu.com/s/offline-share"


class PanCodeBoundaryTests(unittest.TestCase):
    def test_chinese_prose_after_query_is_not_part_of_share_url(self):
        self.assertEqual(
            [{"link": LINK + "?pwd=38y6", "pwd": "38y6"}],
            extract_pan_entries("分享 " + LINK + "?pwd=38y6这是资源说明"),
        )

    def test_complete_url_normalization_does_not_guess_where_opaque_value_ends(self):
        url = LINK + "?pwd=38y6这是"
        self.assertEqual(url, normalize_pan_url(url))
        self.assertEqual("", extract_pwd(url, url))

    def test_quoted_href_uses_attribute_boundary_without_shortening_invalid_value(self):
        for value, expected_code in (("38y6", "38y6"), ("38y6这是", ""), ("38y6ABCD", "")):
            with self.subTest(value=value):
                url = LINK + "?pwd=" + value
                self.assertEqual(
                    [{"link": url, "pwd": expected_code}],
                    extract_pan_entries('<a href="' + url + '">网盘链接</a>这是资源说明'),
                )

    def test_escaped_quote_in_href_is_part_of_invalid_value_not_a_boundary(self):
        self.assertEqual(
            [{"link": LINK + '?pwd=38y6"extra', "pwd": ""}],
            extract_pan_entries('<a href="' + LINK + '?pwd=38y6&quot;extra">网盘链接</a>'),
        )

    def test_href_without_share_id_is_not_a_resource(self):
        self.assertEqual([], extract_pan_entries('<a href="https://pan.baidu.com/s/">网盘首页</a>'))

    def test_chinese_punctuation_and_labels_end_share_url(self):
        for suffix in ("（资源说明）", "：资源说明", "！资源说明", "？资源说明", "提取码：38y6"):
            with self.subTest(suffix=suffix):
                self.assertEqual(
                    [{"link": LINK + "?pwd=38y6", "pwd": "38y6"}],
                    extract_pan_entries(LINK + "?pwd=38y6" + suffix),
                )

    def test_labeled_four_ascii_characters_can_be_followed_by_chinese_prose(self):
        for label in ("提取码", "提取密码", "访问码", "密码"):
            with self.subTest(label=label):
                self.assertEqual(
                    [{"link": LINK + "?pwd=38y6", "pwd": "38y6"}],
                    extract_pan_entries(LINK + " " + label + "：38y6这是资源说明"),
                )

    def test_invalid_query_codes_are_not_silently_shortened(self):
        for invalid in ("abc", "abcde", "38y6ABCD", "12345678", "38y6_more", "38y6-more"):
            with self.subTest(invalid=invalid):
                url = LINK + "?pwd=" + invalid
                self.assertEqual([{"link": url, "pwd": ""}], extract_pan_entries(url))
                self.assertEqual("", extract_pwd(url, url))

    def test_invalid_labeled_codes_are_not_silently_shortened(self):
        for invalid in ("abc", "abcde", "38y6ABCD", "12345678", "38y6_more", "38y6-more", "ＡＢＣＤ", "Kabc", "38y6é"):
            with self.subTest(invalid=invalid):
                self.assertEqual(
                    [{"link": LINK, "pwd": ""}],
                    extract_pan_entries(LINK + " 提取码：" + invalid),
                )

    def test_no_explicit_code_remains_empty(self):
        for suffix in ("", " 38y6", " 这是无需提取码的资源", "?from=38y6"):
            with self.subTest(suffix=suffix):
                self.assertEqual("", extract_pan_entries(LINK + suffix)[0]["pwd"])

    def test_query_delimiters_and_html_escaped_ampersands_are_preserved(self):
        url = LINK + "?from=search&amp;pwd=38y6&amp;token=abc"
        self.assertEqual(
            [{"link": LINK + "?from=search&pwd=38y6&token=abc", "pwd": "38y6"}],
            extract_pan_entries(url + "这是说明"),
        )

    def test_websearch_supplement_uses_the_same_plain_text_boundaries(self):
        class Fetcher:
            def get(self, url, **kwargs):
                return (
                    '<h3><a href="https://example.com/offline">资源</a></h3>'
                    '<div>' + LINK + '?pwd=38y6这是资源说明</div>',
                    None,
                )

        records = BaiduWebCollector(Fetcher(), threading.Event()).search("资源", None)
        self.assertEqual([{"link": LINK + "?pwd=38y6", "pwd": "38y6"}], records[0]["links"])


if __name__ == "__main__":
    unittest.main()
