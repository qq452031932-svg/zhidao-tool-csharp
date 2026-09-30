"""百度网页摘要与知道站点补量；采集器通过注入的 fetcher 访问网络。"""

from __future__ import annotations

import html as html_lib
import re
import random
from collections.abc import Iterator
from threading import Event
from typing import Callable
from urllib.parse import quote

import requests

from lxds.zhidao_parse import extract_pan_entries, extract_collect_entries
from lxds.collection_rules import (SITE_VARIANTS, SITE_QUERY_SUFFIXES, SITE_MAX_PAGES,
    SITE_BARREN_PAGES, SITE_BARREN_VARIANTS, SITE_DUP_RUN, SITE_DETAIL_PER_PAGE, SITE_MAX_VARIANTS)


_SEARCH_URL = "https://www.baidu.com/s?wd={}&pn={}"
_HEADING = re.compile(
    r"<h3\b[^>]*>\s*<a\b[^>]*href\s*=\s*(['\"])(.*?)\1[^>]*>(.*?)</a>\s*</h3>",
    re.I | re.S,
)
_DETAIL_URL = re.compile(r"https?://zhidao\.baidu\.com/question/\d+(?:\.html)?", re.I)
_TITLE = re.compile(r"<title\b[^>]*>(.*?)</title>", re.I | re.S)
_SCRIPT = re.compile(r"<(?:script|style)\b[^>]*>[\s\S]*?</(?:script|style)>", re.I)
_TAGS = re.compile(r"<[^>]+>")
_RISK_WORDS = ("安全验证", "百度安全验证", "访问验证", "wappass", "验证码", "异常访问")


class BaiduWebError(Exception):
    """搜索页面不可用或被风控。"""


def _plain(fragment: str) -> str:
    fragment = _SCRIPT.sub(" ", fragment)
    fragment = _TAGS.sub(" ", fragment)
    return re.sub(r"\s+", " ", html_lib.unescape(fragment)).strip()


def _site_plain(fragment: str) -> str:
    """站点补量保持原版清理顺序，包括残缺标签的处理。"""
    fragment = re.sub(r"<script[\s\S]*?</script>", " ", fragment, flags=re.I)
    fragment = re.sub(r"<style[\s\S]*?</style>", " ", fragment, flags=re.I)
    return re.sub(r"\s+", " ", html_lib.unescape(_TAGS.sub(" ", fragment))).strip()


def _looks_like_risk(text: str) -> bool:
    # 正常搜索页可能也提及“验证码”，短页才视作风控响应。
    return len(text) < 5000 and any(word in text for word in _RISK_WORDS)


def _heading_blocks(text: str) -> list[tuple[str, str, str]]:
    matches = list(_HEADING.finditer(text))
    blocks = []
    for index, match in enumerate(matches):
        end = matches[index + 1].start() if index + 1 < len(matches) else len(text)
        url = html_lib.unescape(match.group(2)).strip()
        blocks.append((_plain(match.group(3)), url, text[match.start():end]))
    return blocks


def site_queries(term: str) -> list[str]:
    """v1.0.4 的知道站点查询；领域扩展与普通关键词后缀分别处理。"""
    cleaned = (term or "").strip()
    if not cleaned:
        return []
    variants = SITE_VARIANTS.get(cleaned)
    if variants:
        return [f"site:zhidao.baidu.com {variant} 百度网盘" for variant in variants]
    return [f"site:zhidao.baidu.com {cleaned} {suffix}" for suffix in SITE_QUERY_SUFFIXES]


class BaiduWebCollector:
    """提取百度搜索结果块中直接出现的网盘分享链接。"""

    def __init__(self, fetcher, stop_event: Event) -> None:
        self.fetcher = fetcher
        self.stop_event = stop_event

    def _should_stop(self) -> bool:
        return self.stop_event.is_set()

    def _request(self, url: str, referer: str | None = None) -> str:
        if self.fetcher is None:
            raise BaiduWebError("网页搜索 fetcher 未初始化")
        try:
            text, _slot = self.fetcher.get(url, referer=referer, purpose="search")
        except Exception as exc:
            raise BaiduWebError(f"百度网页请求失败：{exc}") from exc
        if not isinstance(text, str):
            raise BaiduWebError("百度网页返回格式异常")
        if _looks_like_risk(text):
            raise BaiduWebError("百度安全验证，停止本次搜索")
        return text

    @staticmethod
    def _looks_like_risk(text: str) -> bool:
        return _looks_like_risk(text)

    def _search_page(self, url: str) -> str:
        """请求一页并识别风控；异常交给上层保留已采记录。"""
        return self._request(url)

    def search(self, keyword: str, on_log: Callable[[str], None] | None) -> list[dict]:
        """兼容需要列表的调用方；采集调度使用 iter_search 即时消费。"""
        return list(self.iter_search(keyword, on_log))

    def iter_search(self, keyword: str, on_log: Callable[[str], None] | None) -> Iterator[dict]:
        """最多取两页，每个命中立即产出 title/source_url/links。"""
        found = 0
        seen: set[str] = set()
        for pn in (0, 10):
            if self._should_stop() or found >= 20:
                break
            url = _SEARCH_URL.format(quote(keyword), pn)
            try:
                text = self._search_page(url)
            except BaiduWebError as exc:
                if on_log:
                    on_log(f"百度网页搜索失败：{exc}")
                break
            for title, source_url, block in _heading_blocks(text):
                if self._should_stop():
                    return
                links = extract_pan_entries(_plain(block))
                if not links or source_url in seen:
                    continue
                seen.add(source_url)
                record = {"title": title or keyword, "source_url": source_url, "links": links}
                found += 1
                if on_log:
                    on_log(f"网页搜索命中：{record['title'][:40]}（{len(links)} 条链接）")
                yield record
                if found >= 20:
                    break


