"""Real file regression tests for the keyword import protocol; no Office/network."""
import io
import sys
import tempfile
import unittest
from pathlib import Path
from xml.sax.saxutils import escape
from zipfile import ZIP_DEFLATED, ZipFile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import protocol


def workbook_bytes(rows, *, shared=False, sheet_name="词榜", part="worksheets/words.xml"):
    """Small OOXML fixture with explicit cell references and real relationships."""
    strings = []
    xml_rows = []
    for row_number, row in enumerate(rows, 1):
        cells = []
        for column, value in enumerate(row):
            if value is None:
                continue
            reference = f"{chr(65 + column)}{row_number}"
            if isinstance(value, (int, float)):
                cells.append(f'<c r="{reference}"><v>{value}</v></c>')
            elif shared:
                strings.append(str(value))
                cells.append(f'<c r="{reference}" t="s"><v>{len(strings) - 1}</v></c>')
            else:
                cells.append(f'<c r="{reference}" t="inlineStr"><is><t>{escape(str(value))}</t></is></c>')
        xml_rows.append(f'<row r="{row_number}">{"".join(cells)}</row>')
    output = io.BytesIO()
    with ZipFile(output, "w", ZIP_DEFLATED) as archive:
        archive.writestr("xl/workbook.xml", '<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" '
            'xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">'
            f'<sheets><sheet name="{sheet_name}" sheetId="1" r:id="rId7"/></sheets></workbook>')
        archive.writestr("xl/_rels/workbook.xml.rels", '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
            f'<Relationship Id="rId7" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="{part}"/>'
            '<Relationship Id="rId8" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/>'
            '</Relationships>')
        archive.writestr("xl/" + part.lstrip("/").removeprefix("xl/"), '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">'
            f'<sheetData>{"".join(xml_rows)}</sheetData></worksheet>')
        if shared:
            archive.writestr("xl/sharedStrings.xml", '<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">' +
                "".join(f'<si><t>{escape(value)}</t></si>' for value in strings) + '</sst>')
    return output.getvalue()


class KeywordTableTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)

    def read(self, payload, suffix=".xlsx"):
        path = Path(self.directory.name) / ("keywords" + suffix)
        path.write_bytes(payload)
        return protocol.read_keyword_file(path)

    def test_downloaded_ranking_headers_import_only_words(self):
        for header in ("热搜索词", "高搜索词", "高消费词", "高转化词"):
            for shared in (False, True):
                with self.subTest(header=header, shared=shared):
                    result = self.read(workbook_bytes([[header, "指数", "推广业务"],
                        [" taptap ", 123456, "盒子平台"], ["4399游戏盒", 333, "盒子平台"],
                        ["taptap", 222, "盒子平台"], [None, 111, "游戏"]], shared=shared))
                    self.assertEqual(["taptap", "4399游戏盒"], result["items"])
                    self.assertEqual(4, result["lines"])
                    self.assertEqual("xlsx", result["encoding"])
                    self.assertIn(header, result["source"])

    def test_header_after_title_and_keyword_column_not_first(self):
        result = self.read(workbook_bytes([["九月榜单"], [], ["排名", "关键词", "指数"],
            [1, "三体", 25], [2, None, 2], [3, "雪中悍刀行", 7]], shared=True))
        self.assertEqual(["三体", "雪中悍刀行"], result["items"])
        self.assertEqual(3, result["lines"])

    def test_headerless_single_sparse_column_keeps_first_word(self):
        result = self.read(workbook_bytes([[None, "三体"], [None, "流浪地球"]]))
        self.assertEqual(["三体", "流浪地球"], result["items"])

    def test_header_named_word_inside_headerless_list_does_not_drop_prefix(self):
        result = self.read(workbook_bytes([["三体"], ["关键词"], ["流浪地球"]]))
        self.assertEqual(["三体", "关键词", "流浪地球"], result["items"])

    def test_ambiguous_multi_column_table_does_not_import_numbers(self):
        with self.assertRaisesRegex(protocol.ProtocolError, "关键词列"):
            self.read(workbook_bytes([["排名", "未知字段"], [1, "三体"]]))

    def test_duplicate_keyword_headers_require_clarification(self):
        with self.assertRaisesRegex(protocol.ProtocolError, "多个.*关键词列"):
            self.read(workbook_bytes([["关键词", "搜索词"], ["三体", "流浪地球"]]))

    def test_empty_workbook_returns_empty_result(self):
        self.assertEqual([], self.read(workbook_bytes([[], []]))["items"])

    def test_uppercase_extension_and_absolute_worksheet_relationship(self):
        result = self.read(workbook_bytes([["关键字"], ["三体"]], part="/xl/worksheets/words.xml"), ".XLSX")
        self.assertEqual(["三体"], result["items"])

    def test_csv_quoted_fields_utf8_bom_and_gb18030(self):
        for encoding in ("utf-8-sig", "gb18030"):
            result = self.read('排名,搜索词,指数\r\n1,"甲,乙",123\r\n2,三体,456\r\n'.encode(encoding), ".csv")
            self.assertEqual(["甲,乙", "三体"], result["items"])
            self.assertEqual(2, result["lines"])

    def test_tsv_skips_header_and_blank_values(self):
        result = self.read("序号\t关键词\t热度\n1\t三体\t99\n2\t\t88\n3\t流浪地球\t77".encode(), ".tsv")
        self.assertEqual(["三体", "流浪地球"], result["items"])

    def test_multiline_cell_is_one_keyword_and_keeps_commas(self):
        result = self.read('关键词,指数\n"甲,乙\n合集",2\n'.encode(), ".csv")
        self.assertEqual(["甲,乙 合集"], result["items"])

    def test_rich_text_keyword_preserves_all_runs(self):
        payload = workbook_bytes([["关键词"], ["PLACEHOLDER"]])
        output = io.BytesIO()
        with ZipFile(io.BytesIO(payload)) as source, ZipFile(output, "w") as destination:
            for name in source.namelist():
                content = source.read(name)
                if name.endswith("words.xml"):
                    content = content.replace(b"<t>PLACEHOLDER</t>", '<r><t>流浪</t></r><r><t>地球</t></r>'.encode())
                destination.writestr(name, content)
        self.assertEqual(["流浪地球"], self.read(output.getvalue())["items"])

    def test_large_expansion_is_rejected_before_xml_parsing(self):
        from unittest.mock import patch
        with patch("keyword_files.MAX_UNCOMPRESSED_BYTES", 1024):
            with self.assertRaisesRegex(protocol.ProtocolError, "解压内容过大"):
                self.read(workbook_bytes([["关键词"], ["甲" * 5000]]))

    def test_text_utf16_and_existing_utf8_import(self):
        for encoding in ("utf-16", "utf-8"):
            result = self.read("三体\n流浪地球\n三体\n".encode(encoding), ".txt")
            self.assertEqual(["三体", "流浪地球"], result["items"])

    def test_damaged_workbook_is_not_reported_as_text_encoding_error(self):
        with self.assertRaisesRegex(protocol.ProtocolError, "Excel.*损坏|损坏.*Excel"):
            self.read(b"not a workbook")

    def test_legacy_xls_has_specific_format_error(self):
        with self.assertRaisesRegex(protocol.ProtocolError, "XLS.*XLSX"):
            self.read(b"\xd0\xcf\x11\xe0" + b"\0" * 20, ".xls")

    def test_keyword_limit_is_reported_after_deduplication(self):
        rows = [["关键词"]] + [[f"词{i}"] for i in range(protocol.MAX_KEYWORDS + 3)]
        result = self.read(workbook_bytes(rows))
        self.assertEqual(protocol.MAX_KEYWORDS, len(result["items"]))
        self.assertTrue(result["truncated"])
        self.assertEqual(protocol.MAX_KEYWORDS + 3, result["lines"])

    def test_unrelated_zip_is_rejected(self):
        output = io.BytesIO()
        with ZipFile(output, "w") as archive:
            archive.writestr("other.txt", "hello")
        with self.assertRaisesRegex(protocol.ProtocolError, "Excel"):
            self.read(output.getvalue())


if __name__ == "__main__":
    unittest.main()
