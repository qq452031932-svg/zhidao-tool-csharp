"""Rules recovered from v1.0.4 zhidao_scraper disassembly, with URL fixes retained."""

from datetime import date, timedelta
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from lxds import zhidao_parse as parser


LINK = "https://pan.baidu.com/s/1abcdefghijk"


def detail_html(body, title="实际问题"):
    return (f"<html><head><title>{title}_百度知道</title></head><body>{body}</body>"
            + "<script>" + "x" * 2100 + "</script></html>")


class LegacyNameParityTests(unittest.TestCase):
    def test_quoted_names_do_not_strip_real_name_prefixes(self):
        for name in ("求佛", "资源帝国", "小说之神", "眉蹙春山作者:七月闻蝉", "这个杀手不太冷"):
            with self.subTest(name=name):
                self.assertEqual(name, parser.extract_resource_name(f"求《{name}》百度网盘链接"))
                self.assertTrue(parser.has_concrete_name(f"《{name}》"))

    def test_generic_only_and_numeric_names_are_rejected_even_when_quoted(self):
        for name in ("一下", "书合集", "起点", "合集", "全本", "全集", "小说", "资源", "网盘",
                     "链接", "下载", "热门小说全集", "123456", "A1", "中123", "--"):
            for title in (name, f"《{name}》"):
                with self.subTest(title=title):
                    self.assertEqual("", parser.extract_resource_name(title))
                    self.assertFalse(parser.has_concrete_name(title))

    def test_unquoted_generic_words_are_rejected(self):
        for title in ("这个游戏", "该资源", "一个故事", "热门长篇", "免费长篇", "长安番外", "在线长篇"):
            with self.subTest(title=title):
                self.assertEqual("", parser.extract_resource_name(title))

    def test_unquoted_name_uses_first_noise_boundary(self):
        for suffix in ("完整版", "全集", "全本", "高清", "无删减", "百度网盘", "资源", "下载", "提取码", "TXT"):
            with self.subTest(suffix=suffix):
                self.assertEqual("三体", parser.extract_resource_name("求小说三体" + suffix + "后续文字"))

    def test_unquoted_domain_and_by_author_are_removed(self):
        for title in ("请问：漫画三体 by 刘慈欣", "想要手游：三体", "电脑软件：AutoCAD"):
            with self.subTest(title=title):
                expected = "AutoCAD" if "AutoCAD" in title else "三体"
                self.assertEqual(expected, parser.extract_resource_name(title))

    def test_filename_keeps_legacy_name_characters_and_numeric_prefix(self):
        self.assertEqual("9_三体", parser.extract_resource_name("9_三体.txt"))
        self.assertEqual("三体·黑暗森林＆地球", parser.extract_resource_name("求小说三体·黑暗森林＆地球.epub"))

    def test_empty_and_oversized_names_are_rejected(self):
        for title in (None, "", "中", "中" * 41):
            with self.subTest(title=title):
                self.assertEqual("", parser.extract_resource_name(title))

    def test_clean_title_is_export_fallback_and_does_not_remove_number_or_extension(self):
        self.assertEqual("9_三体.txt", parser.clean_title_for_name("9_三体.txt"))
        self.assertEqual("三体百度", parser.clean_title_for_name("求《三体》百度网盘链接"))
        self.assertEqual("三体特别版", parser.clean_title_for_name("跪求【三体】（特别版）下载"))

    def test_spam_letter_cleanup_does_not_remove_trailing_english(self):
        self.assertEqual("边关兵王abcd", parser.strip_spam_letters(" 边I关i兵I王abcd "))
        for title in ("阿Q正传", "斗罗大陆I绝世唐门", "9_三体", "IPX-580", "English Name"):
            with self.subTest(title=title):
                self.assertEqual(title, parser.strip_spam_letters(title))
        self.assertEqual("", parser.strip_spam_letters(None))

    def test_guess_prefers_last_book_in_link_window_over_question(self):
        text = f"《最早书名》 《目标书名》 {LINK}"
        self.assertEqual("目标书名", parser.guess_resource_name("求《标题书名》下载", text, LINK))

    def test_guess_window_uses_400_characters_before_and_100_after_link_start(self):
        text = "《太远的书》" + "x" * 401 + f"《附近的书》 {LINK}" + "x" * 101 + "《尾部无关书》"
        self.assertEqual("附近的书", parser.guess_resource_name("求《标题书名》下载", text, LINK))

    def test_guess_returns_book_text_without_concrete_name_revalidation(self):
        self.assertEqual("资源", parser.guess_resource_name("三体", f"《资源》{LINK}", LINK))

    def test_guess_missing_link_searches_first_500_characters(self):
        self.assertEqual("开头的书", parser.guess_resource_name("三体", "《开头的书》" + "x" * 501 + "《尾书》", LINK))

    def test_guess_without_book_uses_legacy_title_fallback_then_link(self):
        self.assertEqual("9_三体.txt", parser.guess_resource_name("9_三体.txt", "别的文件.epub", LINK))
        self.assertEqual(LINK, parser.guess_resource_name("", "别的文件.epub", LINK))


