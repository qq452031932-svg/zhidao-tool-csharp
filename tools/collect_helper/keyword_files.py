"""Read keyword columns without Office or optional Python dependencies."""
from __future__ import annotations

import codecs
import csv
import io
import posixpath
import re
from pathlib import Path
from xml.etree import ElementTree as ET
from zipfile import BadZipFile, ZipFile

HEADERS = frozenset(("关键词", "关键字", "搜索词", "搜索关键词", "热搜索词", "高搜索词",
                     "高消费词", "高转化词", "热搜词", "资源名称", "资源名", "词条", "keyword", "keywords"))
MAX_XML_BYTES = 32 * 1024 * 1024
MAX_UNCOMPRESSED_BYTES = 64 * 1024 * 1024
MAX_ROWS = 100_000


class KeywordFileError(ValueError):
    pass


def _text(value):
    # A spreadsheet cell is one keyword, even if it contains a visual line break.
    return re.sub(r"[\r\n]+", " ", str(value or "")).strip()


def _result(values, lines, encoding, source, max_keywords):
    unique = dict.fromkeys(value for value in values if value)
    words = list(unique)
    return {"items": words[:max_keywords], "lines": lines, "encoding": encoding,
            "source": source, "total": len(words), "truncated": len(words) > max_keywords}


def _table(rows, encoding, source, max_keywords):
    nonempty = [(index, row) for index, row in enumerate(rows) if any(row.values())]
    if not nonempty:
        return _result([], 0, encoding, source, max_keywords)
    column = None
    start = 0
    header = ""
    for index, row in nonempty[:20]:
        if index != nonempty[0][0] and (
                sum(bool(value) for value in row.values()) <= 1 or
                any(sum(bool(value) for value in previous.values()) > 1
                    for previous_index, previous in nonempty if previous_index < index)):
            # A word named "关键词" inside a one-column list is data, not a header.
            # Only a multi-column header after single-cell title rows is unambiguous.
            continue
        matches = [key for key, value in row.items() if value.casefold() in HEADERS]
        if len(matches) > 1:
            raise KeywordFileError("表格有多个可能的关键词列，请仅保留需要导入的一列。")
        if matches:
            column, start = matches[0], index + 1
            header = row[column]
            break
    if column is None:
        columns = {key for _, row in nonempty for key, value in row.items() if value}
        if len(columns) != 1:
            raise KeywordFileError("无法确定关键词列，请将该列表头设为“关键词”，或使用单列表格。")
        column = next(iter(columns))
    data = rows[start:]
    label = header or f"第 {column + 1} 列"
    return _result((row.get(column, "") for row in data), len(data), encoding,
                   f"{source} · {label}", max_keywords)


def _decode(raw):
    for bom, encoding in ((codecs.BOM_UTF32_LE, "utf-32"), (codecs.BOM_UTF32_BE, "utf-32"),
                          (codecs.BOM_UTF16_LE, "utf-16"), (codecs.BOM_UTF16_BE, "utf-16")):
        if raw.startswith(bom):
            try:
                return raw.decode(encoding), encoding
            except UnicodeDecodeError as error:
                raise KeywordFileError("文本文件内容损坏，无法读取。") from error
    for encoding in ("utf-8-sig", "gb18030"):
        try:
            content = raw.decode(encoding)
            if "\0" in content:
                break
            return content, "utf-8" if encoding == "utf-8-sig" else encoding
        except UnicodeDecodeError:
            continue
    raise KeywordFileError("无法识别文本文件编码，请使用 UTF-8、UTF-16 或 GB18030。")


def _part(target):
    path = posixpath.normpath(target.lstrip("/") if target.startswith("/") else "xl/" + target)
    if not path.startswith("xl/") or "\\" in path or ":" in path:
        raise KeywordFileError("Excel 工作表路径无效。")
    return path


def _xml(archive, path):
    info = archive.getinfo(path)
    if info.file_size > MAX_XML_BYTES:
        raise KeywordFileError("Excel 内容过大，请拆分表格后导入。")
    raw = archive.read(path)
    # Reject DTDs before parsing, including UTF-16/32 XML declarations.
    if b"<!DOCTYPE" in raw.replace(b"\0", b"").upper():
        raise KeywordFileError("Excel XML 内容不受支持。")
    return ET.fromstring(raw)


def _cell_text(cell, strings):
    kind = cell.get("t")
    value = cell.find("{*}v")
    if kind == "inlineStr":
        return _text("".join(node.text or "" for node in cell.findall("{*}is//{*}t")))
    if kind == "s":
        index = int(value.text) if value is not None else -1
        if not 0 <= index < len(strings):
            raise KeywordFileError("Excel 共享文本索引损坏。")
        return _text(strings[index])
    if kind == "e":
        raise KeywordFileError("Excel 包含错误单元格，请修正后重试。")
    return _text(value.text if value is not None else "")


