$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$AnalysisDir = Join-Path $Root "analysis_outputs\triage_mimic_heavy_day_service_2_5min_load_sweep_until_clear_20260604_140639"
$OutDir = Join-Path $Root "analysis_outputs\results_package\distribution_plots"
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

$Patients = Import-Csv (Join-Path $AnalysisDir "patient_protocol_records.csv")

$Loads = @(2.00, 1.50, 1.25, 1.00)
$ArchOrder = @(
    "FsmReactiveController",
    "PriorityQueueDispatcher",
    "DecisionTableController",
    "ProposedEnvironmentMediatedReplanner"
)
$ArchLabel = @{
    FsmReactiveController = "FSM"
    PriorityQueueDispatcher = "Priority dispatcher"
    DecisionTableController = "Decision table"
    ProposedEnvironmentMediatedReplanner = "Proposed GOAP"
}
$Colors = @{
    FsmReactiveController = "#64748B"
    PriorityQueueDispatcher = "#2563EB"
    DecisionTableController = "#7C3AED"
    ProposedEnvironmentMediatedReplanner = "#DC2626"
}

function N($Value) {
    if ($null -eq $Value -or $Value -eq "") { return 0.0 }
    return [double]::Parse([string]$Value, [Globalization.CultureInfo]::InvariantCulture)
}

function F($Value, $Digits = 1) {
    return ([double]$Value).ToString("F$Digits", [Globalization.CultureInfo]::InvariantCulture)
}

function EscapeXml($Text) {
    return [System.Security.SecurityElement]::Escape([string]$Text)
}

function Quantile($Values, $Q) {
    $arr = @($Values | Sort-Object)
    if ($arr.Count -eq 0) { return 0.0 }
    if ($arr.Count -eq 1) { return [double]$arr[0] }
    $pos = ([double]$arr.Count - 1.0) * [double]$Q
    $lo = [Math]::Floor($pos)
    $hi = [Math]::Ceiling($pos)
    if ($lo -eq $hi) { return [double]$arr[$lo] }
    $weight = $pos - $lo
    return ([double]$arr[$lo] * (1.0 - $weight)) + ([double]$arr[$hi] * $weight)
}

function Mean($Values) {
    $arr = @($Values)
    if ($arr.Count -eq 0) { return 0.0 }
    $sum = 0.0
    foreach ($v in $arr) { $sum += [double]$v }
    return $sum / $arr.Count
}

