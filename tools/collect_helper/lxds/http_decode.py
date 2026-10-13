"""Decode Zhidao HTTP bytes before parsing titles, links or page markers."""

from __future__ import annotations

import codecs
from email.message import Message
from html.parser import HTMLParser

import requests


def _content_type_charset(content_type: str) -> str | None:
    header = Message()
    header["Content-Type"] = content_type
    return header.get_content_charset()


class _HtmlCharset(HTMLParser):
    def __init__(self) -> None:
        super().__init__()
        self.encodings: list[str] = []

    def handle_starttag(self, tag, attrs):
        if tag != "meta":
            return
        attributes = dict(attrs)
        encoding = attributes.get("charset")
        if not encoding and (attributes.get("http-equiv") or "").lower() == "content-type":
            encoding = _content_type_charset(attributes.get("content") or "")
        if encoding:
            self.encodings.append(encoding.strip())


def decode_html_response(response: requests.Response) -> str:
    """Prefer BOM/declared charsets, then UTF-8 and legacy Chinese bytes.

    Requests assigns ISO-8859-1 to text/html without a charset. That default
    is not a server declaration and must not override HTML metadata or UTF-8.
    All candidates decode the original bytes strictly; no title text is repaired.
    """
    content = response.content
    if not content:
        return ""

    encodings = []
    for prefix, encoding in (
        (codecs.BOM_UTF32_LE, "utf-32"),
        (codecs.BOM_UTF32_BE, "utf-32"),
        (codecs.BOM_UTF8, "utf-8-sig"),
        (codecs.BOM_UTF16_LE, "utf-16"),
        (codecs.BOM_UTF16_BE, "utf-16"),
    ):
        if content.startswith(prefix):
            encodings.append(encoding)
            break

    declared = _content_type_charset(response.headers.get("Content-Type", ""))
    if declared:
        encodings.append(declared)

    parser = _HtmlCharset()
    # Latin-1 here only preserves ASCII markup while scanning raw metadata.
    parser.feed(content[:8192].decode("latin1"))
    encodings.extend(parser.encodings)
    encodings.extend(("utf-8", "gb18030"))
    for encoding in dict.fromkeys(encodings):
        try:
            return content.decode(encoding)
        except (LookupError, UnicodeError):
            continue

    # Retain Requests' tolerant behavior for damaged/unsupported responses.
    return response.text
