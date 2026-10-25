param(
    [string] $ArtifactDirectory = "",
    [switch] $NoBuild,
    [switch] $ValidatorSelfTest,
    [switch] $VariedOfficeStyles
)

$ErrorActionPreference = 'Stop'

function Get-AcceptanceFailures([object] $value) {
    $problems = [System.Collections.Generic.List[string]]::new()
    if ([int]$value.formulaComparison.formulaCount -ne 16) { $problems.Add('expected exactly 16 formulas') }
    if ([int]$value.formulaComparison.matchingOrOfficeRecalculatedCount -ne 16) { $problems.Add('not all 16 formulas matched or were independently recalculated by Office') }
    if ([int]$value.formulaComparison.mismatchCount -ne 0) { $problems.Add('formula mismatches exist') }
    if (@($value.formulaComparison.formulas | Where-Object { $_.class -eq 'OFFICE_RECALCULATED_UNCOMPUTED' -and $null -eq $_.localCache }).Count -ne 1 -or @($value.formulaComparison.formulas | Where-Object { $_.cell -eq 'Summary!B10' -and $_.class -eq 'OFFICE_RECALCULATED_UNCOMPUTED' }).Count -ne 1) { $problems.Add('expected the one unsupported SUBTOTAL cache to be explicitly recalculated by Office') }
    if (@($value.chartEvidence).Count -ne 1) { $problems.Add('expected exactly one retained chart') }
    if (@($value.chartEvidence | Where-Object { $_.firstSeriesFormula -match '\$A\$2:\$A\$4' -and $_.firstSeriesFormula -match '\$B\$2:\$B\$4' }).Count -ne 1) { $problems.Add('chart category or value source range changed') }
    if (-not $value.styleEvidence.headerBold -or [string]::IsNullOrWhiteSpace([string]$value.styleEvidence.headerFill) -or [string]$value.styleEvidence.amountNumberFormat -notin @('#,##0.00', '#.##0,00')) { $problems.Add('expected formatting was lost') }
    if (-not $value.fixture.sourceUntouched -or $value.fixture.sourceHashBeforeEdit -ne $value.fixture.sourceHashAfterEdit) { $problems.Add('editing modified the Office input/source workbook') }
    if ([int]$value.fixture.formulaCount -ne 16 -or -not $value.fixture.edited.OpenXmlValidated -or -not $value.fixture.edited.SourceWorkbookPreserved) { $problems.Add('OpenXML fixture structure or source-preservation check failed') }
    if ([int]$value.fixture.chartPartCount -ne 1 -or @($value.fixture.chartXml | Where-Object { $_ -match '\$A\$2:\$A\$4' -and $_ -match '\$B\$2:\$B\$4' }).Count -ne 1) { $problems.Add('OpenXML chart part or source ranges are missing') }
    if ([long]$value.fixture.styleIndexes.sourceHeader -le 0 -or $value.fixture.styleIndexes.editedHeader -ne $value.fixture.styleIndexes.sourceHeader -or [long]$value.fixture.styleIndexes.sourceAmount -le 0 -or $value.fixture.styleIndexes.editedAmount -ne $value.fixture.styleIndexes.sourceAmount) { $problems.Add('OpenXML style indexes changed while editing') }
    if ($value.fixture.macroProjectPresent -ne $false) { $problems.Add('macro project is present in the generated fixture') }
    if ([int]$value.fixture.externalWorkbookLinkCount -ne 0) { $problems.Add('external workbook links are present in the generated fixture') }
    if ($value.macroSecurity -ne 'ForceDisable' -or $value.externalLinkUpdates -ne 'Disabled') { $problems.Add('Office macro or external-link safety setting was not applied') }
    if (-not $value.pdf.exists -or [long]$value.pdf.sizeBytes -le 0 -or [string]::IsNullOrWhiteSpace([string]$value.pdf.sha256)) { $problems.Add('rendered PDF is missing or empty') }
    if ($null -ne $value.importedEdit) {
        $imported = $value.importedEdit
        if (-not $imported.sourceUntouched -or $imported.sourceHashBefore -ne $imported.sourceHashAfter) { $problems.Add('Office-originated source workbook was modified') }
        if (-not $imported.openedAndCalculated -or -not $imported.pdf.exists -or [long]$imported.pdf.sizeBytes -le 0) { $problems.Add('Office-originated workbook did not reopen, calculate, and render') }
        if (-not $imported.editHarness.sourceUntouched -or -not $imported.editHarness.edited.SourceWorkbookPreserved) { $problems.Add('public EditWorkbook did not preserve its source') }
        # Excel can emit extensions which the SDK validator reports even in the untouched source.
        # Retain those warnings in the report; accept only an unchanged set, never a new error.
        $sourceWarnings = ConvertTo-Json -InputObject @($imported.editHarness.sourceValidationWarnings | Sort-Object) -Compress
        $editedWarnings = ConvertTo-Json -InputObject @($imported.editHarness.edited.ValidationWarnings | Sort-Object) -Compress
        if ($sourceWarnings -ne $editedWarnings) { $problems.Add('public EditWorkbook introduced OpenXML validation warnings') }
        if (@($imported.formulaResults).Count -ne 5 -or @($imported.formulaResults | Select-Object -ExpandProperty cell -Unique).Count -ne 5 -or @($imported.formulaResults | Where-Object { -not $_.matches }).Count -ne 0) { $problems.Add('Office-originated workbook formulas are missing or did not match expected results') }
        if ($imported.editHarness.macroProjectPresent -ne $false -or [int]$imported.editHarness.externalWorkbookLinkCount -ne 0) { $problems.Add('Office-originated edit contains macros or external workbook links') }
        if (-not $imported.preserved.mergedHeader -or -not $imported.preserved.table -or -not $imported.preserved.chart -or -not $imported.preserved.namedRange -or -not $imported.preserved.hiddenSheet -or -not $imported.preserved.styles -or -not $imported.preserved.unrelatedData) { $problems.Add('Office-originated workbook content/style/chart/table/name/visibility was not preserved') }
        if (-not $imported.macroSecurity -or -not $imported.externalLinkUpdatesDisabled) { $problems.Add('Office-originated workbook Office safety settings were not applied') }
    } else {
        $problems.Add('Office-originated workbook edit evidence is missing')
    }
    return $problems.ToArray()
}

