"""v1.0.4 business.baidu_web_scraper site-search executable contracts."""
from pathlib import Path
import sys
import unittest
from urllib.parse import parse_qs, urlsplit

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from lxds.websearch import BaiduSiteSearchCollector, BaiduWebError, site_queries


class FastStop:
    stopped = False
    def is_set(self):
        return self.stopped
    def wait(self, timeout):
        return self.stopped


class Fetcher:
    def __init__(self, respond):
        self.respond = respond
        self.calls = []
    def get(self, url, **kwargs):
        self.calls.append((url, kwargs))
        return self.respond(url), None


def question_urls(page, count=6):
    return ' '.join(f'https://zhidao.baidu.com/question/{page * 10 + i + 1000}' for i in range(count))


def detail(url, href=False):
    identity = url.rsplit('/', 1)[-1]
    link = f'https://pan.baidu.com/s/1resource{identity}'
    content = f'<a href="{link}">下载</a>' if href else link
    return f'<title>求《资源{identity}》百度网盘_百度知道</title><div>{content} 提取码：a123</div>'


class LegacyWebParityTests(unittest.TestCase):
    def test_site_malformed_title_cleaning_follows_original_script_style_order(self):
        for title, expected in (('<script>噪声</style>书名', '噪声 书名'),
                                ('<scriptx>书名</script>', 'taptap')):
            with self.subTest(title=title):
                fetcher = Fetcher(lambda url: '<title>' + title + '</title>https://pan.baidu.com/s/1abcdefgh 提取码：a123'
                                  if '/question/' in url else question_urls(0, 1))
                stream = BaiduSiteSearchCollector(fetcher, FastStop()).iter_search('taptap', None)
                self.assertEqual(expected, next(stream)['title'])
                stream.close()

    def test_site_title_requires_original_lowercase_tag_without_attributes(self):
        for tag in ('<TITLE>《别的标题》</TITLE>', '<title data-x="a">《别的标题》</title>'):
            with self.subTest(tag=tag):
                fetcher = Fetcher(lambda url: tag + 'https://pan.baidu.com/s/1abcdefgh 提取码：a123'
                                  if '/question/' in url else question_urls(0, 1))
                stream = BaiduSiteSearchCollector(fetcher, FastStop()).iter_search('taptap', None)
                self.assertEqual('taptap', next(stream)['title'])
                stream.close()

    def test_named_term_has_original_six_suffixes(self):
        self.assertEqual([
            f'site:zhidao.baidu.com taptap {suffix}' for suffix in
            ('百度网盘', '合集 百度网盘', '下载 百度网盘', '资源 百度网盘', '全套 百度网盘', '最新 百度网盘')
        ], site_queries(' taptap '))

    def test_domain_terms_expand_in_original_order(self):
        self.assertEqual('site:zhidao.baidu.com 单机游戏 百度网盘', site_queries('游戏')[0])
        self.assertEqual(18, len(site_queries('游戏')))
        self.assertEqual('site:zhidao.baidu.com 全本小说 百度网盘', site_queries('小说')[0])

    def test_six_pages_six_details_per_page_full_html_links(self):
        def respond(url):
            if '/question/' in url:
                return detail(url, href=True)
            pn = int(parse_qs(urlsplit(url).query)['pn'][0])
            return question_urls(pn, 8)
        fetcher = Fetcher(respond)
        records = BaiduSiteSearchCollector(fetcher, FastStop(), max_variants=1).search('taptap', None)
        self.assertEqual(36, len(records))
        searches = [url for url, _ in fetcher.calls if '/question/' not in url]
        self.assertEqual([0, 10, 20, 30, 40, 50], [int(parse_qs(urlsplit(url).query)['pn'][0]) for url in searches])
        self.assertTrue(all(row['source_url'].startswith('https://zhidao.baidu.com/question/') for row in records))
        self.assertEqual('a123', records[0]['links'][0]['pwd'])
        self.assertNotIn('?pwd=', records[0]['links'][0]['link'])

    def test_three_barren_variants_end_default_six_variant_search(self):
        fetcher = Fetcher(lambda url: '没有找到相关结果')
        self.assertEqual([], BaiduSiteSearchCollector(fetcher, FastStop()).search('taptap', None))
        queries = [parse_qs(urlsplit(url).query) for url, _ in fetcher.calls]
        self.assertEqual(9, len(queries))
        self.assertEqual(site_queries('taptap')[:3], list(dict.fromkeys(row['wd'][0] for row in queries)))

    def test_duplicate_run_breaks_detail_loop_only_and_details_can_repeat(self):
        fetcher = Fetcher(lambda url: detail(url) if '/question/' in url else question_urls(0, 6))
        records = BaiduSiteSearchCollector(fetcher, FastStop(), max_variants=1).search('taptap', None)
        self.assertEqual(6, len(records))
        # First page 6 hits, second page 3 duplicate details, then one on each later page.
        self.assertEqual(11, len([url for url, _ in fetcher.calls if '/question/' in url]))
        self.assertEqual(4, len([url for url, _ in fetcher.calls if '/question/' not in url]))

    def test_risk_markers_stop_even_on_long_response(self):
        fetcher = Fetcher(lambda url: 'x' * 70000 + '百度安全验证')
        collector = BaiduSiteSearchCollector(fetcher, FastStop())
        self.assertEqual([], collector.search('taptap', None))
        self.assertTrue(collector.stop_supplement)
        self.assertEqual(1, len(fetcher.calls))

    def test_detail_web_error_skips_detail_but_not_remaining_page(self):
        fetcher = Fetcher(lambda url: question_urls(0, 2))
        collector = BaiduSiteSearchCollector(fetcher, FastStop(), max_variants=1)
        def request(url, referer=None):
            if url.endswith('/1000'):
                raise BaiduWebError('fixture')
            if '/question/' in url:
                return detail(url)
            return question_urls(0, 2)
        collector._request = request
        stream = collector.iter_search('taptap', None)
        self.assertEqual('https://zhidao.baidu.com/question/1001', next(stream)['source_url'])
        stream.close()


if __name__ == '__main__':
    unittest.main()
