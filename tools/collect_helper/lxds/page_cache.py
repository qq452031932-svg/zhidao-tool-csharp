"""Bounded, task-local HTML reuse; parsing and collection decisions stay unchanged."""
from collections import OrderedDict
import re
from urllib.parse import urlsplit

from lxds.pool import CollectCancelled
from lxds.zhidao_parse import extract_collect_entries, parse_detail_record, parse_search_page


class TaskPageCache:
    """Store only recognised normal pages; never persist HTML or account data."""

    def __init__(self, stop_event, pool, max_entries=128, max_chars=4_000_000):
        self.stop_event = stop_event
        self.pool = pool
        self.max_entries = max_entries
        self.max_chars = max_chars
        self.hits = 0
        self.clear()

    def clear(self):
        self._pages = OrderedDict()
        self._chars = 0
        self._generation = getattr(self.pool, 'cache_generation', 0)

    def reset(self):
        self.clear()
        self.hits = 0

    def _check(self):
        if self.stop_event.is_set():
            raise CollectCancelled('采集已停止')
        if self._generation != getattr(self.pool, 'cache_generation', 0):
            self.clear()

    @staticmethod
    def _key(url):
        parsed = urlsplit(url)
        if parsed.scheme not in ('http', 'https') or parsed.netloc not in ('zhidao.baidu.com', 'www.baidu.com'):
            return None
        if parsed.netloc == 'zhidao.baidu.com':
            match = re.fullmatch(r'/question/(\d+)(?:\.html)?', parsed.path)
            if match:
                return 'detail', match.group(1)
            if parsed.path == '/search':
                return 'zhidao-search', url.split('#', 1)[0]
        elif parsed.path == '/s':
            return 'site-search', url.split('#', 1)[0]
        return None

    @staticmethod
    def _normal(kind, html):
        if not isinstance(html, str) or not html:
            return False
        if any(marker in html for marker in ('安全验证', 'wappass', '请输入验证码', '访问验证', '访问异常')):
            return False
        if kind == 'zhidao-search':
            return bool(parse_search_page(html)[0])
        if kind == 'site-search':
            return bool(re.search(r'https?://zhidao\.baidu\.com/question/\d+', html))
        _, body, state = parse_detail_record(html)
        return bool(body) and (state == 'ok' or (state == 'fallback' and bool(extract_collect_entries(html))))

    def get(self, fetcher, url, **kwargs):
        self._check()
        key = self._key(url)
        if key in self._pages:
            result = self._pages.pop(key)
            self._pages[key] = result
            self.hits += 1
            return result
        result = fetcher.get(url, **kwargs)
        # An underlying request may have handled risk/recovery, or received stop.
        self._check()
        html = result[0]
        if (key is not None and self.max_entries > 0 and isinstance(html, str)
                and len(html) <= self.max_chars and self._normal(key[0], html)):
            while self._pages and (len(self._pages) >= self.max_entries or self._chars + len(html) > self.max_chars):
                _, removed = self._pages.popitem(last=False)
                self._chars -= len(removed[0])
            self._pages[key] = result
            self._chars += len(html)
        return result


class CachedFetcher:
    """Reuse one cache across ordinary and pinned-exit fetchers."""

    def __init__(self, fetcher, cache):
        self.fetcher = fetcher
        self.cache = cache

    def get(self, url, **kwargs):
        return self.cache.get(self.fetcher, url, **kwargs)