if ($ValidatorSelfTest) {
$validatorFormulas = @([pscustomobject]@{cell='Summary!B10';class='OFFICE_RECALCULATED_UNCOMPUTED';localCache=$null})
for ($i = 1; $i -le 15; $i++) { $validatorFormulas += [pscustomobject]@{cell="Summary!B$i";class='MATCH';localCache='1'} }
$valid = [pscustomobject]@{
        formulaComparison = [pscustomobject]@{ formulaCount=16; matchingOrOfficeRecalculatedCount=16; mismatchCount=0; formulas=$validatorFormulas }
        chartEvidence = @([pscustomobject]@{ firstSeriesFormula="=SERIES(, 'Inputs'!`$A`$2:`$A`$4, 'Inputs'!`$B`$2:`$B`$4, 1)" })
        fixtureChartXml = @()
        styleEvidence = [pscustomobject]@{ headerBold=$true; headerFill='D9EAF7'; amountNumberFormat='#,##0.00' }
        fixture = [pscustomobject]@{ sourceUntouched=$true; sourceHashBeforeEdit='same'; sourceHashAfterEdit='same'; formulaCount=16; edited=[pscustomobject]@{ OpenXmlValidated=$true; SourceWorkbookPreserved=$true }; macroProjectPresent=$false; externalWorkbookLinkCount=0; chartPartCount=1; chartXml=@("'Inputs'!`$A`$2:`$A`$4 'Inputs'!`$B`$2:`$B`$4"); styleIndexes=[pscustomobject]@{sourceHeader=1;editedHeader=1;sourceAmount=2;editedAmount=2} }
        macroSecurity='ForceDisable'; externalLinkUpdates='Disabled'
        pdf=[pscustomobject]@{ exists=$true; sizeBytes=100; sha256='pdf-hash' }
        importedEdit=[pscustomobject]@{ sourceUntouched=$true; sourceHashBefore='same'; sourceHashAfter='same'; openedAndCalculated=$true; pdf=[pscustomobject]@{exists=$true;sizeBytes=100}; editHarness=[pscustomobject]@{sourceUntouched=$true;macroProjectPresent=$false;externalWorkbookLinkCount=0;sourceValidationWarnings=@();edited=[pscustomobject]@{OpenXmlValidated=$true;SourceWorkbookPreserved=$true;ValidationWarnings=@()}}; formulaResults=@(1..5 | ForEach-Object { [pscustomobject]@{cell="fixture!B$_";matches=$true} }); preserved=[pscustomobject]@{mergedHeader=$true;table=$true;chart=$true;namedRange=$true;hiddenSheet=$true;styles=$true;unrelatedData=$true};macroSecurity=$true;externalLinkUpdatesDisabled=$true }
    }
    $validProblems = @(Get-AcceptanceFailures $valid)
    if ($validProblems.Count -ne 0) { throw "Acceptance validator rejects a complete synthetic report: $($validProblems -join '; ')" }
    $mutations = @(
        @{ name='missing formula'; change={ param($r) $r.formulaComparison.formulaCount=15 } },
        @{ name='missing chart'; change={ param($r) $r.chartEvidence=@() } },
        @{ name='changed source'; change={ param($r) $r.fixture.sourceHashAfterEdit='changed' } },
        @{ name='lost style'; change={ param($r) $r.styleEvidence.headerBold=$false } },
        @{ name='macro part'; change={ param($r) $r.fixture.macroProjectPresent=$true } },
        @{ name='external link'; change={ param($r) $r.fixture.externalWorkbookLinkCount=1 } },
        @{ name='empty PDF'; change={ param($r) $r.pdf.sizeBytes=0 } },
        @{ name='lost imported table'; change={ param($r) $r.importedEdit.preserved.table=$false } },
        @{ name='wrong imported formula'; change={ param($r) $r.importedEdit.formulaResults[0].matches=$false } },
        @{ name='missing imported formulas'; change={ param($r) $r.importedEdit.formulaResults=@() } },
        @{ name='new validation warning'; change={ param($r) $r.importedEdit.editHarness.edited.ValidationWarnings=@('new error') } },
        @{ name='imported macro part'; change={ param($r) $r.importedEdit.editHarness.macroProjectPresent=$true } }
    )
    foreach ($mutation in $mutations) {
        $candidate = $valid | ConvertTo-Json -Depth 8 | ConvertFrom-Json
        & $mutation.change $candidate
        if (@(Get-AcceptanceFailures $candidate).Count -eq 0) { throw "Acceptance validator failed to reject: $($mutation.name)" }
    }
    Write-Output "PASS: validator rejects all $($mutations.Count) intentionally incomplete reports."
    return
}
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$toolProject = Join-Path $repoRoot 'tools/PaRuX.WorkbookOfficeAcceptance/WorkbookOfficeAcceptance.csproj'
if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $ArtifactDirectory = Join-Path $repoRoot ".ai/artifacts/workbook-office-$stamp"
}
$ArtifactDirectory = [System.IO.Path]::GetFullPath($ArtifactDirectory)
New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null

