"""Offline HTTP-byte regressions for Chinese search titles and page markers."""

from pathlib import Path
import sys
import threading
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import requests

from lxds.pool import SlotPool, is_risk_html
from lxds.zhidao_parse import (
    classify_search_html,
    extract_resource_name,
    parse_detail_page,
    parse_search_page,
)


TITLE = "求《植物大战僵尸》百度网盘链接"
SEARCH = (
    '<html><head>{meta}</head><body><dl><dt>'
    '<a href="https://zhidao.baidu.com/question/101.html?fr=search">'
    + TITLE
    + '</a></dt><dd>分享 https://pan.baidu.com/s/offline 提取码：ab12</dd>'
    '</dl></body></html>'
)


def raw_response(body, content_type="text/html"):
    response = requests.Response()
    response.status_code = 200
    response._content = body
    if content_type is not None:
        response.headers["Content-Type"] = content_type
    # This is what Requests' HTTPAdapter does after receiving response headers.
    response.encoding = requests.utils.get_encoding_from_headers(response.headers)
    return response


class PoolDecodingTests(unittest.TestCase):
    def setUp(self):
        proxy_probe = patch("lxds.pool.probe_clash_detail", return_value=(0, False, "offline"))
        proxy_probe.start()
        self.addCleanup(proxy_probe.stop)
        self.pool = SlotPool(
            [{"name": "offline", "cookie": "", "exit_kind": "direct"}],
            pause_ask=lambda _: False,
            on_log=lambda _: None,
            stop_event=threading.Event(),
            on_status=lambda _: None,
        )
        self.pool.start()
        self.slot = self.pool.slots[0]
        self.addCleanup(self.slot.session.close)

    def fetch(self, response, purpose="search"):
        with patch.object(self.slot.session, "get", return_value=response):
            return self.pool.fetch(self.slot, "https://zhidao.baidu.com/offline", purpose=purpose)

    def assert_search_title(self, response):
        body = self.fetch(response)
        items, _ = parse_search_page(body)
        self.assertEqual(TITLE, items[0]["title"])
        self.assertEqual("植物大战僵尸", extract_resource_name(items[0]["title"]))
        self.assertIn("提取码：ab12", items[0]["text"])

    def test_utf8_html_without_http_charset_does_not_use_requests_latin1_default(self):
        response = raw_response(SEARCH.format(meta="").encode("utf-8"))
        self.assertEqual("ISO-8859-1", response.encoding)
        self.assertIn(TITLE.encode("utf-8").decode("latin1"), response.text)
        self.assert_search_title(response)

    def test_utf8_meta_declarations_are_read_from_raw_bytes(self):
        for meta in (
            '<meta charset="UTF-8">',
            "<META CHARSET='utf-8' />",
            '<meta http-equiv="Content-Type" content="text/html; charset=utf-8">',
        ):
            with self.subTest(meta=meta):
                self.assert_search_title(raw_response(SEARCH.format(meta=meta).encode("utf-8")))

    def test_gbk_and_gb18030_http_declarations_preserve_chinese(self):
        for encoding, suffix in (("gbk", ""), ("gb18030", "𠮷")):
            with self.subTest(encoding=encoding):
                source = SEARCH.format(meta="") + suffix
                response = raw_response(source.encode(encoding), f'text/html; charset="{encoding}"')
                self.assertEqual(source, self.fetch(response))
                self.assert_search_title(response)

    def test_gbk_and_gb18030_html_declarations_override_implicit_latin1(self):
        for encoding, suffix in (("gbk", ""), ("gb18030", "𠮷")):
            for meta in (
                f'<meta charset="{encoding}">',
                f'<meta content="text/html; charset={encoding}" http-equiv="Content-Type">',
            ):
                with self.subTest(encoding=encoding, meta=meta):
                    source = SEARCH.format(meta=meta) + suffix
                    self.assertEqual(source, self.fetch(raw_response(source.encode(encoding))))

    def test_gb18030_without_encoding_declaration_is_preserved(self):
        source = SEARCH.format(meta="") + "𠮷"
        self.assertEqual(source, self.fetch(raw_response(source.encode("gb18030"))))

    def test_valid_http_charset_takes_priority_over_conflicting_meta(self):
        source = '<meta charset="utf-8"><p>cafÃ©</p>'
        response = raw_response(source.encode("latin1"), "text/html; charset=iso-8859-1")
        self.assertEqual(source, self.fetch(response))

    def test_utf8_text_that_resembles_mojibake_is_not_repaired(self):
        source = '<meta charset="utf-8"><p>真实标题与 cafÃ© 原文</p>'
        self.assertEqual(source, self.fetch(raw_response(source.encode("utf-8"))))

    def test_byte_order_mark_takes_priority_over_http_charset(self):
        source = SEARCH.format(meta="")
        for encoding in ("utf-8-sig", "utf-16", "utf-32"):
            with self.subTest(encoding=encoding):
                response = raw_response(source.encode(encoding), "text/html; charset=gbk")
                self.assertEqual(source, self.fetch(response))

    def test_invalid_or_missing_http_charset_falls_back_to_utf8(self):
        for content_type in (None, "text/html; charset=not-a-real-encoding", "text/html; charset=ascii"):
            with self.subTest(content_type=content_type):
                self.assert_search_title(raw_response(SEARCH.format(meta="").encode("utf-8"), content_type))

    def test_chinese_control_markers_are_decoded_before_classification(self):
        security = self.fetch(raw_response("<html>百度安全验证</html>".encode("utf-8")))
        self.assertTrue(is_risk_html(security))
        empty = self.fetch(raw_response("<html>没有找到相关结果</html>".encode("utf-8")))
        self.assertEqual("empty", classify_search_html(empty, []))
        deleted = self.fetch(raw_response("<html>该问题已被删除</html>".encode("utf-8")), purpose="detail")
        self.assertEqual(("", "dead"), parse_detail_page(deleted))

    def test_empty_response_stays_empty(self):
        self.assertEqual("", self.fetch(raw_response(b"")))


if __name__ == "__main__":
    unittest.main()
