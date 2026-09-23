"""百度知道搜索页与详情页解析。全部为纯函数，不发网络请求。"""

from __future__ import annotations

from datetime import date
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
_PWD_TEXT = re.compile(r"(?:提取码|提取密码|访问码|密码)\s*[:：]?\s*([0-9A-Za-z]{4})\b", re.I)
_PWD_URL = re.compile(r"(?:^|[?&])pwd=([0-9A-Za-z]{4})(?:&|$)", re.I)
_BOOK = re.compile(r"[《]([^》]{2,40})[》]")
_FILENAME = re.compile(
    r"([\u4e00-\u9fffA-Za-z0-9_\-《》:：&（）()]{2,40})\.(?:txt|epub|mobi|pdf|docx?|xlsx?|pptx?|zip|rar|7z|mp4|mp3|flac|apk|exe|iso)(?![A-Za-z0-9_])",
    re.I,
)
_TITLE_PREFIX = re.compile(r"^(?:求|有一个|谁有|哪里|哪里有|谁能|我要|找|有没有)[:：]?\s*")
_TYPE_PREFIX = re.compile(r"^(?:小说|电影|电视剧|动漫|动画|番剧|综艺|短剧|游戏|单机游戏|软件|资料|学习资料|教程|课程|素材|写真|资源|纪录片)[:：]?\s*")
_GENERIC = re.compile(r"^(这个|那个|外部|这部|这本|那部|一本|一部|它|这游戏|这个游戏|这资源|这个资源)")
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


def _plain(fragment: str) -> str:
    fragment = _SCRIPT.sub(" ", fragment)
    fragment = _TAG.sub(" ", fragment)
    return re.sub(r"\s+", " ", html_lib.unescape(fragment)).strip()


def parse_search_page(html: str) -> tuple[list[dict], int | None]:
    """抽取搜索结果和下一页 pn。"""
    items: list[dict] = []
    for match in _SEARCH_ITEM.finditer(html):
        nearby = html[match.end():match.end() + 350]
        found_date = _DATE.search(nearby)
        items.append({
            "url": html_lib.unescape(match.group(1)),
            "qid": match.group(2),
            "title": _plain(match.group(3)),
            "date": found_date.group(1) if found_date else "",
        })
    next_link = _NEXT_1.search(html) or _NEXT_2.search(html)
    next_pn = _NEXT_PN.search(html_lib.unescape(next_link.group(1))) if next_link else None
    return items, int(next_pn.group(1)) if next_pn else None


def classify_search_html(html: str, items: list) -> str:
    """区分正常有结果、正常空结果和结构异常/风控。"""
    if items:
        return "normal"
    if any(marker in html for marker in ("没有找到相关结果", "未找到相关结果", "没有找到相关内容", "暂无相关结果")):
        return "empty"
    return "unknown"


def parse_detail_page(html: str) -> tuple[str, str]:
    """返回 `(正文, ok/dead/fallback/empty)`。"""
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
    normalized = normalize_pan_url(link)
    query_match = _PWD_URL.search(urlsplit(normalized).query)
    if query_match:
        return query_match.group(1)
    pos = text.find(link)
    nearby = text[max(0, pos - 40):pos + len(link) + 120] if pos >= 0 else text
    code = _PWD_TEXT.search(nearby)
    return code.group(1) if code else ""


def extract_pan_entries(text: str) -> list[dict]:
    """从正文抽取网盘链接与四位提取码。"""
    source = html_lib.unescape(text)
    entries: list[dict] = []
    seen: set[str] = set()
    for match in _PAN_LINK.finditer(source):
        link = normalize_pan_url(match.group(0))
        key = link_key(link)
        if key in seen:
            continue
        seen.add(key)
        pwd = extract_pwd(source, link)
        if pwd and not _PWD_URL.search(urlsplit(link).query):
            link += ("&" if "?" in link else "?") + f"pwd={pwd}"
        entries.append({"link": link, "pwd": pwd})
    return entries


def extract_pan_links(text: str) -> list[str]:
    return [entry["link"] for entry in extract_pan_entries(text)]


def strip_spam_letters(name: str) -> str:
    """同一字母在汉字间插入至少两次才删除，避免误伤真实名称。"""
    letters = [match.group(1).casefold() for match in _SPAM_LETTER.finditer(name)]
    repeated = {letter for letter in letters if letters.count(letter) >= 2}
    cleaned = _SPAM_LETTER.sub(lambda match: "" if match.group(1).casefold() in repeated else match.group(0), name)
    if repeated:
        cleaned = re.sub(r"(?<=[\u4e00-\u9fff])[a-z]{4,}$", "", cleaned)
    return cleaned


def clean_title_for_name(title: str) -> str:
    """剥离问句前后缀、序号和文件扩展名，保留具体资源名。"""
    name = strip_spam_letters(_plain(title))
    name = re.sub(r"^\d+[_.\-\s]+", "", name)
    name = _TITLE_PREFIX.sub("", name)
    name = _TYPE_PREFIX.sub("", name)
    name = re.sub(r"(?:百度网盘|网盘)?(?:资源)?(?:完整)?链接(?:下载|获取)?[？?！!。\s]*$", "", name)
    name = re.sub(r"\.(?:xlsx|xls|doc|docx|pdf|txt|epub|mobi|mp4|mp3|zip|rar|7z|apk|exe|iso|ppt|pptx)$", "", name, flags=re.I)
    return name.strip(" \t:：-—_《》【】[]()（）？?")


def extract_resource_name(title: str) -> str:
    book = _BOOK.search(title)
    if book:
        return clean_title_for_name(book.group(1))
    filename = _FILENAME.search(title)
    if filename:
        return clean_title_for_name(filename.group(1))
    return clean_title_for_name(title)


def has_concrete_name(title: str) -> bool:
    name = extract_resource_name(title)
    return len(name) >= 2 and not bool(_GENERIC.match(name))


def guess_resource_name(question_title: str, text: str, link: str) -> str:
    """优先问题标题，再从详情正文补具体名称。"""
    title_name = extract_resource_name(question_title)
    if has_concrete_name(question_title):
        return title_name
    book = _BOOK.search(text)
    if book:
        return clean_title_for_name(book.group(1))
    filename = _FILENAME.search(text)
    if filename:
        return clean_title_for_name(filename.group(1))
    return title_name or f"资源{urlsplit(link).path.rsplit('/', 1)[-1]}"


def within_range(item_date: str, days: int) -> bool:
    """判断 ISO 日期是否在最近 days 天内。"""
    try:
        delta = (date.today() - date.fromisoformat(item_date)).days
    except ValueError:
        return False
    return 0 <= delta <= days
