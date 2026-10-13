"""百度知道搜索页与详情页解析。全部为纯函数，不发网络请求。"""

from __future__ import annotations

from datetime import datetime
from html.parser import HTMLParser
import html as html_lib
import re
from urllib.parse import urlsplit


_SEARCH_ITEM = re.compile(
    r'<a[^>]+href="([^"]*zhidao\.baidu\.com/question/(\d+)\.html\?fr=(?:iks|search)[^"]*)"[^>]*>(.*?)</a>',
    re.I | re.S,
)
_DATE = re.compile(r"(20\d{2}-\d{2}-\d{2})")
_PAN_LINK = re.compile(
    r"https?://pan\.baidu\.com/s/[0-9A-Za-z_-]+(?:\?[^\s<>\"'，。；、）】｝」》\u3000]*)?",
    re.I,
)
_PAN_HREF = re.compile(
    r'''<a\b[^>]*\bhref\s*=\s*(["'])(https?://pan\.baidu\.com/s/[0-9A-Za-z_-]+.*?)\1''',
    re.I | re.S | re.ASCII,
)
_URL_PROSE_START = re.compile(r"[\u3400-\u9fff，。；：！？、（）【】｛｝「」『』《》\u3000]")
_PWD_TEXT = re.compile(
    r"(?:提取码|提取密码|访问码|密码)\s*[:：]?\s*([0-9A-Za-z]{4})(?=$|[^\w-]|[\u3400-\u9fff])"
)
_PWD_URL = re.compile(r"(?:^|[?&])pwd=([0-9A-Za-z]{4})(?:&|$)", re.I | re.ASCII)
_BOOK = re.compile(r"[《]([^《》]{2,40})[》]")
_FILENAME = re.compile(
    r"([\u4e00-\u9fffA-Za-z0-9_\-·：:＆&（）()]{2,40})\.(?:txt|epub|mobi|pdf|docx?|xlsx?|pptx?|zip|rar|7z|mp4|mp3|flac|apk|exe|iso)(?![A-Za-z0-9_])",
    re.I,
)
# These filters are recovered from v1.0.4, including their ordering. In
# particular, quoted names use lighter validation than unquoted candidates.
_TITLE_PREFIX = re.compile(r"^(?:求|求一个|跪求|请问|哪里有|谁有|想要|找|有偿求)[:：]?\s*")
_TYPE_PREFIX = re.compile(r"^(?:小说|电影|电视剧|动漫|动画|番剧|漫画|短剧|游戏|单机游戏|手游|软件|电脑软件|教程|课程|音乐|写真|综艺|纪录片)[:：]?\s*")
_NAME_NOISE = re.compile(
    r"(完整版|全集|全本|全套|高清|无删减|百度网盘|网盘|资源|下载|链接|提取码|txt|epub|mobi|pdf|zip|rar|mp4|mp3|求|哪里|谁有|跪求|请问)", re.I,
)
_GENERIC = re.compile(r"^(该|这个|这部|这款|这种|那个|那部|一种|一个|此|该游戏|这个游戏|该资源|这个资源)")
_BAD_GENERIC_WORDS = (
    "全文", "免费", "最新", "热门", "完整", "作者", "简介", "分享", "小说", "书网", "网盘", "资源",
    "阅读", "在线", "下载", "全集", "系列", "百度", "书斋", "另类", "亚洲", "番外", "附带",
)
_NAME_STOPWORDS = ("一下", "书合集", "起点", "合集", "全本", "全集", "小说", "资源", "网盘", "链接", "下载")
_EMPTY_RESULT_MARKERS = ("没有找到", "找不到", "没有相关", "未找到", "换个关键词", "换个词试试")
_DETAIL_DEAD_MARKERS = ("该问题可能已经失效", "知道宝贝找不到问题了")
_SPAM_LETTER = re.compile(r"(?<=[\u4e00-\u9fff])([A-Za-z])(?=[\u4e00-\u9fff])")
_NEXT_1 = re.compile(r'href="([^"]+)"[^>]*class="[^"]*pager-next', re.I)
_NEXT_2 = re.compile(r'class="[^"]*pager-next[^>]*href="([^"]+)"', re.I)
_NEXT_PN = re.compile(r"[?&]pn=(\d+)")
_TAG = re.compile(r"<[^>]+>")
_SCRIPT = re.compile(r"<script[\s\S]*?</script>|<style[\s\S]*?</style>", re.I)
_DETAIL_CONTAINER = re.compile(
    r'<(?:div|pre|p)[^>]*class="[^"]*(?:best-text|answer-text|answer-content|content-detail)[^"]*"[^>]*>([\s\S]*?)</(?:div|pre|p)>',
    re.I,
)
_LEGACY_DETAIL_PATTERNS = (
    r'<div[^>]*class="line content"[^>]*>([\s\S]*?)iknow-qb_home_icons',
    r'<div[^>]*class="line content"[^>]*>([\s\S]*?)</div>\s*</div>',
    r'<div[^>]*id="answer-content-[^"]*"[^>]*>([\s\S]*?)</div>\s*</div>',
    r'<div[^>]*class="[^"]*answer-text[^"]*"[^>]*>([\s\S]*?)</div>\s*</div>',
    r'<div[^>]*class="[^"]*best-text[^"]*"[^>]*>([\s\S]*?)</div>',
    r'class="[^"]*answer[^"]*content[^"]*"[^>]*>([\s\S]*?)</div>',
)