function GroupValues($Load, $Arch, $Priority, $Metric) {
    $loadText = ([double]$Load).ToString("0.0##", [Globalization.CultureInfo]::InvariantCulture)
    $rows = @($Patients | Where-Object {
        ([double]::Parse($_.load_multiplier, [Globalization.CultureInfo]::InvariantCulture) -eq [double]$Load) `
            -and $_.architecture -eq $Arch
    })

    if ($Priority -eq "critical") {
        $rows = @($rows | Where-Object { $_.is_critical -eq "True" })
    }
    elseif ($Priority -eq "normal") {
        $rows = @($rows | Where-Object { $_.is_critical -eq "False" })
    }

    if ($Metric -eq "assessment") {
        return @($rows | ForEach-Object { (N $_.time_to_triage_seconds) / 60.0 })
    }

    return @($rows | ForEach-Object { (N $_.total_seconds) / 60.0 })
}

function Stats($Values) {
    $arr = @($Values | Where-Object { $null -ne $_ })
    return [pscustomobject]@{
        Count = $arr.Count
        Min = Quantile $arr 0.00
        P05 = Quantile $arr 0.05
        Q1 = Quantile $arr 0.25
        Median = Quantile $arr 0.50
        Q3 = Quantile $arr 0.75
        P95 = Quantile $arr 0.95
        Max = Quantile $arr 1.00
        Mean = Mean $arr
    }
}

function Write-BoxplotSvg($FileName, $Title, $YLabel, $Priority, $Metric) {
    $W = 1260
    $H = 720
    $ML = 92
    $MR = 180
    $MT = 76
    $MB = 112
    $PlotW = $W - $ML - $MR
    $PlotH = $H - $MT - $MB
    $BoxW = 26

    $items = New-Object System.Collections.ArrayList
    $allValues = New-Object System.Collections.ArrayList
    foreach ($load in $Loads) {
        foreach ($arch in $ArchOrder) {
            $values = GroupValues $load $arch $Priority $Metric
            foreach ($v in $values) { [void]$allValues.Add($v) }
            [void]$items.Add([pscustomobject]@{
                Load = $load
                Arch = $arch
                S = Stats $values
            })
        }
    }

    $maxVal = ($allValues | Measure-Object -Maximum).Maximum
    $YMin = 0.0
    $YMax = [Math]::Ceiling(([double]$maxVal * 1.10) / 5.0) * 5.0
    if ($YMax -lt 5.0) { $YMax = 5.0 }
    $YRange = $YMax - $YMin
    function Y($Value) {
        return $MT + (($YMax - [double]$Value) / $YRange * $PlotH)
    }

    $groupStep = $PlotW / $Loads.Count
    $svg = New-Object System.Collections.ArrayList
    [void]$svg.Add("<svg xmlns=""http://www.w3.org/2000/svg"" width=""$W"" height=""$H"" viewBox=""0 0 $W $H"">")
    [void]$svg.Add("<rect width=""100%"" height=""100%"" fill=""#FFFFFF""/>")
    [void]$svg.Add("<text x=""$($W/2)"" y=""35"" text-anchor=""middle"" font-family=""Arial, sans-serif"" font-size=""23"" font-weight=""700"" fill=""#111827"">$(EscapeXml $Title)</text>")
    [void]$svg.Add("<text x=""$($W/2)"" y=""58"" text-anchor=""middle"" font-family=""Arial, sans-serif"" font-size=""13"" fill=""#475569"">Boxes show median and IQR; whiskers show 5th-95th percentiles.</text>")

    [void]$svg.Add("<line x1=""$ML"" y1=""$($MT+$PlotH)"" x2=""$($ML+$PlotW)"" y2=""$($MT+$PlotH)"" stroke=""#111827"" stroke-width=""1.6""/>")
    [void]$svg.Add("<line x1=""$ML"" y1=""$MT"" x2=""$ML"" y2=""$($MT+$PlotH)"" stroke=""#111827"" stroke-width=""1.6""/>")
    for ($t = 0; $t -le 5; $t++) {
        $val = $YMin + (($YMax - $YMin) * $t / 5.0)
        $yy = Y $val
        [void]$svg.Add("<line x1=""$ML"" y1=""$yy"" x2=""$($ML+$PlotW)"" y2=""$yy"" stroke=""#E5E7EB"" stroke-width=""1""/>")
        [void]$svg.Add("<text x=""$($ML-12)"" y=""$($yy+4)"" text-anchor=""end"" font-family=""Arial, sans-serif"" font-size=""12"" fill=""#374151"">$(F $val 1)</text>")
    }

    for ($li = 0; $li -lt $Loads.Count; $li++) {
        $groupX = $ML + ($li * $groupStep) + ($groupStep / 2.0)
        [void]$svg.Add("<text x=""$groupX"" y=""$($MT+$PlotH+38)"" text-anchor=""middle"" font-family=""Arial, sans-serif"" font-size=""14"" font-weight=""700"" fill=""#111827"">$($Loads[$li].ToString("F2", [Globalization.CultureInfo]::InvariantCulture))</text>")
        [void]$svg.Add("<text x=""$groupX"" y=""$($MT+$PlotH+58)"" text-anchor=""middle"" font-family=""Arial, sans-serif"" font-size=""11"" fill=""#64748B"">arrival multiplier</text>")

        for ($ai = 0; $ai -lt $ArchOrder.Count; $ai++) {
            $arch = $ArchOrder[$ai]
            $item = $items | Where-Object { $_.Load -eq $Loads[$li] -and $_.Arch -eq $arch } | Select-Object -First 1
            $s = $item.S
            $offset = ($ai - 1.5) * 34
            $cx = $groupX + $offset
            $color = $Colors[$arch]
            $yP05 = Y $s.P05
            $yQ1 = Y $s.Q1
            $yMed = Y $s.Median
            $yQ3 = Y $s.Q3
            $yP95 = Y $s.P95
            $yMean = Y $s.Mean
            $boxH = [Math]::Max(1.0, $yQ1 - $yQ3)
            [void]$svg.Add("<line x1=""$cx"" y1=""$yP95"" x2=""$cx"" y2=""$yQ3"" stroke=""$color"" stroke-width=""2""/>")
            [void]$svg.Add("<line x1=""$cx"" y1=""$yQ1"" x2=""$cx"" y2=""$yP05"" stroke=""$color"" stroke-width=""2""/>")
            [void]$svg.Add("<line x1=""$($cx-10)"" y1=""$yP95"" x2=""$($cx+10)"" y2=""$yP95"" stroke=""$color"" stroke-width=""2""/>")
            [void]$svg.Add("<line x1=""$($cx-10)"" y1=""$yP05"" x2=""$($cx+10)"" y2=""$yP05"" stroke=""$color"" stroke-width=""2""/>")
            [void]$svg.Add("<rect x=""$($cx-$BoxW/2)"" y=""$yQ3"" width=""$BoxW"" height=""$boxH"" fill=""$color"" fill-opacity=""0.18"" stroke=""$color"" stroke-width=""2""/>")
            [void]$svg.Add("<line x1=""$($cx-$BoxW/2)"" y1=""$yMed"" x2=""$($cx+$BoxW/2)"" y2=""$yMed"" stroke=""$color"" stroke-width=""3""/>")
            [void]$svg.Add("<circle cx=""$cx"" cy=""$yMean"" r=""3.5"" fill=""#FFFFFF"" stroke=""$color"" stroke-width=""2""/>")
        }
    }

    [void]$svg.Add("<text x=""$($ML+$PlotW/2)"" y=""$($H-28)"" text-anchor=""middle"" font-family=""Arial, sans-serif"" font-size=""14"" fill=""#111827"">Lower arrival multiplier means denser workload.</text>")
    [void]$svg.Add("<text x=""24"" y=""$($MT+$PlotH/2)"" text-anchor=""middle"" font-family=""Arial, sans-serif"" font-size=""14"" fill=""#111827"" transform=""rotate(-90 24 $($MT+$PlotH/2))"">$(EscapeXml $YLabel)</text>")

    $ly = $MT + 16
    foreach ($arch in $ArchOrder) {
        $color = $Colors[$arch]
        [void]$svg.Add("<rect x=""$($ML+$PlotW+34)"" y=""$($ly-10)"" width=""18"" height=""12"" fill=""$color"" fill-opacity=""0.22"" stroke=""$color"" stroke-width=""2""/>")
        [void]$svg.Add("<text x=""$($ML+$PlotW+60)"" y=""$($ly+1)"" font-family=""Arial, sans-serif"" font-size=""13"" fill=""#111827"">$($ArchLabel[$arch])</text>")
        $ly += 25
    }

    [void]$svg.Add("</svg>")
    Set-Content -LiteralPath (Join-Path $OutDir $FileName) -Value ($svg -join "`n") -Encoding UTF8
}

function Write-HistogramSvg($FileName, $Title, $Priority, $Metric, $Load, $XMaxOverride = $null) {
    $W = 1180
    $H = 760
    $ML = 70
    $MR = 45
    $MT = 78
    $MB = 76
    $PanelGapX = 62
    $PanelGapY = 70
    $PanelW = ($W - $ML - $MR - $PanelGapX) / 2.0
    $PanelH = ($H - $MT - $MB - $PanelGapY) / 2.0
    $Bins = 14

    $groups = New-Object System.Collections.ArrayList
    $allValues = New-Object System.Collections.ArrayList
    foreach ($arch in $ArchOrder) {
        $values = GroupValues $Load $arch $Priority $Metric
        foreach ($v in $values) { [void]$allValues.Add($v) }
        [void]$groups.Add([pscustomobject]@{ Arch = $arch; Values = $values })
    }

    $xMax = if ($null -ne $XMaxOverride) { [double]$XMaxOverride } else { [Math]::Ceiling((($allValues | Measure-Object -Maximum).Maximum) * 1.05) }
    if ($xMax -lt 1.0) { $xMax = 1.0 }
    $binW = $xMax / $Bins
    $maxCount = 1
    foreach ($g in $groups) {
        $counts = @(0) * $Bins
        foreach ($v in $g.Values) {
            $idx = [Math]::Floor([double]$v / $binW)
            if ($idx -lt 0) { $idx = 0 }
            if ($idx -ge $Bins) { $idx = $Bins - 1 }
            $counts[$idx]++
        }
        $g | Add-Member -MemberType NoteProperty -Name Counts -Value $counts
        $localMax = ($counts | Measure-Object -Maximum).Maximum
        if ($localMax -gt $maxCount) { $maxCount = $localMax }
    }
    $yMax = [Math]::Ceiling($maxCount / 5.0) * 5.0
    if ($yMax -lt 5) { $yMax = 5 }

    $svg = New-Object System.Collections.ArrayList
    $loadLabel = ([double]$Load).ToString("F2", [Globalization.CultureInfo]::InvariantCulture)
    [void]$svg.Add("<svg xmlns=""http://www.w3.org/2000/svg"" width=""$W"" height=""$H"" viewBox=""0 0 $W $H"">")
    [void]$svg.Add("<rect width=""100%"" height=""100%"" fill=""#FFFFFF""/>")
    [void]$svg.Add("<text x=""$($W/2)"" y=""35"" text-anchor=""middle"" font-family=""Arial, sans-serif"" font-size=""23"" font-weight=""700"" fill=""#111827"">$(EscapeXml $Title)</text>")
    [void]$svg.Add("<text x=""$($W/2)"" y=""58"" text-anchor=""middle"" font-family=""Arial, sans-serif"" font-size=""13"" fill=""#475569"">Arrival multiplier $loadLabel; highest-density workload in this batch.</text>")

    for ($gi = 0; $gi -lt $groups.Count; $gi++) {
        $g = $groups[$gi]
        $row = [Math]::Floor($gi / 2)
        $col = $gi % 2
        $px = $ML + ($col * ($PanelW + $PanelGapX))
        $py = $MT + ($row * ($PanelH + $PanelGapY))
        $color = $Colors[$g.Arch]
        [void]$svg.Add("<text x=""$($px+$PanelW/2)"" y=""$($py-18)"" text-anchor=""middle"" font-family=""Arial, sans-serif"" font-size=""16"" font-weight=""700"" fill=""$color"">$($ArchLabel[$g.Arch])</text>")
        [void]$svg.Add("<line x1=""$px"" y1=""$($py+$PanelH)"" x2=""$($px+$PanelW)"" y2=""$($py+$PanelH)"" stroke=""#111827"" stroke-width=""1.3""/>")
        [void]$svg.Add("<line x1=""$px"" y1=""$py"" x2=""$px"" y2=""$($py+$PanelH)"" stroke=""#111827"" stroke-width=""1.3""/>")
        for ($t = 0; $t -le 4; $t++) {
            $val = $yMax * $t / 4.0
            $yy = $py + (($yMax - $val) / $yMax * $PanelH)
            [void]$svg.Add("<line x1=""$px"" y1=""$yy"" x2=""$($px+$PanelW)"" y2=""$yy"" stroke=""#E5E7EB"" stroke-width=""1""/>")
            [void]$svg.Add("<text x=""$($px-8)"" y=""$($yy+4)"" text-anchor=""end"" font-family=""Arial, sans-serif"" font-size=""11"" fill=""#475569"">$(F $val 0)</text>")
        }
        for ($b = 0; $b -lt $Bins; $b++) {
            $count = $g.Counts[$b]
            $barX = $px + ($b * $PanelW / $Bins) + 2
            $barW = ($PanelW / $Bins) - 4
            $barH = if ($count -eq 0) { 0 } else { $count / $yMax * $PanelH }
            $barY = $py + $PanelH - $barH
            [void]$svg.Add("<rect x=""$barX"" y=""$barY"" width=""$barW"" height=""$barH"" fill=""$color"" fill-opacity=""0.55""/>")
        }
        $mean = Mean $g.Values
        $meanX = $px + ($mean / $xMax * $PanelW)
        [void]$svg.Add("<line x1=""$meanX"" y1=""$py"" x2=""$meanX"" y2=""$($py+$PanelH)"" stroke=""$color"" stroke-width=""2"" stroke-dasharray=""6 4""/>")
        [void]$svg.Add("<text x=""$meanX"" y=""$($py+$PanelH+18)"" text-anchor=""middle"" font-family=""Arial, sans-serif"" font-size=""11"" fill=""$color"">mean $(F $mean 1)</text>")
        for ($t = 0; $t -le 4; $t++) {
            $xval = $xMax * $t / 4.0
            $xx = $px + ($xval / $xMax * $PanelW)
            [void]$svg.Add("<line x1=""$xx"" y1=""$($py+$PanelH)"" x2=""$xx"" y2=""$($py+$PanelH+5)"" stroke=""#111827""/>")
            [void]$svg.Add("<text x=""$xx"" y=""$($py+$PanelH+34)"" text-anchor=""middle"" font-family=""Arial, sans-serif"" font-size=""11"" fill=""#111827"">$(F $xval 0)</text>")
        }
    }

    [void]$svg.Add("<text x=""$($W/2)"" y=""$($H-24)"" text-anchor=""middle"" font-family=""Arial, sans-serif"" font-size=""14"" fill=""#111827"">Time to assessment (min)</text>")
    [void]$svg.Add("<text x=""20"" y=""$($H/2)"" text-anchor=""middle"" font-family=""Arial, sans-serif"" font-size=""14"" fill=""#111827"" transform=""rotate(-90 20 $($H/2))"">Patient count</text>")
    [void]$svg.Add("</svg>")
    Set-Content -LiteralPath (Join-Path $OutDir $FileName) -Value ($svg -join "`n") -Encoding UTF8
}

Write-BoxplotSvg "box_critical_time_to_assessment.svg" "Critical Patients: Time to Assessment" "Time to assessment (min)" "critical" "assessment"
Write-BoxplotSvg "box_normal_time_to_assessment.svg" "Normal Patients: Time to Assessment" "Time to assessment (min)" "normal" "assessment"
Write-BoxplotSvg "box_all_total_time.svg" "All Patients: Total System Time" "Total time in system (min)" "all" "total"

Write-HistogramSvg "hist_dense_critical_time_to_assessment.svg" "Dense Scenario Histogram: Critical Time to Assessment" "critical" "assessment" 1.00 8.0
Write-HistogramSvg "hist_dense_normal_time_to_assessment.svg" "Dense Scenario Histogram: Normal Time to Assessment" "normal" "assessment" 1.00 40.0

$Captions = @"
# Distribution Figures and Captions

## Figure D1
![Critical boxplot](box_critical_time_to_assessment.svg)

**Caption.** Patient-level distribution of time to assessment for critical patients. Boxes show the interquartile range and median; whiskers show the 5th-95th percentile range. The proposed controller shows the lowest critical-patient tail latency in the densest workload condition.

## Figure D2
![Normal boxplot](box_normal_time_to_assessment.svg)

**Caption.** Patient-level distribution of time to assessment for normal-priority patients. This figure shows the fairness cost of critical-priority routing under moderate loads and its disappearance in the densest workload.

## Figure D3
![Total time boxplot](box_all_total_time.svg)

**Caption.** Distribution of total patient system time from arrival to home completion. This complements the aggregate completion-time table by showing patient-level spread rather than only the final run duration.

## Figure D4
![Critical histogram](hist_dense_critical_time_to_assessment.svg)

**Caption.** Histogram of critical-patient time to assessment in the densest workload condition (arrival multiplier 1.00). Dashed vertical lines mark controller-specific means.

## Figure D5
![Normal histogram](hist_dense_normal_time_to_assessment.svg)

**Caption.** Histogram of normal-patient time to assessment in the densest workload condition (arrival multiplier 1.00). This view makes the normal-patient waiting-time trade-off visible as a distribution rather than as a single average.
"@
Set-Content -LiteralPath (Join-Path $OutDir "distribution_figure_captions.md") -Value $Captions -Encoding UTF8

Write-Output "Generated distribution plots: $OutDir"
