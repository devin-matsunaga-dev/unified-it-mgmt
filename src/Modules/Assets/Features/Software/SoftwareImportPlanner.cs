using System.Globalization;

using Modules.Assets.Features.Import;

namespace Modules.Assets.Features.Software;

/// <summary>One inventory line, read out of the file and not yet resolved against anything.</summary>
public sealed record SoftwareImportParsedRow(
    int LineNumber,
    string? AssetTag,
    string? SerialNumber,
    string? Hostname,
    string SoftwareName,
    string? Publisher,
    string? Version,
    DateOnly? InstalledOn,
    IReadOnlyList<string> Errors)
{
    /// <summary>What the row said the machine was, for the report — whichever column it used.</summary>
    public string? Machine => AssetTag ?? SerialNumber ?? Hostname;
}

/// <summary>A whole file read into rows, or one sentence saying why it could not be.</summary>
public sealed record SoftwareImportPlan(IReadOnlyList<SoftwareImportParsedRow> Rows, string? Error)
{
    public bool IsSuccess => Error is null;
}

/// <summary>
/// Turns an inventory file into rows. Pure: no database, no knowledge of which CIs exist.
/// <para>
/// Unlike the WP-2.5 CI importer this has no column-mapping wizard. An inventory export has a shape —
/// a machine, a piece of software, a version — and the columns are recognised by name from a fixed set
/// of aliases, so the operator uploads the file their agent or RMM produced and nothing else. A file
/// whose columns cannot be recognised is a 400 that names the headers this reads.
/// </para>
/// </summary>
public static class SoftwareImportPlanner
{
    private static readonly string[] AssetTagHeaders = ["asset tag", "assettag", "tag"];
    private static readonly string[] SerialHeaders = ["serial number", "serialnumber", "serial"];
    private static readonly string[] HostnameHeaders = ["hostname", "host name", "computer", "computer name", "machine"];
    private static readonly string[] SoftwareHeaders = ["software", "software name", "display name", "product", "application"];
    private static readonly string[] PublisherHeaders = ["publisher", "vendor", "manufacturer"];
    private static readonly string[] VersionHeaders = ["version", "software version", "display version"];
    private static readonly string[] InstalledOnHeaders = ["installed on", "install date", "installed", "installed date"];

    /// <summary>The date formats an export is allowed to use, invariant so a locale cannot change a date.</summary>
    private static readonly string[] DateFormats =
        ["yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yyyy", "dd-MM-yyyy", "MM/dd/yyyy", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss"];

    /// <summary>The sentence a file with the wrong columns is refused with. Names every column this reads.</summary>
    public const string HeaderHelp =
        "An inventory file needs a machine column ('asset tag', 'serial number' or 'hostname') and a "
        + "software column ('software', 'display name' or 'product'). 'publisher', 'version' and "
        + "'installed on' are optional.";

    public static SoftwareImportPlan Plan(CiImportTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var headers = table.Headers.Select(Normalise).ToList();

        var assetTag = IndexOf(headers, AssetTagHeaders);
        var serial = IndexOf(headers, SerialHeaders);
        var hostname = IndexOf(headers, HostnameHeaders);
        var software = IndexOf(headers, SoftwareHeaders);
        var publisher = IndexOf(headers, PublisherHeaders);
        var version = IndexOf(headers, VersionHeaders);
        var installedOn = IndexOf(headers, InstalledOnHeaders);

        if (assetTag is null && serial is null && hostname is null)
        {
            return new([], $"The file has no column naming the machine. {HeaderHelp}");
        }

        if (software is null)
        {
            return new([], $"The file has no column naming the software. {HeaderHelp}");
        }

        var rows = new List<SoftwareImportParsedRow>(table.Rows.Count);
        foreach (var row in table.Rows)
        {
            var errors = new List<string>();
            var name = Cell(row.Cells, software);
            if (name is null)
            {
                errors.Add("The software name is blank.");
            }

            var machineTag = Cell(row.Cells, assetTag);
            var machineSerial = Cell(row.Cells, serial);
            var machineHost = Cell(row.Cells, hostname);
            if (machineTag is null && machineSerial is null && machineHost is null)
            {
                errors.Add("The row names no machine.");
            }

            DateOnly? installed = null;
            if (Cell(row.Cells, installedOn) is { } rawDate)
            {
                if (TryParseDate(rawDate, out var parsed))
                {
                    installed = parsed;
                }
                else
                {
                    errors.Add($"'{rawDate}' is not a date this import can read (use yyyy-MM-dd).");
                }
            }

            rows.Add(new(
                row.LineNumber,
                machineTag,
                machineSerial,
                machineHost,
                name ?? string.Empty,
                Cell(row.Cells, publisher),
                Cell(row.Cells, version),
                installed,
                errors));
        }

        return new(rows, null);
    }

    private static bool TryParseDate(string value, out DateOnly parsed)
    {
        if (DateTime.TryParseExact(
                value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
        {
            parsed = DateOnly.FromDateTime(exact);
            return true;
        }

        parsed = default;
        return false;
    }

    private static int? IndexOf(IReadOnlyList<string> headers, IReadOnlyList<string> aliases)
    {
        for (var index = 0; index < headers.Count; index++)
        {
            if (aliases.Contains(headers[index], StringComparer.Ordinal))
            {
                return index;
            }
        }

        return null;
    }

    private static string? Cell(IReadOnlyList<string> cells, int? index) =>
        index is { } position && position < cells.Count && cells[position].Trim() is { Length: > 0 } value
            ? value
            : null;

    /// <summary>Header comparison ignores case, surrounding space and the underscores an export writes.</summary>
    private static string Normalise(string header) =>
        SoftwareNormaliser.Canonicalise(header.Replace('_', ' ').Replace('-', ' '));
}