def _plain(fragment: str) -> str:
    fragment = _SCRIPT.sub(" ", fragment)
    fragment = _TAG.sub(" ", fragment)
    return re.sub(r"\s+", " ", html_lib.unescape(fragment)).strip()


class _ResultBoundaries(HTMLParser):
    """记录已闭合的搜索结果容器，防止最后一条摘要吞入页脚/推荐内容。"""

    def __init__(self, html: str) -> None:
        super().__init__()
        self._line_starts = [0] + [match.end() for match in re.finditer("\n", html)]
        self._stack: list[tuple[str, int, bool]] = []
        self.blocks: list[tuple[int, int]] = []
        self.feed(html)

    def _offset(self) -> int:
        line, column = self.getpos()
        return self._line_starts[line - 1] + column

    def handle_starttag(self, tag, attrs):
        if tag not in ("dl", "div", "li", "article"):
            return
        classes = (dict(attrs).get("class") or "").split()
        is_result = tag == "dl" or bool(set(classes) & {"result", "result-op", "c-container"})
        self._stack.append((tag, self._offset(), is_result))

    def handle_startendtag(self, tag, attrs):
        pass

    def handle_endtag(self, tag):
        for index in range(len(self._stack) - 1, -1, -1):
            if self._stack[index][0] == tag:
                closed = self._stack[index:]
                del self._stack[index:]
                for _, start, is_result in closed:
                    if is_result:
                        self.blocks.append((start, self._offset()))
                break


def parse_search_page(html: str) -> tuple[list[dict], int | None]:
    """抽取搜索结果和下一页 pn。"""
    items: list[dict] = []
    matches = list(_SEARCH_ITEM.finditer(html))
    blocks = _ResultBoundaries(html).blocks
    for index, match in enumerate(matches):
        end = matches[index + 1].start() if index + 1 < len(matches) else len(html)
        end = min([end] + [stop for start, stop in blocks if start <= match.start() < stop])
        block = html[match.start():end]
        title = _plain(match.group(3))
        if not title:
            continue
        # 标题中的版本/录播日期不是发布日期，也不要从属性或脚本中拾取日期。
        found_date = _DATE.search(_plain(html[match.end():end]))
        items.append({
            "url": html_lib.unescape(match.group(1)),
            "qid": match.group(2),
            "title": title,
            "text": _plain(block),
            "date": found_date.group(1) if found_date else "",
        })
    pns = [int(value) for value in _NEXT_PN.findall(html)]
    inferred_pn = max(pns) + 10 if pns and max(pns) < 900 else None
    next_link = _NEXT_1.search(html) or _NEXT_2.search(html)
    next_pn = _NEXT_PN.search(html_lib.unescape(next_link.group(1))) if next_link else None
    return items, int(next_pn.group(1)) if next_pn else inferred_pn