$excelType = [Type]::GetTypeFromProgID('Excel.Application')
if ($null -eq $excelType) {
    throw 'Microsoft Excel desktop automation is not registered on this machine.'
}

function Release-ComObject([object] $value) {
    if ($null -ne $value -and [Runtime.InteropServices.Marshal]::IsComObject($value)) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($value)
    }
}

$dotnetArguments = @('run', '--project', $toolProject, '--configuration', 'Release')
if ($NoBuild) { $dotnetArguments += '--no-build' }
$dotnetArguments += @('--', $ArtifactDirectory)
$dotnetOutput = & dotnet @dotnetArguments 2>&1
if ($LASTEXITCODE -ne 0) {
    $dotnetOutput | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'harness-build.log') -Encoding utf8
    throw "The public DocumentWriteService fixture harness failed with exit code $LASTEXITCODE. See harness-build.log."
}
$fixtureLine = $dotnetOutput | Where-Object { $_ -like 'PARUX_OFFICE_FIXTURE=*' } | Select-Object -Last 1
if ($null -eq $fixtureLine) {
    $dotnetOutput | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'harness-build.log') -Encoding utf8
    throw 'The fixture harness did not emit its machine-readable result.'
}
$fixture = ($fixtureLine -replace '^PARUX_OFFICE_FIXTURE=', '') | ConvertFrom-Json
if (-not $fixture.edited.OpenXmlValidated -or -not $fixture.sourceUntouched) {
    throw 'Fixture preflight failed OpenXML validation or changed the source workbook during editing.'
}

$candidate = [System.IO.Path]::GetFullPath([string]$fixture.edited.Path)
$officePath = Join-Path $ArtifactDirectory 'office-recalculated.xlsx'
Copy-Item -LiteralPath $candidate -Destination $officePath
$pdfPath = Join-Path $ArtifactDirectory 'office-recalculated.pdf'
$excel = $null
$workbook = $null
$syntheticFormulaResults = [System.Collections.Generic.List[object]]::new()
$officeVersion = $null
$officeBuild = $null
$calculationState = $null
$chartEvidence = [System.Collections.Generic.List[object]]::new()
$styleEvidence = $null
$pdfEvidence = $null
$failure = $null