class LegacyPageParityTests(unittest.TestCase):
    def record(self, source):
        self.assertTrue(callable(getattr(parser, "parse_detail_record", None)), "strict legacy detail API is missing")
        return parser.parse_detail_record(source)

    def test_strict_detail_rejects_short_pages_before_title_or_dead_marker(self):
        self.assertEqual(("", "", "empty"), self.record("<title>标题_百度知道</title>该问题可能已经失效"))
        self.assertEqual(("", "", "empty"), self.record(" " * 1999))
        self.assertEqual(("", "", "fallback"), self.record(" " * 2000))

    def test_strict_detail_returns_actual_title_and_all_six_legacy_containers(self):
        cases = (
            '<div class="line content">正文<span>iknow-qb_home_icons</span></div>',
            '<div class="line content">正文</div></div>',
            '<div id="answer-content-123">正文</div></div>',
            '<div class="answer-text">正文</div></div>',
            '<div class="best-text">正文</div>',
            '<section class="answer-other-content">正文</div>',
        )
        for body in cases:
            with self.subTest(body=body):
                self.assertEqual(("实际 标题 &名称", "正文", "ok"), self.record(detail_html(body, "实际 <b>标题</b>&amp;名称")))

    def test_strict_detail_uses_pattern_priority_and_first_nonempty_match(self):
        body = ('<div class="best-text">低优先级</div>'
                '<div class="line content">最高优先级</div></div>'
                '<div class="line content">第二块</div></div>')
        self.assertEqual(("实际问题", "最高优先级", "ok"), self.record(detail_html(body)))

    def test_strict_detail_recognizes_original_dead_markers(self):
        for marker in ("该问题可能已经失效", "知道宝贝找不到问题了"):
            with self.subTest(marker=marker):
                self.assertEqual(("实际问题", "", "dead"), self.record(detail_html(marker)))

    def test_strict_detail_fallback_limits_text_to_body_and_removes_answer_noise(self):
        body = "收起 正文 本回答由百度网友推荐 本回答被提问者采纳 其他回答 热心网友"
        self.assertEqual(("实际问题", "正文", "fallback"), self.record(detail_html(body)))

    def test_strict_detail_cleans_answer_noise_inside_container(self):
        body = '<div class="best-text">展开全部 正文 本回答由某某推荐 本回答被提问者采纳 其他回答 热心网友</div>'
        self.assertEqual(("实际问题", "正文", "ok"), self.record(detail_html(body)))

    def test_public_detail_tuple_still_accepts_tiny_fixtures_and_newer_dead_marker(self):
        self.assertEqual(("正文", "ok"), parser.parse_detail_page('<div class="best-text">正文</div>'))
        self.assertEqual(("", "dead"), parser.parse_detail_page("该问题已被删除"))

    def test_search_empty_markers_match_original_and_large_pages_stay_unknown(self):
        for marker in ("没有找到", "找不到", "没有相关", "未找到", "换个关键词", "换个词试试"):
            with self.subTest(marker=marker):
                self.assertEqual("empty", parser.classify_search_html(marker, []))
                self.assertEqual("unknown", parser.classify_search_html(marker + "x" * 80000, []))
        self.assertEqual("normal", parser.classify_search_html("换个词试试", [{"title": "三体"}]))

    def test_search_ignores_empty_title(self):
        source = '<a href="https://zhidao.baidu.com/question/123.html?fr=search"> <i></i> </a>'
        self.assertEqual([], parser.parse_search_page(source)[0])

    def test_search_pagination_falls_back_to_max_pn_plus_ten_below_900(self):
        self.assertEqual(40, parser.parse_search_page('<a href="/search?pn=10"></a><a href="/search?pn=30"></a>')[1])
        self.assertIsNone(parser.parse_search_page('<a href="/search?pn=900"></a>')[1])
        self.assertEqual(20, parser.parse_search_page('<a href="/search?pn=100"></a><a class="pager-next" href="/search?pn=20"></a>')[1])

    def test_date_range_accepts_future_dates_and_numeric_string_days(self):
        self.assertTrue(parser.within_range((date.today() + timedelta(days=2)).isoformat(), 30))
        self.assertTrue(parser.within_range(date.today().isoformat(), "30"))
        self.assertFalse(parser.within_range("2001-1-1", 30))
        self.assertTrue(parser.within_range("unknown", 30))


