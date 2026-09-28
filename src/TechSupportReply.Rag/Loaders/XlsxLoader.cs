using System.Collections.Generic;
using System.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace TechSupportReply.Rag.Loaders
{
    /// <summary>XLSX의 시트마다 첫 행을 머리글로 삼아 행 단위 섹션을 만든다.</summary>
    public sealed class XlsxLoader : IDocumentLoader
    {
        public IReadOnlyCollection<string> Extensions { get; } = new[] { ".xlsx" };

        public LoadedDocument Load(string path)
        {
            var doc = new LoadedDocument { Path = path, Kind = DocKind.Table, Title = System.IO.Path.GetFileNameWithoutExtension(path) };
            using (var xlsx = SpreadsheetDocument.Open(path, false))
            {
                var workbook = xlsx.WorkbookPart;
                if (workbook?.Workbook?.Sheets == null) return doc;
                var shared = workbook.SharedStringTablePart?.SharedStringTable;
                foreach (var sheet in workbook.Workbook.Sheets.Elements<Sheet>())
                {
                    if (!(workbook.GetPartById(sheet.Id) is WorksheetPart part)) continue;
                    var rows = ReadRows(part, shared);
                    doc.Sections.AddRange(TableSections.FromRows(rows, sheet.Name?.Value ?? "시트"));
                }
            }
            return doc;
        }

        private static List<string[]> ReadRows(WorksheetPart part, SharedStringTable shared)
        {
            var result = new List<string[]>();
            var data = part.Worksheet?.GetFirstChild<SheetData>();
            if (data == null) return result;
            uint? previous = null;
            foreach (var row in data.Elements<Row>())
            {
                uint index = row.RowIndex?.Value ?? (previous ?? 0) + 1;
                if (previous.HasValue && result.Count > 0)
                    for (uint gap = previous.Value + 1; gap < index; gap++) result.Add(new string[0]);
                previous = index;

                var cells = new List<string>();
                foreach (var cell in row.Elements<Cell>())
                {
                    int col = ColumnIndex(cell.CellReference?.Value);
                    if (col < 0) col = cells.Count;
                    while (cells.Count < col) cells.Add("");
                    cells.Add(CellText(cell, shared));
                }
                if (result.Count == 0 && cells.All(c => c.Length == 0)) continue;
                result.Add(cells.ToArray());
            }
            return result;
        }

        private static string CellText(Cell cell, SharedStringTable shared)
        {
            var type = cell.DataType?.Value;
            if (type == CellValues.SharedString)
            {
                if (shared != null && int.TryParse(cell.CellValue?.Text, out var i))
                    return shared.ElementAt(i).InnerText;
                return "";
            }
            if (type == CellValues.InlineString) return cell.InlineString?.InnerText ?? "";
            if (type == CellValues.Boolean) return cell.CellValue?.Text == "1" ? "TRUE" : "FALSE";
            return cell.CellValue?.Text ?? "";
        }

        private static int ColumnIndex(string reference)
        {
            if (string.IsNullOrEmpty(reference)) return -1;
            int col = 0, i = 0;
            for (; i < reference.Length && char.IsLetter(reference[i]); i++)
                col = col * 26 + (char.ToUpperInvariant(reference[i]) - 'A' + 1);
            return i == 0 ? -1 : col - 1;
        }
    }
}