try {
    # Construct a fresh COM server. Never enumerate, attach to, close, or kill the user's Excel
    # processes or workbooks; every workbook this harness opens is generated in the artifact folder.
    $excel = [Activator]::CreateInstance($excelType)
    $excel.Visible = $false
    $excel.DisplayAlerts = $false
    $excel.ScreenUpdating = $false
    $excel.EnableEvents = $false
    $excel.AskToUpdateLinks = $false
    $excel.AutomationSecurity = 3 # msoAutomationSecurityForceDisable
    $officeVersion = [string]$excel.Version
    $officeBuild = [string]$excel.Build

    $workbook = $excel.Workbooks.Open($officePath, 0, $false)
    $excel.CalculateFullRebuild()
    # Excel 16.0 on this host keeps the application-wide state at xlPending (2) even after a
    # synchronous full rebuild. Calculate each fixture cell explicitly and validate the values
    # directly; a nonzero global state is recorded as diagnostic rather than treated as failure.
    $calculationState = [int]$excel.CalculationState

    foreach ($entry in $fixture.formulas) {
        $parts = ([string]$entry.Cell) -split '!', 2
        $sheetName = $parts[0]
        $cellReference = $parts[1]
        $sheet = $null
        $range = $null
        try {
            $sheet = $workbook.Worksheets.Item($sheetName)
            $range = $sheet.Range($cellReference)
            $range.Calculate()
            $value = $range.Value2
            $officeText = if ($null -eq $value) { $null } else { [Convert]::ToString($value, [Globalization.CultureInfo]::InvariantCulture) }
            $localText = $entry.LocalCache
            $numericLocal = 0.0
            $numericOffice = 0.0
            $localIsNumber = $null -ne $localText -and [double]::TryParse([string]$localText,
                [Globalization.NumberStyles]::Float,
                [Globalization.CultureInfo]::InvariantCulture, [ref]$numericLocal)
            $officeIsNumber = $null -ne $value -and [double]::TryParse([Convert]::ToString($value,
                [Globalization.CultureInfo]::InvariantCulture), [Globalization.NumberStyles]::Float,
                [Globalization.CultureInfo]::InvariantCulture, [ref]$numericOffice)
            $hasExcelError = $null -ne $officeText -and $officeText.StartsWith('#', [StringComparison]::Ordinal)

            if ($null -eq $localText) {
                $classification = if ($null -ne $officeText -and -not $hasExcelError) { 'OFFICE_RECALCULATED_UNCOMPUTED' } else { 'OFFICE_RECALC_FAILED' }
                $matches = $classification -eq 'OFFICE_RECALCULATED_UNCOMPUTED'
            } elseif ($localIsNumber -and $officeIsNumber) {
                $matches = [Math]::Abs($numericLocal - $numericOffice) -le 1e-10
                $classification = if ($matches) { 'MATCH' } else { 'SEMANTIC_MISMATCH' }
            } else {
                $matches = [string]$localText -eq [string]$officeText
                $classification = if ($matches) { 'MATCH' } else { 'SEMANTIC_MISMATCH' }
            }
            $syntheticFormulaResults.Add([pscustomobject]@{
                cell = [string]$entry.Cell
                formula = [string]$entry.Formula
                localCache = $localText
                officeValue = $officeText
                class = $classification
                matches = $matches
            })
        } finally {
            Release-ComObject $range
            Release-ComObject $sheet
            $range = $null
            $sheet = $null
        }
    }

    foreach ($sheetName in $fixture.sheetNames) {
        $sheet = $null
        $chartObjects = $null
        $chartObject = $null
        $chart = $null
        $series = $null
        try {
            $sheet = $workbook.Worksheets.Item([string]$sheetName)
            $chartObjects = $sheet.ChartObjects()
            for ($index = 1; $index -le [int]$chartObjects.Count; $index++) {
                $chartObject = $chartObjects.Item($index)
                $chart = $chartObject.Chart
                $series = $chart.SeriesCollection()
                $seriesFormula = if ([int]$series.Count -gt 0) { [string]$series.Item(1).Formula } else { '' }
                $chartEvidence.Add([pscustomobject]@{
                    sheet = [string]$sheetName
                    title = [string]$chart.ChartTitle.Text
                    seriesCount = [int]$series.Count
                    firstSeriesFormula = $seriesFormula
                })
                Release-ComObject $series
                Release-ComObject $chart
                Release-ComObject $chartObject
                $series = $null
                $chart = $null
                $chartObject = $null
            }
        } finally {
            Release-ComObject $series
            Release-ComObject $chart
            Release-ComObject $chartObject
            Release-ComObject $chartObjects
            Release-ComObject $sheet
        }
    }

    for ($index = 1; $index -le [int]$workbook.Worksheets.Count; $index++) {
        $sheet = $workbook.Worksheets.Item($index)
        try {
            $sheet.PageSetup.Zoom = $false
            $sheet.PageSetup.FitToPagesWide = 1
            $sheet.PageSetup.FitToPagesTall = 1
        } finally {
            Release-ComObject $sheet
        }
    }

    $styleSheet = $workbook.Worksheets.Item('Inputs')
    $header = $styleSheet.Range('A1')
    $amount = $styleSheet.Range('B2')
    $styleEvidence = [pscustomobject]@{
        headerBold = [bool]$header.Font.Bold
        headerFill = [string]$header.Interior.Color
        amountNumberFormat = [string]$amount.NumberFormat
    }
    Release-ComObject $amount
    Release-ComObject $header
    Release-ComObject $styleSheet

    $workbook.Save()
    $workbook.ExportAsFixedFormat(0, $pdfPath)
    $pdfEvidence = [pscustomobject]@{
        exists = Test-Path -LiteralPath $pdfPath
        sizeBytes = if (Test-Path -LiteralPath $pdfPath) { (Get-Item -LiteralPath $pdfPath).Length } else { 0 }
        sha256 = if (Test-Path -LiteralPath $pdfPath) { (Get-FileHash -LiteralPath $pdfPath -Algorithm SHA256).Hash } else { $null }
    }
} catch {
    $failure = $_.Exception.ToString()
} finally {
    if ($null -ne $workbook) {
        try { $workbook.Close($false) } catch { }
        Release-ComObject $workbook
        $workbook = $null
    }
    if ($null -ne $excel) {
        try { $excel.Quit() } catch { }
        Release-ComObject $excel
        $excel = $null
    }
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}

