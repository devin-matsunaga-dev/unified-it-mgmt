using QRCoder;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Modules.Assets.Features.Labels;

/// <summary>
/// Draws labels. A single asset gets a page cut to the label so a dedicated label printer needs no
/// trimming; a batch gets an A4 sheet of them with cut guides, which QuestPDF paginates itself once
/// the column runs past the bottom of the page.
/// </summary>
public static class CiLabelDocument
{
    /// <summary>
    /// A4 is 210 mm wide, so this margin is what a full row of labels plus its gaps has to fit inside:
    /// three standard labels come to 195.5 mm of the 196 mm it leaves.
    /// </summary>
    private const float SheetMarginMm = 7f;

    static CiLabelDocument() => QuestPDF.Settings.License = LicenseType.Community;

    public static byte[] Render(IReadOnlyList<CiLabel> labels, CiLabelSize size) =>
        Build(labels, size).GeneratePdf();

    /// <summary>
    /// The laid-out document before it is written to PDF, kept separate so the layout can also be
    /// rendered to images when someone wants to look at a label without a printer.
    /// </summary>
    public static IDocument Build(IReadOnlyList<CiLabel> labels, CiLabelSize size)
    {
        var spec = LabelSpec.For(size);
        var drawn = labels.Select(label => new DrawnLabel(label, QrPng(label.Payload))).ToList();
        var document = drawn.Count == 1
            ? Document.Create(container => container.Page(page =>
            {
                page.Size(spec.WidthMm, spec.HeightMm, Unit.Millimetre);
                page.Margin(0);
                page.Content().Element(content => Draw(content, drawn[0], spec));
            }))
            : Document.Create(container => container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(SheetMarginMm, Unit.Millimetre);
                page.Content().Column(column =>
                {
                    column.Spacing(spec.GapMm, Unit.Millimetre);
                    foreach (var line in drawn.Chunk(spec.Columns))
                    {
                        column.Item().Row(row =>
                        {
                            row.Spacing(spec.GapMm, Unit.Millimetre);
                            foreach (var label in line)
                            {
                                row.ConstantItem(spec.WidthMm, Unit.Millimetre)
                                    .Height(spec.HeightMm, Unit.Millimetre)
                                    .Element(cell => Draw(cell, label, spec));
                            }

                            // The last line is padded so its labels keep the column positions the
                            // sheet stock expects rather than spreading across the page.
                            for (var empty = line.Length; empty < spec.Columns; empty++)
                            {
                                row.ConstantItem(spec.WidthMm, Unit.Millimetre);
                            }
                        });
                    }
                });
            }));

        return document;
    }

    private static void Draw(IContainer container, DrawnLabel drawn, LabelSpec spec)
    {
        var label = drawn.Label;
        container
            .Border(0.5f)
            .BorderColor(Colors.Grey.Lighten1)
            .Padding(spec.PaddingMm, Unit.Millimetre)
            .Row(row =>
            {
                row.Spacing(spec.PaddingMm, Unit.Millimetre);
                row.ConstantItem(spec.QrMm, Unit.Millimetre).AlignMiddle().Image(drawn.QrPng).FitArea();
                row.RelativeItem().AlignMiddle().Column(text =>
                {
                    text.Item().Text(Fit(label.Name, spec.NameLimit))
                        .FontSize(spec.NameFontSize).SemiBold().LineHeight(1.1f);
                    if (label.AssetTag is { } assetTag)
                    {
                        text.Item().PaddingTop(1).Text(Fit(assetTag, spec.CodeLimit))
                            .FontSize(spec.CodeFontSize).SemiBold();
                    }

                    if (label.SerialNumber is { } serial)
                    {
                        text.Item().Text($"S/N {Fit(serial, spec.CodeLimit)}")
                            .FontSize(spec.DetailFontSize).FontColor(Colors.Grey.Darken1);
                    }

                    // Neither identifier is mandatory on a CI, so a label with no codes at all still
                    // has to say what it is stuck to beyond its name.
                    if (label.AssetTag is null && label.SerialNumber is null)
                    {
                        text.Item().Text(label.Type.ToString())
                            .FontSize(spec.DetailFontSize).FontColor(Colors.Grey.Darken1);
                    }
                });
            });
    }

    /// <summary>
    /// Trimmed in code rather than clipped by the layout, so what a label leaves out is decided once
    /// and can be asserted on rather than depending on the renderer's measuring.
    /// </summary>
    public static string Fit(string value, int limit) =>
        value.Length <= limit ? value : $"{value[..(limit - 1)].TrimEnd()}…";

    private static byte[] QrPng(string payload)
    {
        using var generator = new QRCodeGenerator();
        // Level M survives the scuffing a label on a laptop lid picks up without costing the modules
        // that make a small label unreadable.
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(10);
    }

    private sealed record DrawnLabel(CiLabel Label, byte[] QrPng);

    private sealed record LabelSpec(
        float WidthMm,
        float HeightMm,
        int Columns,
        float GapMm,
        float PaddingMm,
        float QrMm,
        float NameFontSize,
        float CodeFontSize,
        float DetailFontSize,
        int NameLimit,
        int CodeLimit)
    {
        public static LabelSpec For(CiLabelSize size) => size switch
        {
            CiLabelSize.Standard => new(63.5f, 33.9f, 3, 2.5f, 2.5f, 26f, 9f, 8f, 7f, 30, 24),
            CiLabelSize.Small => new(45.7f, 21.2f, 4, 1.5f, 1.5f, 17f, 6.5f, 6f, 5.5f, 24, 20),
            _ => throw new InvalidOperationException($"Unknown label size '{size}'."),
        };
    }
}
