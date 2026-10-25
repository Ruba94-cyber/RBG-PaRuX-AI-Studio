namespace PaRuX.Contracts;

// ---- Reading the apps already installed on this PC: notes, mail, calendar, bookmarks. Every
// ---- source is off until connected, read-only, scoped to one path, and read locally - connecting
// ---- one adds no network egress of any kind. ----

public sealed class DesktopIntegrationStatus
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>"notes" | "mail" | "calendar" | "bookmarks".</summary>
    public string Kind { get; set; } = string.Empty;
    public string WhatItReads { get; set; } = string.Empty;
    public bool Detected { get; set; }
    public string? DetectedPath { get; set; }
    public bool RequiresExplicitPath { get; set; }
    public bool Connected { get; set; }
    public string? ConnectedPath { get; set; }
    public DateTime? LastReadUtc { get; set; }
    public int LastItemCount { get; set; }
    public string? LastError { get; set; }
}

public sealed class ConnectDesktopIntegrationRequest
{
    public string? Path { get; set; }
}

public sealed class DesktopIntegrationItem
{
    public string SourceId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Subtitle { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public string Excerpt { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
}

// ---- Documents PaRuX generates. Reading someone's files and writing into their filesystem are
// ---- different risks; everything below lands in one folder the user nominated, never overwrites,
// ---- and reports the path it produced. ----

public sealed class WordDocumentContent
{
    public string Title { get; set; } = string.Empty;
    public List<WordBlock> Blocks { get; set; } = new();
}

public sealed class WordBlock
{
    public string Kind { get; set; } = "paragraph";
    public string Text { get; set; } = string.Empty;
    public int Level { get; set; }
    public List<WordTableRow> Rows { get; set; } = new();
}

public sealed class WordTableRow
{
    public List<string> Cells { get; set; } = new();
}

/// <summary>
/// Native workbook contract. <see cref="WorkbookSheet.Rows"/> is the backward-compatible simple
/// table path. <see cref="WorkbookSheet.StructuredCells"/> is additive and provides real spreadsheet
/// semantics: explicit references/types, formulas, formatting and chart ranges. Both may coexist;
/// a structured cell replaces the legacy cell at the same reference.
/// </summary>
public sealed class WorkbookContent
{
    public List<WorkbookSheet> Sheets { get; set; } = new();
    /// <summary>Ask spreadsheet applications to recalculate formulas when the output is opened.
    /// PaRuX still writes only cached values it computed independently; this flag is not presented
    /// as proof that every Excel formula has been evaluated locally.</summary>
    public bool RecalculateOnOpen { get; set; } = true;
}

public sealed class WorkbookSheet
{
    public string Name { get; set; } = string.Empty;
    public List<WorkbookRow> Rows { get; set; } = new();
    public List<WorkbookCell> StructuredCells { get; set; } = new();
    public List<WorkbookChart> Charts { get; set; } = new();
}

public sealed class WorkbookRow
{
    /// <summary>Legacy simple-table values. Numeric text is stored as a number. Strings beginning
    /// with '=' remain text for backward compatibility; use StructuredCells.Formula for formulas.</summary>
    public List<string> Cells { get; set; } = new();
}

public sealed class WorkbookCell
{
    /// <summary>A1-style reference such as B4.</summary>
    public string Reference { get; set; } = string.Empty;
    /// <summary>Literal value when Formula is empty.</summary>
    public string? Value { get; set; }
    /// <summary>Excel formula, with or without a leading '='. PaRuX evaluates the supported numeric
    /// subset itself for a trustworthy cached value and otherwise leaves the result uncomputed so
    /// Excel/LibreOffice can recalculate it on open.</summary>
    public string? Formula { get; set; }
    /// <summary>auto | string | number | boolean | date.</summary>
    public string DataType { get; set; } = "auto";
    public WorkbookCellStyle? Style { get; set; }
}

public sealed class WorkbookCellStyle
{
    public bool Bold { get; set; }
    /// <summary>ARGB or RGB hex, e.g. FFEFEFEF or EFEFEF.</summary>
    public string? FillColor { get; set; }
    public string? FontColor { get; set; }
    /// <summary>Excel-compatible number-format code, e.g. #,##0.00 or 0.0%.</summary>
    public string? NumberFormat { get; set; }
    /// <summary>left | center | right.</summary>
    public string? HorizontalAlignment { get; set; }
}

public sealed class WorkbookChart
{
    /// <summary>column | bar | line | pie.</summary>
    public string Type { get; set; } = "column";
    public string Title { get; set; } = string.Empty;
    /// <summary>Sheet-qualified or current-sheet range, e.g. A2:A8 or 'Sales 2026'!A2:A8.</summary>
    public string CategoryRange { get; set; } = string.Empty;
    public List<WorkbookChartSeries> Series { get; set; } = new();
    /// <summary>Top-left and bottom-right anchors. Defaults to H2 and P18.</summary>
    public string FromCell { get; set; } = "H2";
    public string ToCell { get; set; } = "P18";
}

public sealed class WorkbookChartSeries
{
    public string Name { get; set; } = string.Empty;
    public string ValueRange { get; set; } = string.Empty;
}

public sealed class DocumentWriteResult
{
    public string Path { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public bool RenamedToAvoidOverwrite { get; set; }
    /// <summary>not-applicable | no-formulas | cached-local-and-recalc-on-open |
    /// contains-uncomputed-formulas-recalc-on-open.</summary>
    public string CalculationStatus { get; set; } = "not-applicable";
    public bool OpenXmlValidated { get; set; }
    public List<string> ValidationWarnings { get; set; } = new();
    /// <summary>True only when this output was made by copying an existing workbook before applying
    /// targeted changes; the original remains untouched.</summary>
    public bool SourceWorkbookPreserved { get; set; }
}

public sealed class DocumentOutputStatus
{
    public string Folder { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
}

public sealed class SetDocumentOutputRequest
{
    public string? Folder { get; set; }
}