def classify_search_html(html: str, items: list | None = None) -> str:
    """区分正常有结果、正常空结果和结构异常/风控。"""
    if items:
        return "normal"
    text = html or ""
    if len(text) < 80000 and any(marker in text for marker in _EMPTY_RESULT_MARKERS):
        return "empty"
    return "unknown"


def parse_detail_page(html: str) -> tuple[str, str]:
    """宽松兼容 API，返回 `(正文, 状态)`；采集器使用 parse_detail_record。"""
    if not html:
        return "", "empty"
    if any(marker in html for marker in ("该问题已被删除", "问题已被删除", "此问题已删除", "问题不存在")):
        return "", "dead"
    containers = [_plain(match.group(1)) for match in _DETAIL_CONTAINER.finditer(html)]
    content = " ".join(part for part in containers if part).strip()
    if content:
        return content, "ok"
    fallback = _plain(html)
    return (fallback, "fallback") if fallback else ("", "empty")


def parse_detail_record(html: str) -> tuple[str, str, str]:
    """按 v1.0.4 返回 `(实际问题标题, 正文, ok/dead/fallback/empty)`。

    短于 2000 字符的响应先判空；六种容器按原版顺序取首个非空正文。
    通用 parse_detail_page 保留原有小片段解析能力，不受这个采集门槛影响。
    """
    if not html or len(html) < 2000:
        return "", "", "empty"
    title_match = re.search(r"<title>([\s\S]*?)_百度知道</title>", html)
    title = _plain(title_match.group(1)) if title_match else ""
    if any(marker in html for marker in _DETAIL_DEAD_MARKERS):
        return title, "", "dead"
    content = ""
    state = "empty"
    for pattern in _LEGACY_DETAIL_PATTERNS:
        match = re.search(pattern, html)
        if match:
            content = _plain(match.group(1))
            if content:
                state = "ok"
                break
    if not content:
        state = "fallback"
        body = re.search(r"<body[\s\S]*?</body>", html, re.I)
        content = _plain(body.group(0) if body else html)
    for pattern in (r"本回答由[\s\S]*?推荐", "本回答被提问者采纳", "其他回答", "热心网友"):
        content = re.sub(pattern, "", content)
    content = re.sub(r"\s+", " ", content).strip()
    content = re.sub(r"^(收起|展开全部)\s*", "", content)
    return title, content, state


def normalize_pan_url(url: str) -> str:
    """清理链接末尾中文标点及 HTML 转义。"""
    cleaned = html_lib.unescape(url.strip().strip("【[]()（）\"'「」"))
    return cleaned.rstrip("，。；、）】｝」》\u3000\"' ")


def link_key(url: str) -> str:
    """按分享路径去重，提取码不同仍是同一条链接。"""
    parsed = urlsplit(normalize_pan_url(url))
    return f"{parsed.scheme.lower()}://{parsed.netloc.lower()}{parsed.path}"


def extract_pwd(text: str, link: str) -> str:
    """优先取链接内的四位码，再取链接附近的文本码。"""
    # link 的正文/属性边界由调用方确定，不在这里截断完整查询参数。
    query_match = _PWD_URL.search(urlsplit(link).query)
    if query_match:
        return query_match.group(1)
    pos = text.find(link)
    nearby = text[max(0, pos - 40):pos + len(link) + 120] if pos >= 0 else text
    code = _PWD_TEXT.search(nearby)
    return code.group(1) if code else ""