$imported = $null
$importError = $null
$importedPdfPath = Join-Path $ArtifactDirectory 'office-originated-recalculated.pdf'
$importSourcePath = Join-Path (Join-Path $ArtifactDirectory 'workbooks') 'Office Originated Source.xlsx'
$importedPath = Join-Path (Join-Path $ArtifactDirectory 'workbooks') 'Office Originated Edited.xlsx'
$importedExcel = $null
$importedWorkbook = $null
$officeSource = $null
try {
    # Create a genuine Excel-authored workbook in the isolated artifact folder. No existing
    # Excel process, workbook, or user file is enumerated or touched.
    $importedExcel = [Activator]::CreateInstance($excelType)
    $importedExcel.Visible = $false
    $importedExcel.DisplayAlerts = $false
    $importedExcel.ScreenUpdating = $false
    $importedExcel.EnableEvents = $false
    $importedExcel.AskToUpdateLinks = $false
    $importedExcel.AutomationSecurity = 3
    $officeSource = $importedExcel.Workbooks.Add()
    while ([int]$officeSource.Worksheets.Count -lt 3) {
        $extraSheet = $officeSource.Worksheets.Add()
        Release-ComObject $extraSheet
    }
    $dataSheet = $officeSource.Worksheets.Item(1)
    $dataSheet.Name = 'Imported Data'
    $summarySheet = $officeSource.Worksheets.Item(2)
    $summarySheet.Name = 'Summary'
    $hiddenSheet = $officeSource.Worksheets.Item(3)
    $hiddenSheet.Name = 'Hidden Metadata'
    $dataSheet.Range('A1:D1').Merge()
    $dataSheet.Range('A1').Value2 = 'Office authored source data'
    $dataSheet.Range('A1').Font.Bold = $true
    $dataSheet.Range('A1').Interior.Color = 0xD9EAF7
    $dataSheet.Range('A3:D3').Value2 = @('Region', 'Units', 'Rate', 'Extended')
    $dataSheet.Range('A4:C4').Value2 = @('North', 2, 10)
    $dataSheet.Range('A5:C5').Value2 = @('South', 3, 20)
    $dataSheet.Range('A6:C6').Value2 = @('West', 4, 30)
    foreach ($row in 4..6) { $dataSheet.Cells.Item($row, 4).Formula = "=B$row*C$row" }
    $dataSheet.Range('C4:C6').NumberFormat = '0.00'
    $table = $dataSheet.ListObjects.Add(1, $dataSheet.Range('A3:D6'), $null, 1)
    $table.Name = 'OfficeTable'
    $table.TableStyle = 'TableStyleMedium2'
    if ($VariedOfficeStyles) {
        $dataSheet.Range('B4:B6').NumberFormat = '"units "0'
        $dataSheet.Range('B4:B6').Font.Italic = $true
        $dataSheet.Range('C4:C6').NumberFormat = '"$"0'
        $table.TableStyle = 'TableStyleMedium9'
    }
    $sourceTableStyleName = [string]$table.TableStyle.Name
    $summarySheet.Range('A1').Value2 = 'Existing total'
    $summarySheet.Range('B2').Formula = "=SUM('Imported Data'!D4:D6)"
    $summarySheet.Range('B2').NumberFormat = '0'
    $summarySheet.Range('A4').Value2 = 'Unrelated note'
    $summarySheet.Range('B4').Value2 = 'keep this note'
    $summarySheet.Range('A4:B4').Font.Bold = $true
    [void]$officeSource.Names.Add('InputTotal', "='Imported Data'!`$C`$4:`$C`$6")
    $chartObjects = $dataSheet.ChartObjects()
    $chartObject = $chartObjects.Add(320, 20, 420, 250)
    $chart = $chartObject.Chart
    $chart.ChartType = 51 # xlColumnClustered
    $chartSeries = $chart.SeriesCollection().NewSeries()
    $chartSeries.Name = 'Extended'
    $chartSeries.XValues = $dataSheet.Range('A4:A6')
    $chartSeries.Values = $dataSheet.Range('D4:D6')
    $chart.HasTitle = $true
    $chart.ChartTitle.Text = 'Office source chart'
    $hiddenSheet.Range('A1').Value2 = 'Preserve hidden metadata'
    $hiddenSheet.Visible = 0
    $dataSheet.Range('A:D').ColumnWidth = 14
    $summarySheet.Range('A:A').ColumnWidth = 28
    $summarySheet.Range('B:C').ColumnWidth = 18
    $sourceHashBeforeEdit = if (Test-Path -LiteralPath $importSourcePath) { (Get-FileHash -LiteralPath $importSourcePath -Algorithm SHA256).Hash } else { $null }
    $sourceInputFormat = [string]$dataSheet.Range('B4').NumberFormat
    $officeSource.SaveAs($importSourcePath, 51) # xlOpenXMLWorkbook; macro-free package
    $officeSource.Close($false)
    Release-ComObject $chart
    Release-ComObject $chartObject
    Release-ComObject $chartObjects
    Release-ComObject $table
    Release-ComObject $hiddenSheet
    Release-ComObject $summarySheet
    Release-ComObject $dataSheet
    Release-ComObject $officeSource
    $officeSource = $null
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
    $sourceHashBeforeEdit = (Get-FileHash -LiteralPath $importSourcePath -Algorithm SHA256).Hash

    $editArguments = @('run', '--project', $toolProject, '--configuration', 'Release', '--no-build', '--', '--edit-office-source', $ArtifactDirectory, 'Office Originated Source.xlsx')
    $editOutput = & dotnet @editArguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "DocumentWriteService.EditWorkbook failed for the Office-authored source. See harness-build.log. $($editOutput -join ' ')" }
    $editLine = $editOutput | Where-Object { $_ -like 'PARUX_OFFICE_IMPORTED_EDIT=*' } | Select-Object -Last 1
    if ($null -eq $editLine) { throw 'Office-originated edit harness did not emit evidence.' }
    $editEvidence = ($editLine -replace '^PARUX_OFFICE_IMPORTED_EDIT=', '') | ConvertFrom-Json

    $importedExcelVersion = [string]$importedExcel.Version
    $importedExcelBuild = [string]$importedExcel.Build
    $importedMacroSecurity = ([int]$importedExcel.AutomationSecurity -eq 3)
    $importedLinkUpdatesDisabled = (-not [bool]$importedExcel.AskToUpdateLinks)
    $importedWorkbook = $importedExcel.Workbooks.Open($importedPath, 0, $false)
    $importedExcel.CalculateFullRebuild()
    foreach ($cellRef in @(@('Summary','B2'), @('Summary','C2'), @('Imported Data','D4'), @('Imported Data','D5'), @('Imported Data','D6'))) {
        $s = $importedWorkbook.Worksheets.Item($cellRef[0]); $r = $s.Range($cellRef[1]); $r.Calculate()
        Release-ComObject $r; Release-ComObject $s
    }
    $importedFormulaResults = [System.Collections.Generic.List[object]]::new()
    foreach ($spec in @(
        @{sheet='Summary'; cell='B2'; expected=210.0; formula="=SUM('Imported Data'!D4:D6)"},
        @{sheet='Summary'; cell='C2'; expected=60.0; formula="='Imported Data'!D4*2"},
        @{sheet='Imported Data'; cell='D4'; expected=30.0; formula='=B4*C4'},
        @{sheet='Imported Data'; cell='D5'; expected=60.0; formula='=B5*C5'},
        @{sheet='Imported Data'; cell='D6'; expected=120.0; formula='=B6*C6'}
    )) {
        $formulaSheet = $importedWorkbook.Worksheets.Item($spec.sheet)
        $formulaRange = $formulaSheet.Range($spec.cell)
        $actualValue = [double]$formulaRange.Value2
        $actualFormula = [string]$formulaRange.Formula
        $importedFormulaResults.Add([pscustomobject]@{ cell="$($spec.sheet)!$($spec.cell)"; formula=$actualFormula; value=$actualValue; expected=[double]$spec.expected; matches=($actualFormula -eq $spec.formula -and [Math]::Abs($actualValue - [double]$spec.expected) -lt 1e-10) })
        Release-ComObject $formulaRange; Release-ComObject $formulaSheet
    }
    $summaryWorksheet = $importedWorkbook.Worksheets.Item('Summary')
    $sumValue = [double]$summaryWorksheet.Range('B2').Value2
    $newValue = [double]$summaryWorksheet.Range('C2').Value2
    $sumFormula = [string]$summaryWorksheet.Range('B2').Formula
    $newFormula = [string]$summaryWorksheet.Range('C2').Formula
    $importedWorksheet = $importedWorkbook.Worksheets.Item('Imported Data')
    $hiddenWorksheet = $importedWorkbook.Worksheets.Item('Hidden Metadata')
    $importedChartCount = [int]$importedWorksheet.ChartObjects().Count
    $importedTableCount = [int]$importedWorksheet.ListObjects.Count
    $importedTableName = [string]$importedWorksheet.ListObjects.Item(1).Name
    $importedTableRange = [string]$importedWorksheet.ListObjects.Item(1).Range.Address($false,$false)
    $merged = [bool]$importedWorksheet.Range('A1:D1').MergeCells
    $headerBold = [bool]$importedWorksheet.Range('A1').Font.Bold
    $inputFormat = [string]$importedWorksheet.Range('B4').NumberFormat
    $headerText = [string]$importedWorksheet.Range('A1').Value2
    $unrelatedNote = [string]$summaryWorksheet.Range('B4').Value2
    $unrelatedNoteBold = [bool]$summaryWorksheet.Range('B4').Font.Bold
    $northLabel = [string]$importedWorksheet.Range('A4').Value2
    $southLabel = [string]$importedWorksheet.Range('A5').Value2
    $southUnits = [double]$importedWorksheet.Range('B5').Value2
    $inputB4 = [double]$importedWorksheet.Range('B4').Value2
    $inputC4 = [double]$importedWorksheet.Range('C4').Value2
    $inputD4Formula = [string]$importedWorksheet.Range('D4').Formula
    $summaryOriginalFormula = $sumFormula
    $hiddenState = [int]$hiddenWorksheet.Visible
    $definedName = $importedWorkbook.Names.Item('InputTotal')
    $definedFormula = [string]$definedName.RefersTo
    $chartCollection = $importedWorksheet.ChartObjects()
    $importedChart = $chartCollection.Item(1).Chart
    $chartSeriesCollection = $importedChart.SeriesCollection()
    $importedChartSeriesFormula = [string]$chartSeriesCollection.Item(1).Formula
    $tableStyleObject = $importedWorksheet.ListObjects.Item(1).TableStyle
    $importedTableStyle = [string]$tableStyleObject.Name
    Release-ComObject $tableStyleObject
    foreach ($ws in @($importedWorksheet,$summaryWorksheet,$hiddenWorksheet)) { $ws.PageSetup.Zoom=$false; $ws.PageSetup.FitToPagesWide=1; $ws.PageSetup.FitToPagesTall=1 }
    $importedWorkbook.Save()
    $importedWorkbook.ExportAsFixedFormat(0, $importedPdfPath)
    $importedPdf = [pscustomobject]@{ exists=(Test-Path -LiteralPath $importedPdfPath); sizeBytes=if(Test-Path -LiteralPath $importedPdfPath){(Get-Item -LiteralPath $importedPdfPath).Length}else{0}; sha256=if(Test-Path -LiteralPath $importedPdfPath){(Get-FileHash -LiteralPath $importedPdfPath -Algorithm SHA256).Hash}else{$null} }
    $importedWorkbook.Close($false); Release-ComObject $importedWorkbook; $importedWorkbook=$null
    $importedExcel.Quit(); Release-ComObject $importedExcel; $importedExcel=$null
    [GC]::Collect(); [GC]::WaitForPendingFinalizers()
    $sourceHashAfterEdit = (Get-FileHash -LiteralPath $importSourcePath -Algorithm SHA256).Hash
    $imported = [pscustomobject]@{
        sourceHashBefore=$sourceHashBeforeEdit; sourceHashAfter=$sourceHashAfterEdit; sourceUntouched=($sourceHashBeforeEdit -eq $sourceHashAfterEdit)
        openedAndCalculated=$true; formulaResults=@($importedFormulaResults); pdf=$importedPdf
        office=[pscustomobject]@{version=$importedExcelVersion;build=$importedExcelBuild}
        editHarness=$editEvidence
        nativeStructure=$editEvidence.sheets; definedNames=$editEvidence.definedNames
        observations=[pscustomobject]@{headerBold=$headerBold;inputFormat=$inputFormat;unrelatedNote=$unrelatedNote;unrelatedNoteBold=$unrelatedNoteBold;northLabel=$northLabel;southLabel=$southLabel;southUnits=$southUnits;inputB4=$inputB4;inputC4=$inputC4;inputD4Formula=$inputD4Formula;summaryOriginalFormula=$summaryOriginalFormula;newFormula=$newFormula;tableStyle=$importedTableStyle;namedRange=$definedFormula;chartSeries=$importedChartSeriesFormula;hiddenState=$hiddenState}
        preserved=[pscustomobject]@{
            mergedHeader=($merged -and $headerText -eq 'Office authored source data' -and @($editEvidence.sheets | Where-Object { $_.name -eq 'Imported Data' -and $_.mergeRanges -contains 'A1:D1' }).Count -eq 1)
            table=($importedTableCount -eq 1 -and $importedTableName -eq 'OfficeTable' -and $importedTableRange -eq 'A3:D6' -and @($editEvidence.sheets | Where-Object { $_.name -eq 'Imported Data' -and $_.tableCount -eq 1 -and $_.tableNames -contains 'OfficeTable' }).Count -eq 1)
            chart=($importedChartCount -eq 1 -and $importedChartSeriesFormula.Replace('$','') -match 'A4:A6' -and $importedChartSeriesFormula.Replace('$','') -match 'D4:D6' -and @($editEvidence.sheets | Where-Object { $_.name -eq 'Imported Data' -and $_.chartCount -eq 1 -and @($_.chartReferences | Where-Object { $_.Replace('$','') -match 'A4:A6' }).Count -eq 1 -and @($_.chartReferences | Where-Object { $_.Replace('$','') -match 'D4:D6' }).Count -eq 1 }).Count -eq 1)
            namedRange=($definedFormula.Replace('$','') -eq "='Imported Data'!C4:C6" -and @($editEvidence.definedNames | Where-Object { $_.name -eq 'InputTotal' -and $_.formula.Replace('$','') -eq "'Imported Data'!C4:C6" }).Count -eq 1)
            hiddenSheet=($hiddenState -eq 0 -and @($editEvidence.sheets | Where-Object { $_.name -eq 'Hidden Metadata' -and $_.state -eq 'hidden' }).Count -eq 1)
            styles=($headerBold -and $inputFormat -eq $sourceInputFormat -and $editEvidence.styleIndexes.mergedHeader -eq $editEvidence.styleIndexes.sourceMergedHeader -and $editEvidence.styleIndexes.editedInput -eq $editEvidence.styleIndexes.sourceInput)
            unrelatedData=($unrelatedNote -eq 'keep this note' -and $unrelatedNoteBold -and $northLabel -eq 'North' -and $southLabel -eq 'South' -and $southUnits -eq 3 -and $inputB4 -eq 3 -and $inputC4 -eq 10 -and $inputD4Formula -eq '=B4*C4' -and $summaryOriginalFormula -eq "=SUM('Imported Data'!D4:D6)" -and $newFormula -eq "='Imported Data'!D4*2" -and $importedTableStyle -eq $sourceTableStyleName)
        }
        macroSecurity=$importedMacroSecurity; externalLinkUpdatesDisabled=$importedLinkUpdatesDisabled
    }
} catch {
    $importError = $_.Exception.ToString() + "`n" + $_.ScriptStackTrace
    $imported = [pscustomobject]@{ sourceUntouched=$false; sourceHashBefore=''; sourceHashAfter=''; openedAndCalculated=$false; pdf=[pscustomobject]@{exists=$false;sizeBytes=0}; formulaResults=@(); preserved=[pscustomobject]@{mergedHeader=$false;table=$false;chart=$false;namedRange=$false;hiddenSheet=$false;styles=$false;unrelatedData=$false}; macroSecurity=$false; externalLinkUpdatesDisabled=$false; error=$importError }
} finally {
    if ($null -ne $officeSource) { try { $officeSource.Close($false) } catch {}; Release-ComObject $officeSource }
    if ($null -ne $importedWorkbook) { try { $importedWorkbook.Close($false) } catch {}; Release-ComObject $importedWorkbook }
    if ($null -ne $importedExcel) { try { $importedExcel.Quit() } catch {}; Release-ComObject $importedExcel }
    [GC]::Collect(); [GC]::WaitForPendingFinalizers()
}