class LegacyCollectionLinkParityTests(unittest.TestCase):
    def entries(self, text):
        self.assertTrue(callable(getattr(parser, "extract_collect_entries", None)), "strict collection entry API is missing")
        return parser.extract_collect_entries(text)

    def test_collection_links_require_eight_character_share_id(self):
        self.assertEqual([], self.entries("https://pan.baidu.com/s/1234567"))
        self.assertEqual([{"link": "https://pan.baidu.com/s/12345678", "pwd": ""}],
                         self.entries("https://pan.baidu.com/s/12345678"))

    def test_collection_identity_is_share_id_regardless_of_scheme_or_pwd(self):
        self.assertTrue(callable(getattr(parser, "collect_link_key", None)), "collection share-ID API is missing")
        self.assertEqual("1abcdefghijk", parser.collect_link_key(LINK + "?pwd=abcd"))
        self.assertEqual("1abcdefghijk", parser.collect_link_key(LINK.replace("https:", "http:")))
        self.assertEqual([{"link": LINK, "pwd": ""}], self.entries(LINK + " " + LINK.replace("https:", "http:") + "?pwd=abcd"))

    def test_collection_pwd_stays_separate_and_nearest_code_wins(self):
        self.assertEqual([{"link": LINK, "pwd": "new2"}],
                         self.entries("提取码：old1 " + "x" * 30 + LINK + " 提取码：new2"))

    def test_collection_pwd_uses_legacy_150_before_300_after_window(self):
        self.assertEqual("far1", self.entries("提取码：far1 " + "x" * 100 + LINK)[0]["pwd"])
        self.assertEqual("far2", self.entries(LINK + "x" * 0 + " " + "x" * 200 + " 提取码：far2")[0]["pwd"])
        self.assertEqual("", self.entries("提取码：far1 " + "x" * 151 + LINK)[0]["pwd"])
        self.assertEqual("", self.entries(LINK + " " + "x" * 301 + " 提取码：far2")[0]["pwd"])

    def test_collection_pwd_prefers_url_and_preserves_boundary_fixes(self):
        self.assertEqual([{"link": LINK + "?pwd=38y6", "pwd": "38y6"}],
                         self.entries(LINK + "?pwd=38y6这是说明 提取码：zzzz"))
        for value in ("38y6这是", "38y6ABCD", "38y6_more"):
            with self.subTest(value=value):
                url = LINK + "?pwd=" + value
                self.assertEqual([{"link": url, "pwd": ""}], self.entries('<a href="' + url + '">网盘链接</a>'))
        self.assertEqual([{"link": LINK, "pwd": ""}], self.entries(LINK + " 提取码：38y6_more"))

    def test_public_entry_api_keeps_short_ids_and_appended_codes(self):
        self.assertEqual([{"link": "https://pan.baidu.com/s/demo?pwd=abcd", "pwd": "abcd"}],
                         parser.extract_pan_entries("https://pan.baidu.com/s/demo 提取码：abcd"))


if __name__ == "__main__":
    unittest.main()