def _pan_candidates(text: str):
    """保留已修复的正文/HTML 属性边界，供通用与采集 API 共用。"""
    # 先按原 HTML 找属性引号，再解实体，避免把 &quot; 误当属性结束。
    hrefs = list(_PAN_HREF.finditer(text))
    candidates = [(match.start(2), match.group(2), True) for match in hrefs]
    for match in _PAN_LINK.finditer(text):
        if not any(href.start(2) <= match.start() < href.end(2) for href in hrefs):
            candidates.append((match.start(), match.group(0), False))
    for _, raw_link, is_href in sorted(candidates):
        # 裸正文里的中文是 URL 后的说明；href 有引号边界，完整保留其值。
        raw_link = html_lib.unescape(raw_link)
        yield raw_link if is_href else normalize_pan_url(_URL_PROSE_START.split(raw_link, maxsplit=1)[0])


def extract_pan_entries(text: str) -> list[dict]:
    """通用兼容 API：允许短分享 ID，并把正文提取码补到链接。"""
    source = html_lib.unescape(text)
    entries: list[dict] = []
    seen: set[str] = set()
    for link in _pan_candidates(text):
        key = link_key(link)
        if key in seen:
            continue
        seen.add(key)
        pwd = extract_pwd(source, link)
        if pwd and not _PWD_URL.search(urlsplit(link).query):
            link += ("&" if "?" in link else "?") + f"pwd={pwd}"
        entries.append({"link": link, "pwd": pwd})
    return entries


def collect_link_key(url: str) -> str:
    """采集去重沿用原版分享 ID，同一分享的 http/https 和提取码视为相同。"""
    match = re.search(r"/s/(1?[0-9A-Za-z_-]+)", url)
    return match.group(1) if match else url


def _collect_pwd(text: str, link: str) -> str:
    query_match = _PWD_URL.search(urlsplit(link).query)
    if query_match:
        return query_match.group(1)
    pos = text.find(link)
    if pos < 0:
        return ""
    start = max(0, pos - 150)
    link_end = pos + max(1, len(link))
    best_pwd = ""
    best_distance = None
    for match in _PWD_TEXT.finditer(text[start:pos + 300]):
        code_pos = start + match.start()
        distance = pos - code_pos if code_pos < pos else max(0, code_pos - link_end)
        if best_distance is None or distance < best_distance:
            best_pwd, best_distance = match.group(1), distance
    return best_pwd


def extract_collect_entries(text: str) -> list[dict]:
    """原版采集准入/去重/就近提取码规则，正文码独立保存而不追加 URL。

    有意保留新版边界修复：href 的完整查询参数不会被截短，非法长 pwd
    不会变成四位码；裸正文中的中文说明不并入 URL。通用 API 不受影响。
    """
    source = html_lib.unescape(text or "")
    entries: list[dict] = []
    seen: set[str] = set()
    for link in _pan_candidates(text or ""):
        key = collect_link_key(link)
        if len(key) < 8 or key in seen:
            continue
        seen.add(key)
        entries.append({"link": link, "pwd": _collect_pwd(source, link)})
    return entries


def extract_pan_links(text: str) -> list[str]:
    return [entry["link"] for entry in extract_pan_entries(text)]


def strip_spam_letters(name: str) -> str:
    """同一字母在汉字间插入至少两次才删除，避免误伤真实名称。"""
    text = (name or "").strip()
    for _ in range(3):
        letters = [letter.lower() for letter in _SPAM_LETTER.findall(text)]
        repeated = {letter for letter in letters if letters.count(letter) >= 2}
        if not repeated:
            break
        text = _SPAM_LETTER.sub(lambda match: "" if match.group(1).lower() in repeated else match.group(1), text)
    return text or (name or "").strip()