$report = [pscustomobject]@{
    status = 'FAIL'
    office = [pscustomobject]@{ product = 'Microsoft Excel'; version = $officeVersion; build = $officeBuild }
    macroSecurity = 'ForceDisable'
    externalLinkUpdates = 'Disabled'
    excelCalculationStateAfterFullRebuild = $calculationState
    allFormulaCellsExplicitlyCalculated = $true
    fixture = $fixture
    recalculatedWorkbook = [pscustomobject]@{
        path = $officePath
        sha256 = if (Test-Path -LiteralPath $officePath) { (Get-FileHash -LiteralPath $officePath -Algorithm SHA256).Hash } else { $null }
    }
    formulaComparison = [pscustomobject]@{
        formulaCount = $syntheticFormulaResults.Count
        matchingOrOfficeRecalculatedCount = @($syntheticFormulaResults | Where-Object { $_.matches }).Count
        mismatchCount = @($syntheticFormulaResults | Where-Object { -not $_.matches }).Count
        formulas = @($syntheticFormulaResults)
    }
    chartEvidence = @($chartEvidence)
    styleEvidence = $styleEvidence
    pdf = $pdfEvidence
    error = $failure
}
$report | Add-Member -NotePropertyName importedEdit -NotePropertyValue $imported
$report | Add-Member -NotePropertyName importedEditError -NotePropertyValue $importError
$report | Add-Member -NotePropertyName acceptanceFailures -NotePropertyValue @()
$acceptanceFailures = @(Get-AcceptanceFailures $report)
$report.acceptanceFailures = $acceptanceFailures
if ($null -eq $failure -and $acceptanceFailures.Count -eq 0) { $report.status = 'PASS' }
$reportPath = Join-Path $ArtifactDirectory 'office-acceptance.json'
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $reportPath -Encoding utf8
Write-Output "Report: $reportPath"
Write-Output "Office: Microsoft Excel $officeVersion build $officeBuild"
Write-Output "Formula results: $($syntheticFormulaResults.Count), mismatches: $(@($syntheticFormulaResults | Where-Object { -not $_.matches }).Count)"
Write-Output "Workbook charts retained: $($chartEvidence.Count); rendered PDF: $($pdfEvidence.exists)"
if ($null -ne $failure) { Write-Output "Excel error: $failure" }
if ($acceptanceFailures.Count -gt 0) { Write-Output "Acceptance failures: $($acceptanceFailures -join '; ')" }
if ($report.status -ne 'PASS') { exit 2 }
