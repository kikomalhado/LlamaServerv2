using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
using LlamaServer.Models;

namespace LlamaServer.Services;

public sealed record ExtractedSection(string Text, int? PageNumber = null, string? Section = null);

public sealed class DocumentTextExtractor
{
    public async Task<IReadOnlyList<ExtractedSection>> ExtractAsync(
        DocumentFile document,
        CancellationToken cancellationToken)
    {
        return document.Extension switch
        {
            ".txt" or ".md" => [new ExtractedSection(await File.ReadAllTextAsync(document.StoredPath, Encoding.UTF8, cancellationToken))],
            ".json" => [new ExtractedSection(await ReadJsonAsync(document.StoredPath, cancellationToken))],
            ".pdf" => ExtractPdf(document.StoredPath),
            ".docx" => ExtractDocx(document.StoredPath),
            ".xlsx" => ExtractXlsx(document.StoredPath),
            _ => throw new InvalidOperationException($"Formato não suportado: {document.Extension}")
        };
    }

    private static async Task<string> ReadJsonAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return JsonSerializer.Serialize(json.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }

    private static IReadOnlyList<ExtractedSection> ExtractPdf(string path)
    {
        using var pdf = PdfDocument.Open(path);
        return pdf.GetPages()
            .Select(page => new ExtractedSection(page.Text, page.Number))
            .Where(section => !string.IsNullOrWhiteSpace(section.Text))
            .ToList();
    }

    private static IReadOnlyList<ExtractedSection> ExtractDocx(string path)
    {
        using var document = WordprocessingDocument.Open(path, false);
        var mainPart = document.MainDocumentPart
            ?? throw new InvalidOperationException("O DOCX não contém uma parte principal.");
        var body = mainPart.Document?.Body
            ?? throw new InvalidOperationException("O DOCX não contém um corpo de documento.");

        var text = string.Join(
            Environment.NewLine,
            body.Descendants<Paragraph>()
                .Select(paragraph => paragraph.InnerText)
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        return [new ExtractedSection(text)];
    }

    private static IReadOnlyList<ExtractedSection> ExtractXlsx(string path)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var workbook = document.WorkbookPart
            ?? throw new InvalidOperationException("O XLSX não contém um workbook.");
        var sharedStrings = workbook.SharedStringTablePart?.SharedStringTable;
        var sections = new List<ExtractedSection>();

        var sheets = workbook.Workbook?.Sheets
            ?? throw new InvalidOperationException("O XLSX não contém folhas.");
        foreach (var sheet in sheets.Elements<Sheet>())
        {
            var relationshipId = sheet.Id?.Value
                ?? throw new InvalidOperationException("A folha XLSX não tem relação válida.");
            var part = workbook.GetPartById(relationshipId) as WorksheetPart
                ?? throw new InvalidOperationException("A folha XLSX não é uma worksheet válida.");
            var worksheet = part.Worksheet
                ?? throw new InvalidOperationException("A folha XLSX não contém dados.");
            var values = worksheet.Descendants<Cell>()
                .Select(cell => GetCellValue(cell, sharedStrings))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            if (values.Count > 0)
            {
                sections.Add(new ExtractedSection(string.Join(" | ", values), Section: sheet.Name?.Value));
            }
        }

        return sections;
    }

    private static string GetCellValue(Cell cell, SharedStringTable? sharedStrings)
    {
        var value = cell.CellValue?.Text ?? cell.InnerText;
        if (cell.DataType?.Value == CellValues.SharedString
            && int.TryParse(value, out var index)
            && sharedStrings is not null)
        {
            return sharedStrings.Elements<SharedStringItem>().ElementAtOrDefault(index)?.InnerText ?? "";
        }

        return value;
    }
}