def clean_title_for_name(title: str) -> str:
    """原版输出名兜底清洗；具体名称准入由 extract_resource_name 判断。"""
    name = re.sub(r"^(求|跪求|哪里有|谁有|请问|谁知道|急求|有偿求)\s*", "", strip_spam_letters(title))
    for suffix in (
        "完整网盘链接下载", "完整网盘链接获取", "完整网盘资源链接", "百度网盘链接获取",
        "百度网盘链接下载", "百度网盘资源链接", "完整版网盘链接", "网盘链接下载",
        "网盘资源链接", "网盘资源", "网盘链接", "完整版", "资源下载", "下载",
    ):
        if name.endswith(suffix):
            name = name[:-len(suffix)]
            break
    return re.sub(r"[《》\[\]【】()（）]", "", name).strip()


def _clean_candidate(name: str, quoted: bool = False) -> str:
    candidate = re.sub(r"\s+", " ", strip_spam_letters(name)).strip(" \t.-_、,，。:：")
    candidate = re.sub(r'''^[《》\"'“”]+|[《》\"'“”]+$''', "", candidate)
    if not 2 <= len(candidate) <= 40 or not re.search(r"[\u4e00-\u9fffA-Za-z]", candidate):
        return ""
    if candidate in _NAME_STOPWORDS or _NAME_NOISE.fullmatch(candidate):
        return ""
    residue = candidate
    for word in _BAD_GENERIC_WORDS + _NAME_STOPWORDS:
        residue = residue.replace(word, "")
    if len(re.findall(r"[\u4e00-\u9fffA-Za-z]", residue)) < 2:
        return ""
    if quoted:
        return candidate
    if _GENERIC.match(candidate) or any(word in candidate for word in _BAD_GENERIC_WORDS):
        return ""
    if re.search(r"^(完整版|全集|全本|全套|高清|资源|网盘|下载|链接)$", candidate):
        return ""
    return candidate


def extract_resource_name(title: str) -> str:
    title = (title or "").strip()
    book = _BOOK.search(title)
    if book:
        return _clean_candidate(re.sub(r"\s+", " ", book.group(1)).strip(), quoted=True)
    filename = _FILENAME.search(title)
    if filename:
        name = re.sub(r"\s+", " ", filename.group(1)).strip(" .-_")
        return _clean_candidate(_TYPE_PREFIX.sub("", _TITLE_PREFIX.sub("", name)))
    candidate = _TYPE_PREFIX.sub("", _TITLE_PREFIX.sub("", title))
    if candidate:
        noise = _NAME_NOISE.search(candidate)
        if noise:
            candidate = candidate[:noise.start()]
        candidate = re.sub(r"\s+by\s+[\u4e00-\u9fffA-Za-z0-9_·]{1,20}$", "", candidate, flags=re.I)
        return _clean_candidate(candidate)
    return ""


def has_concrete_name(title: str) -> bool:
    return bool(extract_resource_name(title))


def guess_resource_name(question_title: str, text: str, link: str) -> str:
    """原版先取链接前 400/后 100 字符内最后一本书名，再清洗问题标题。"""
    if text and link:
        pos = text.find(link)
        if pos < 0:
            # Raw candidates do not append a body pwd, so their positions remain
            # valid when the public entry API has augmented the requested link.
            for candidate in _pan_candidates(text):
                if collect_link_key(candidate) == collect_link_key(link):
                    pos = text.find(candidate)
                    break
        window = text[max(0, pos - 400):pos + 100] if pos >= 0 else text[:500]
        books = re.findall(r"《([^》]{1,60})》", window)
        if books:
            return books[-1].strip()
    return clean_title_for_name(question_title) or link


def within_range(item_date: str, days: int) -> bool:
    """已知日期按天数过滤；与旧版一致，日期缺失/异常不作为丢弃依据。"""
    if not days or not item_date:
        return True
    try:
        delta = (datetime.now() - datetime.strptime(item_date, "%Y-%m-%d")).days
        return delta <= int(days)
    except ValueError:
        return True