def _worksheet_rows(root, strings):
    rows = []
    for row in root.findall("{*}sheetData/{*}row"):
        if len(rows) >= MAX_ROWS:
            raise KeywordFileError("表格超过 100000 行，请拆分后导入。")
        values = {}
        next_column = 0
        for cell in row.findall("{*}c"):
            reference = cell.get("r")
            column = next_column
            if reference:
                match = re.fullmatch(r"([A-Z]+)[1-9][0-9]*", reference)
                if not match:
                    raise KeywordFileError("Excel 单元格位置损坏。")
                column = 0
                for letter in match[1]:
                    column = column * 26 + ord(letter) - 64
                column -= 1
            if column >= 16384 or column in values:
                raise KeywordFileError("Excel 列位置无效。")
            values[column] = _cell_text(cell, strings)
            next_column = column + 1
        rows.append(values)
    return rows


def _xlsx(raw, max_keywords):
    try:
        with ZipFile(io.BytesIO(raw)) as archive:
            entries = archive.infolist()
            if len(entries) > 2048 or sum(info.file_size for info in entries) > MAX_UNCOMPRESSED_BYTES:
                raise KeywordFileError("Excel 解压内容过大，请拆分表格后导入。")
            if len({info.filename for info in entries}) != len(entries):
                raise KeywordFileError("Excel 压缩包包含重复内容。")
            book = _xml(archive, "xl/workbook.xml")
            relations = _xml(archive, "xl/_rels/workbook.xml.rels")
            targets = {r.get("Id"): r for r in relations}
            string_part = next((r.get("Target") for r in relations
                                if (r.get("Type") or "").endswith("/sharedStrings")
                                and r.get("TargetMode") != "External"), "sharedStrings.xml")
            strings = []
            string_path = _part(string_part)
            if string_path in archive.namelist():
                strings = ["".join(node.text or "" for node in si.findall(".//{*}t"))
                           for si in _xml(archive, string_path).findall("{*}si")]
            for sheet in book.findall("{*}sheets/{*}sheet"):
                if sheet.get("state", "visible") != "visible":
                    continue
                relationship = next((v for k, v in sheet.attrib.items() if k.endswith("}id")), None)
                target = targets.get(relationship)
                if target is None or target.get("TargetMode") == "External":
                    raise KeywordFileError("Excel 工作表关系损坏。")
                if not (target.get("Type") or "").endswith("/worksheet"):
                    continue
                rows = _worksheet_rows(_xml(archive, _part(target.get("Target", ""))), strings)
                if any(any(row.values()) for row in rows):
                    return _table(rows, "xlsx", f"Excel：{sheet.get('name', '工作表')}", max_keywords)
            return _result([], 0, "xlsx", "Excel", max_keywords)
    except KeywordFileError:
        raise
    except (BadZipFile, ET.ParseError, KeyError, ValueError, RuntimeError, OSError) as error:
        raise KeywordFileError("Excel 文件损坏或不是有效的 XLSX 表格，请检查文件。") from error


def read_keyword_file(path, *, max_bytes, max_keywords):
    target = Path(path)
    if not target.is_file():
        raise KeywordFileError(f"找不到文件：{path}")
    if target.stat().st_size > max_bytes:
        raise KeywordFileError(f"文件超过 {max_bytes // (1024 * 1024)} MB")
    with target.open("rb") as source:
        raw = source.read(max_bytes + 1)
    if len(raw) > max_bytes:
        raise KeywordFileError(f"文件超过 {max_bytes // (1024 * 1024)} MB")
    extension = target.suffix.lower()
    if extension == ".xlsx":
        return _xlsx(raw, max_keywords)
    if extension == ".xls":
        raise KeywordFileError("暂不支持旧版 XLS，请在表格软件中另存为 XLSX 后直接导入。")
    if extension not in (".txt", ".csv", ".tsv"):
        raise KeywordFileError("请选择 XLSX、CSV、TSV 或 TXT 关键词文件。")
    content, encoding = _decode(raw)
    if extension == ".txt":
        lines = content.splitlines()
        return _result((line.strip() for line in lines), len(lines), encoding, "", max_keywords)
    try:
        rows = []
        for row in csv.reader(io.StringIO(content, newline=""), delimiter="\t" if extension == ".tsv" else ",", strict=True):
            if len(rows) >= MAX_ROWS:
                raise KeywordFileError("表格超过 100000 行，请拆分后导入。")
            rows.append({index: _text(value) for index, value in enumerate(row)})
        return _table(rows, encoding, extension[1:].upper(), max_keywords)
    except csv.Error as error:
        raise KeywordFileError("表格分隔符或引号格式有误，请检查 CSV/TSV 文件。") from error
