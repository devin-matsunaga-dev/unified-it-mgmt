using System.Text;

using ClosedXML.Excel;
using Microsoft.AspNetCore.Http;
using Modules.Assets.Features.Import;

namespace Infrastructure.Tests;

public sealed class CiImportFileReaderTests
{
    [Fact]
    public async Task ReadAsync_Csv_ReadsHeadersAndNumbersRowsFromTwo()
    {
        var result = await ReadCsvAsync("Name,Asset tag\nlaptop-1,AT-1\nlaptop-2,AT-2\n");

        var table = Assert.IsType<CiImportTable>(result.Table);
        Assert.Equal(["Name", "Asset tag"], table.Headers);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(2, table.Rows[0].LineNumber);
        Assert.Equal(["laptop-1", "AT-1"], table.Rows[0].Cells);
        Assert.Equal(3, table.Rows[1].LineNumber);
    }

    [Fact]
    public async Task ReadAsync_QuotedValues_KeepsCommasQuotesAndNewlines()
    {
        var result = await ReadCsvAsync("Name,Description\n\"laptop, spare\",\"He said \"\"hi\"\"\nsecond line\"\nlaptop-2,plain\n");

        var table = Assert.IsType<CiImportTable>(result.Table);
        Assert.Equal("laptop, spare", table.Rows[0].Cells[0]);
        Assert.Equal("He said \"hi\"\nsecond line", table.Rows[0].Cells[1]);
        // The embedded newline is a physical line, so the row after it is still numbered honestly.
        Assert.Equal(4, table.Rows[1].LineNumber);
    }

    [Fact]
    public async Task ReadAsync_ShortAndLongRows_AreFittedToTheHeaderWidth()
    {
        var result = await ReadCsvAsync("Name,Asset tag,Serial\nonly-a-name\nall,AT-9,SN-9,extra\n");

        var table = Assert.IsType<CiImportTable>(result.Table);
        Assert.Equal(["only-a-name", "", ""], table.Rows[0].Cells);
        Assert.Equal(["all", "AT-9", "SN-9"], table.Rows[1].Cells);
    }

    [Fact]
    public async Task ReadAsync_BlankLines_AreNotRows()
    {
        var result = await ReadCsvAsync("Name\nlaptop-1\n\n\nlaptop-2\n");

        var table = Assert.IsType<CiImportTable>(result.Table);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(5, table.Rows[1].LineNumber);
    }

    [Fact]
    public async Task ReadAsync_ByteOrderMark_IsNotPartOfTheFirstHeader()
    {
        var result = await ReadCsvAsync("﻿Name,Asset tag\nlaptop-1,AT-1\n");

        Assert.Equal("Name", Assert.IsType<CiImportTable>(result.Table).Headers[0]);
    }

    [Fact]
    public async Task ReadAsync_Xlsx_ReadsTheFirstWorksheet()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Assets");
        sheet.Cell(1, 1).Value = "Name";
        sheet.Cell(1, 2).Value = "Serial";
        sheet.Cell(2, 1).Value = "vm-payroll";
        sheet.Cell(2, 2).Value = "SN-100";
        using var content = new MemoryStream();
        workbook.SaveAs(content);

        var result = await CiImportFileReader.ReadAsync(FormFileOf(content.ToArray(), "assets.xlsx"), default);

        var table = Assert.IsType<CiImportTable>(result.Table);
        Assert.Equal(["Name", "Serial"], table.Headers);
        Assert.Equal(["vm-payroll", "SN-100"], Assert.Single(table.Rows).Cells);
    }

    [Fact]
    public async Task ReadAsync_DuplicateHeaders_IsRejected()
    {
        var result = await ReadCsvAsync("Name,name\na,b\n");

        Assert.Null(result.Table);
        Assert.Contains("more than once", result.Error);
    }

    [Fact]
    public async Task ReadAsync_HeaderRowWithAGapInIt_IsRejected()
    {
        var result = await ReadCsvAsync("Name,,Serial\na,b,c\n");

        Assert.Null(result.Table);
        Assert.Contains("must be named", result.Error);
    }

    [Fact]
    public async Task ReadAsync_HeaderRowOnly_IsRejected()
    {
        var result = await ReadCsvAsync("Name,Asset tag\n");

        Assert.Null(result.Table);
        Assert.Contains("no data rows", result.Error);
    }

    [Fact]
    public async Task ReadAsync_UnsupportedExtension_IsRejected()
    {
        var result = await CiImportFileReader.ReadAsync(
            FormFileOf(Encoding.UTF8.GetBytes("Name\na\n"), "assets.txt"), default);

        Assert.Null(result.Table);
        Assert.Equal("Upload a .csv or .xlsx file.", result.Error);
    }

    [Fact]
    public async Task ReadAsync_FileThatIsNotAWorkbook_IsRejectedWithoutLeakingTheParserError()
    {
        var result = await CiImportFileReader.ReadAsync(
            FormFileOf(Encoding.UTF8.GetBytes("this is not a workbook"), "assets.xlsx"), default);

        Assert.Null(result.Table);
        Assert.Equal("The file could not be read. Check that it is a valid CSV or Excel workbook.", result.Error);
    }

    [Fact]
    public async Task ReadAsync_EmptyFile_IsRejected()
    {
        var result = await CiImportFileReader.ReadAsync(FormFileOf([], "assets.csv"), default);

        Assert.Null(result.Table);
        Assert.Equal("The file is empty.", result.Error);
    }

    private static Task<CiImportFileResult> ReadCsvAsync(string text) =>
        CiImportFileReader.ReadAsync(FormFileOf(Encoding.UTF8.GetBytes(text), "assets.csv"), default);

    private static FormFile FormFileOf(byte[] content, string fileName) =>
        new(new MemoryStream(content), 0, content.Length, "file", fileName);
}