class BaiduSiteSearchCollector(BaiduWebCollector):
    """用百度 site 查询发现知道问题，详情页中提取网盘链接。"""

    def __init__(self, fetcher, stop_event: Event, max_variants: int = SITE_MAX_VARIANTS) -> None:
        super().__init__(fetcher, stop_event)
        self.max_variants = max(1, int(max_variants))
        self.stop_supplement = False

    def search(self, term: str, on_log: Callable[[str], None] | None) -> list[dict]:
        return list(self.iter_search(term, on_log))

    def _request(self, url: str, referer: str | None = None) -> str:
        # 原版站点补量让请求池处理网络/验证重试，保留它的异常类型。
        if self.fetcher is None:
            raise BaiduWebError("请求入口未初始化")
        text, _ = self.fetcher.get(url, referer=referer, purpose="search")
        return text

    @staticmethod
    def _looks_like_risk(text: str) -> bool:
        return any(marker in text for marker in ("百度安全验证", "wappass", "请输入验证码"))

    def iter_search(self, term: str, on_log: Callable[[str], None] | None) -> Iterator[dict]:
        """原版 search 的逐条适配；保留搜索/详情/去重和退出层级。"""
        seen: set[str] = set()
        barren_variants = 0
        for query in site_queries(term)[:self.max_variants]:
            if self._should_stop() or self.stop_supplement:
                return
            got_any = False
            barren_pages = 0
            dup_run = 0
            for pn in range(0, SITE_MAX_PAGES * 10, 10):
                if self._should_stop() or self.stop_supplement:
                    return
                url = _SEARCH_URL.format(quote(query), pn)
                try:
                    text = self._request(url)
                except BaiduWebError as exc:
                    if on_log:
                        on_log(f"百度一下搜索失败：{exc}")
                    break
                if self._looks_like_risk(text):
                    self.stop_supplement = True
                    if on_log:
                        on_log("百度一下出现安全验证，停止本次补量")
                    return
                # 原版全文找问题 URL，并不按详情 URL 去重；去重对象是盘链接。
                details = list(dict.fromkeys(re.findall(r"https?://zhidao\.baidu\.com/question/\d+", text or "")))
                new_in_page = 0
                for detail_url in details[:SITE_DETAIL_PER_PAGE]:
                    if self._should_stop():
                        return
                    try:
                        detail = self._request(detail_url)
                    except BaiduWebError:
                        continue
                    if self._looks_like_risk(detail):
                        self.stop_supplement = True
                        if on_log:
                            on_log("知道详情页出现安全验证，停止本次补量")
                        return
                    links = [entry for entry in extract_collect_entries(detail)
                             if entry.get("link") and entry["link"] not in seen]
                    if not links:
                        dup_run += 1
                        if dup_run >= SITE_DUP_RUN:
                            if on_log:
                                on_log(f"变体「{query}」连续 {dup_run} 条无新链接，结束本页详情检查")
                            break
                        continue
                    dup_run = 0
                    seen.update(entry["link"] for entry in links)
                    title_match = re.search(r"<title>(.*?)</title>", detail, re.S)
                    title = _site_plain(title_match.group(1)) if title_match else ""
                    new_in_page += 1
                    got_any = True
                    if on_log:
                        on_log(f"知道页命中：{title or term}（{len(links)} 条链接）")
                    yield {"title": title or term, "source_url": detail_url, "links": links}
                barren_pages = 0 if new_in_page else barren_pages + 1
                if barren_pages >= SITE_BARREN_PAGES:
                    break
                if self.stop_event.wait(random.uniform(0.3, 0.8)):
                    return
            barren_variants = 0 if got_any else barren_variants + 1
            if barren_variants >= SITE_BARREN_VARIANTS:
                if on_log:
                    on_log(f"连续 {barren_variants} 个变体无新增，结束本次补量")
                return


def _fetch_snippets(url: str, timeout: int, max_results: int, max_chars: int) -> str:
    session = requests.Session()
    session.trust_env = False
    session.headers.update({
        "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0 Safari/537.36",
        "Accept-Language": "zh-CN,zh;q=0.9",
        "Referer": "https://www.baidu.com/",
    })
    try:
        response = session.get(url, timeout=timeout)
        if response.status_code != 200 or _looks_like_risk(response.text):
            return ""
        parts = []
        for title, _, block in _heading_blocks(response.text)[:max_results]:
            body = _plain(block)
            if title and body.startswith(title):
                body = body[len(title):].strip()
            if title:
                parts.append(f"{title}：{body[:220]}")
        return "；".join(parts)[:max_chars]
    except requests.RequestException:
        return ""
    finally:
        session.close()


def robust_search_snippets(name: str, max_chars: int = 500) -> str:
    """跨搜索源尝试资源名及资源名+网盘，失败时返回空串。"""
    cleaned = (name or "").strip()
    if not cleaned or max_chars <= 0:
        return ""
    sources = (
        "https://www.baidu.com/s?wd={}&pn=0",
        "https://www.so.com/s?q={}",
        "https://www.sogou.com/web?query={}",
    )
    for keyword in (cleaned, f"{cleaned} 网盘"):
        for source in sources:
            result = _fetch_snippets(source.format(quote(keyword)), timeout=6, max_results=6, max_chars=max_chars)
            if result:
                return result
    return ""
