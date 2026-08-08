using System.Text;

using ClosedXML.Excel;
using Microsoft.AspNetCore.Http;

namespace Modules.Assets.Features.Import;

/// <summary>One record from the uploaded file, numbered as a person editing that file would see it.</summary>
public sealed record CiImportRow(int LineNumber, IReadOnlyList<string> Cells);

public sealed record CiImportTable(IReadOnlyList<string> Headers, IReadOnlyList<CiImportRow> Rows);

public sealed record CiImportFileResult(CiImportTable? Table, string? Error)
{
    public bool IsSuccess => Table is not null;
}

/// <summary>
/// Turns an uploaded CSV or .xlsx into a header row plus numbered data rows. The header is line 1 and
/// the first record line 2, so every error this import reports names the line the operator has to fix.
/// Pure parsing — no database access and no knowledge of what a CI is.
/// </summary>
public static class CiImportFileReader
{
    internal const long MaximumFileSize = 5 * 1024 * 1024;
    internal const int MaximumRows = 5_000;
    internal const int MaximumColumns = 100;

    public static async Task<CiImportFileResult> ReadAsync(IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length <= 0)
        {
            return new(null, "The file is empty.");
        }

        if (file.Length > MaximumFileSize)
        {
            return new(null, $"The file must be {MaximumFileSize / 1024 / 1024} MB or smaller.");
        }

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension is not (".csv" or ".xlsx"))
        {
            return new(null, "Upload a .csv or .xlsx file.");
        }

        await using var content = new MemoryStream(checked((int)file.Length));
        await file.CopyToAsync(content, cancellationToken);
        content.Position = 0;

        List<(int Line, List<string> Cells)> records;
        try
        {
            records = extension == ".csv"
                ? ParseCsv(await new StreamReader(content, Encoding.UTF8).ReadToEndAsync(cancellationToken))
                : ParseWorkbook(content);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // An upload is untrusted input and the workbook reader raises whatever its own layers raise
            // (FileFormatException, InvalidDataException, XML errors) on a file that is not really a
            // workbook. None of that may reach the operator as a 500 — the answer is always the same
            // sentence, and the parser's internals stay inside the parser.
            return new(null, "The file could not be read. Check that it is a valid CSV or Excel workbook.");
        }

        return Build(records);
    }

    private static CiImportFileResult Build(List<(int Line, List<string> Cells)> records)
    {
        if (records.Count == 0)
        {
            return new(null, "The file has no rows.");
        }

        var headerCells = records[0].Cells.Select(cell => cell.Trim()).ToList();
        while (headerCells.Count > 0 && headerCells[^1].Length == 0)
        {
            headerCells.RemoveAt(headerCells.Count - 1);
        }

        if (headerCells.Count == 0)
        {
            return new(null, "The first row must be a header row naming each column.");
        }

        if (headerCells.Count > MaximumColumns)
        {
            return new(null, $"The file must have {MaximumColumns} columns or fewer.");
        }

        if (headerCells.Any(header => header.Length == 0))
        {
            return new(null, "Every column in the header row must be named.");
        }

        var duplicate = headerCells
            .GroupBy(header => header, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            return new(null, $"The header row names '{duplicate.Key}' more than once; column names must be unique.");
        }

        var rows = records.Skip(1).ToList();
        if (rows.Count == 0)
        {
            return new(null, "The file has a header row but no data rows.");
        }

        if (rows.Count > MaximumRows)
        {
            return new(null, $"The file must have {MaximumRows} data rows or fewer; this one has {rows.Count}.");
        }

        return new(
            new CiImportTable(
                headerCells,
                [.. rows.Select(record => new CiImportRow(record.Line, Fit(record.Cells, headerCells.Count)))]),
            null);
    }

    /// <summary>A short row is padded and a long one truncated, so a row is always indexable by header.</summary>
    private static IReadOnlyList<string> Fit(List<string> cells, int width) =>
        [.. Enumerable.Range(0, width).Select(index => index < cells.Count ? cells[index].Trim() : string.Empty)];

    /// <summary>
    /// RFC 4180: comma separated, double quotes around a value that contains a comma, quote or newline,
    /// and a doubled quote inside a quoted value. Physical lines are counted as they are consumed, so a
    /// value containing a newline does not shift the numbers reported for the rows after it.
    /// </summary>
    private static List<(int Line, List<string> Cells)> ParseCsv(string text)
    {
        var records = new List<(int, List<string>)>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var line = 1;
        var recordLine = 1;
        var inQuotes = false;

        void EndRecord()
        {
            cells.Add(cell.ToString());
            cell.Clear();
            // A blank line carries no record; skipping it here keeps trailing newlines from becoming rows.
            if (cells.Any(value => value.Length > 0))
            {
                records.Add((recordLine, cells));
            }

            cells = [];
        }

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (index == 0 && character == '\uFEFF')
            {
                continue;
            }

            if (inQuotes)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        cell.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    if (character == '\n')
                    {
                        line++;
                    }

                    cell.Append(character);
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    cells.Add(cell.ToString());
                    cell.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    EndRecord();
                    line++;
                    recordLine = line;
                    break;
                default:
                    cell.Append(character);
                    break;
            }
        }

        if (cell.Length > 0 || cells.Count > 0)
        {
            EndRecord();
        }

        return records;
    }

    private static List<(int Line, List<string> Cells)> ParseWorkbook(Stream content)
    {
        using var workbook = new XLWorkbook(content);
        var worksheet = workbook.Worksheets.FirstOrDefault()
            ?? throw new InvalidDataException("The workbook has no worksheets.");
        var used = worksheet.RangeUsed();
        if (used is null)
        {
            return [];
        }

        var firstColumn = used.FirstColumn().ColumnNumber();
        var lastColumn = used.LastColumn().ColumnNumber();
        var records = new List<(int, List<string>)>();
        foreach (var row in worksheet.RowsUsed())
        {
            var cells = new List<string>(lastColumn - firstColumn + 1);
            for (var column = firstColumn; column <= lastColumn; column++)
            {
                cells.Add(row.Cell(column).GetFormattedString());
            }

            if (cells.Any(value => !string.IsNullOrWhiteSpace(value)))
            {
                records.Add((row.RowNumber(), cells));
            }
        }

        return records;
    }
}
