$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$AnalysisDir = Join-Path $Root "analysis_outputs\triage_mimic_heavy_day_service_2_5min_load_sweep_until_clear_20260604_140639"
$RunDir = "C:\UnityProgects\GOAP_5Attempt\GOAP_Diagnostics\Experiments\triage_mimic_heavy_day_service_2_5min_load_sweep_until_clear_20260604_140639"
$OutDir = Join-Path $Root "analysis_outputs\results_package"
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

$Summary = Import-Csv (Join-Path $AnalysisDir "protocol_summary.csv")
$Runs = Import-Csv (Join-Path $RunDir "runs_summary.csv")
$Patients = Import-Csv (Join-Path $AnalysisDir "patient_protocol_records.csv")
$TimeSeries = Import-Csv (Join-Path $RunDir "time_series.csv")

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

function Key($Load, $Arch) {
    return ("{0:F2}|{1}" -f [double]$Load, $Arch)
}

function N($Value) {
    if ($null -eq $Value -or $Value -eq "") { return 0.0 }
    return [double]::Parse([string]$Value, [Globalization.CultureInfo]::InvariantCulture)
}

function F($Value, $Digits = 2) {
    return (N $Value).ToString("F$Digits", [Globalization.CultureInfo]::InvariantCulture)
}

function Average($Values) {
    $arr = @($Values | Where-Object { $null -ne $_ })
    if ($arr.Count -eq 0) { return 0.0 }
    $sum = 0.0
    foreach ($v in $arr) { $sum += [double]$v }
    return $sum / $arr.Count
}

function Percentile95($Values) {
    $arr = @($Values | Sort-Object)
    if ($arr.Count -eq 0) { return 0.0 }
    $idx = [Math]::Ceiling(0.95 * $arr.Count) - 1
    if ($idx -lt 0) { $idx = 0 }
    if ($idx -ge $arr.Count) { $idx = $arr.Count - 1 }
    return [double]$arr[$idx]
}

function ImprovePct($NewValue, $OldValue) {
    $old = N $OldValue
    if ($old -eq 0) { return 0.0 }
    return 100.0 * ($old - (N $NewValue)) / $old
}

$SummaryBy = @{}
foreach ($row in $Summary) { $SummaryBy[(Key $row.load_multiplier $row.architecture)] = $row }
$RunsBy = @{}
foreach ($row in $Runs) { $RunsBy[(Key $row.load_multiplier $row.architecture)] = $row }

$PatientGroups = @{}
foreach ($row in $Patients) {
    $k = Key $row.load_multiplier $row.architecture
    if (-not $PatientGroups.ContainsKey($k)) { $PatientGroups[$k] = New-Object System.Collections.ArrayList }
    [void]$PatientGroups[$k].Add($row)
}

$TimeMax = @{}
foreach ($row in $TimeSeries) {
    $k = Key $row.load_multiplier $row.architecture
    if (-not $TimeMax.ContainsKey($k)) {
        $TimeMax[$k] = [ordered]@{
            max_queue = 0
            max_critical_queue = 0
            max_inside_wing = 0
            plan_requests = 0
            path_skips = 0
        }
    }
    $m = $TimeMax[$k]
    $m.max_queue = [Math]::Max($m.max_queue, [int](N $row.queue_count))
    $m.max_critical_queue = [Math]::Max($m.max_critical_queue, [int](N $row.critical_queue_count))
    $m.max_inside_wing = [Math]::Max($m.max_inside_wing, [int](N $row.inside_wing))
    $m.plan_requests = [Math]::Max($m.plan_requests, [int](N $row.plan_requests))
    $m.path_skips = [Math]::Max($m.path_skips, [int](N $row.path_skips))
}

$Table1 = @(
    [pscustomobject]@{ Parameter = "Workload source"; Value = "MIMIC-demo-informed synthetic trace / stress-test schedule" }
    [pscustomobject]@{ Parameter = "Scheduled arrivals per run"; Value = "300 patients" }
    [pscustomobject]@{ Parameter = "Priority mix"; Value = "67 critical (22.3%), 233 normal (77.7%)" }
    [pscustomobject]@{ Parameter = "Base arrival window"; Value = "20 min at multiplier 1.00" }
    [pscustomobject]@{ Parameter = "Arrival time multipliers"; Value = "2.00, 1.50, 1.25, 1.00 (lower value = denser arrivals)" }
    [pscustomobject]@{ Parameter = "Assessment resources"; Value = "30 cubicles total: 15 right, 15 left" }
    [pscustomobject]@{ Parameter = "Triage/contact duration"; Value = "2-5 min, mean 206.23 s" }
    [pscustomobject]@{ Parameter = "Run stopping rule"; Value = "Until all scheduled patients reached home" }
    [pscustomobject]@{ Parameter = "Simulation acceleration"; Value = "Time.timeScale = 10" }
)

$Table2 = New-Object System.Collections.ArrayList
$Table3 = New-Object System.Collections.ArrayList
foreach ($load in $Loads) {
    foreach ($arch in $ArchOrder) {
        $k = Key $load $arch
        $r = $RunsBy[$k]
        $s = $SummaryBy[$k]
        $minutes = (N $r.simulation_duration_seconds) / 60.0
        $throughput = (N $r.completed_home) / ($minutes / 60.0)
        [void]$Table2.Add([pscustomobject]@{
            "Arrival multiplier" = $load.ToString("F2", [Globalization.CultureInfo]::InvariantCulture)
            "Controller" = $ArchLabel[$arch]
            "Completed patients" = $r.completed_home
            "Completion time (min)" = $minutes.ToString("F2", [Globalization.CultureInfo]::InvariantCulture)
            "Throughput (patients/hour)" = $throughput.ToString("F1", [Globalization.CultureInfo]::InvariantCulture)
            "Mean total time (min)" = F $s.avg_total_min 2
            "P95 total time (min)" = F $s.p95_total_min 2
            "World-state changes" = $r.world_counter
            "Max queue" = $TimeMax[$k].max_queue
        })

        $group = @($PatientGroups[$k])
        $normalTimes = @($group | Where-Object { $_.is_critical -eq "False" } | ForEach-Object { (N $_.time_to_triage_seconds) / 60.0 })
        [void]$Table3.Add([pscustomobject]@{
            "Arrival multiplier" = $load.ToString("F2", [Globalization.CultureInfo]::InvariantCulture)
            "Controller" = $ArchLabel[$arch]
            "Mean time to assessment (min)" = F $s.avg_time_to_triage_min 2
            "P95 time to assessment (min)" = F $s.p95_time_to_triage_min 2
            "Critical mean (min)" = F $s.avg_critical_time_to_triage_min 2
            "Critical P95 (min)" = F $s.p95_critical_time_to_triage_min 2
            "Normal mean (min)" = (Average $normalTimes).ToString("F2", [Globalization.CultureInfo]::InvariantCulture)
            "Normal P95 (min)" = (Percentile95 $normalTimes).ToString("F2", [Globalization.CultureInfo]::InvariantCulture)
            ">15 min all (%)" = F $s.time_to_triage_over_15min_pct 2
            ">15 min critical (%)" = F $s.critical_time_to_triage_over_15min_pct 2
            "Contact >5 min (%)" = F $s.contact_over_5min_pct 2
        })
    }
}

$Table4 = New-Object System.Collections.ArrayList
foreach ($load in $Loads) {
    $propS = $SummaryBy[(Key $load "ProposedEnvironmentMediatedReplanner")]
    $propR = $RunsBy[(Key $load "ProposedEnvironmentMediatedReplanner")]
    $bestCritAvg = $null
    $bestCritP95 = $null
    $bestFinish = $null
    foreach ($arch in $ArchOrder[0..2]) {
        $s = $SummaryBy[(Key $load $arch)]
        $r = $RunsBy[(Key $load $arch)]
        if ($null -eq $bestCritAvg -or (N $s.avg_critical_time_to_triage_min) -lt (N $bestCritAvg.s.avg_critical_time_to_triage_min)) {
            $bestCritAvg = @{ arch = $arch; s = $s; r = $r }
        }
        if ($null -eq $bestCritP95 -or (N $s.p95_critical_time_to_triage_min) -lt (N $bestCritP95.s.p95_critical_time_to_triage_min)) {
            $bestCritP95 = @{ arch = $arch; s = $s; r = $r }
        }
        if ($null -eq $bestFinish -or (N $r.simulation_duration_seconds) -lt (N $bestFinish.r.simulation_duration_seconds)) {
            $bestFinish = @{ arch = $arch; s = $s; r = $r }
        }
    }
    [void]$Table4.Add([pscustomobject]@{
        "Arrival multiplier" = $load.ToString("F2", [Globalization.CultureInfo]::InvariantCulture)
        "Best baseline critical mean" = $ArchLabel[$bestCritAvg.arch]
        "Critical mean: proposed vs best baseline (min)" = "$(F $propS.avg_critical_time_to_triage_min 2) vs $(F $bestCritAvg.s.avg_critical_time_to_triage_min 2)"
        "Critical mean improvement (%)" = (ImprovePct $propS.avg_critical_time_to_triage_min $bestCritAvg.s.avg_critical_time_to_triage_min).ToString("F1", [Globalization.CultureInfo]::InvariantCulture)
        "Best baseline critical P95" = $ArchLabel[$bestCritP95.arch]
        "Critical P95: proposed vs best baseline (min)" = "$(F $propS.p95_critical_time_to_triage_min 2) vs $(F $bestCritP95.s.p95_critical_time_to_triage_min 2)"
        "Critical P95 improvement (%)" = (ImprovePct $propS.p95_critical_time_to_triage_min $bestCritP95.s.p95_critical_time_to_triage_min).ToString("F1", [Globalization.CultureInfo]::InvariantCulture)
        "Best baseline finish" = $ArchLabel[$bestFinish.arch]
        "Finish time: proposed vs best baseline (min)" = "$(((N $propR.simulation_duration_seconds) / 60.0).ToString("F2", [Globalization.CultureInfo]::InvariantCulture)) vs $(((N $bestFinish.r.simulation_duration_seconds) / 60.0).ToString("F2", [Globalization.CultureInfo]::InvariantCulture))"
        "Finish-time improvement (%)" = (ImprovePct $propR.simulation_duration_seconds $bestFinish.r.simulation_duration_seconds).ToString("F1", [Globalization.CultureInfo]::InvariantCulture)
    })
}

$Table5 = New-Object System.Collections.ArrayList
foreach ($load in $Loads) {
    $loadLabel = $load.ToString("F2", [Globalization.CultureInfo]::InvariantCulture)
    $proposed = $Table3 | Where-Object { $_."Arrival multiplier" -eq $loadLabel -and $_.Controller -eq "Proposed GOAP" } | Select-Object -First 1
    $baselines = @($Table3 | Where-Object { $_."Arrival multiplier" -eq $loadLabel -and $_.Controller -ne "Proposed GOAP" })
    $baselineMean = Average ($baselines | ForEach-Object { N $_."Normal mean (min)" })
    $baselineP95 = Average ($baselines | ForEach-Object { N $_."Normal P95 (min)" })
    [void]$Table5.Add([pscustomobject]@{
        "Arrival multiplier" = $loadLabel
        "Proposed normal mean (min)" = $proposed."Normal mean (min)"
        "Baseline average normal mean (min)" = $baselineMean.ToString("F2", [Globalization.CultureInfo]::InvariantCulture)
        "Mean penalty vs baseline average (min)" = ((N $proposed."Normal mean (min)") - $baselineMean).ToString("F2", [Globalization.CultureInfo]::InvariantCulture)
        "Proposed normal P95 (min)" = $proposed."Normal P95 (min)"
        "Baseline average normal P95 (min)" = $baselineP95.ToString("F2", [Globalization.CultureInfo]::InvariantCulture)
        "P95 penalty vs baseline average (min)" = ((N $proposed."Normal P95 (min)") - $baselineP95).ToString("F2", [Globalization.CultureInfo]::InvariantCulture)
    })
}

$Table1 | Export-Csv (Join-Path $OutDir "table_1_workload_environment.csv") -NoTypeInformation -Encoding UTF8
$Table2 | Export-Csv (Join-Path $OutDir "table_2_completion_throughput.csv") -NoTypeInformation -Encoding UTF8
$Table3 | Export-Csv (Join-Path $OutDir "table_3_access_protocol_metrics.csv") -NoTypeInformation -Encoding UTF8
$Table4 | Export-Csv (Join-Path $OutDir "table_4_proposed_vs_best_baseline.csv") -NoTypeInformation -Encoding UTF8
$Table5 | Export-Csv (Join-Path $OutDir "table_5_normal_delay_penalty.csv") -NoTypeInformation -Encoding UTF8

function EscapeXml($Text) {
    return [System.Security.SecurityElement]::Escape([string]$Text)
}

function Write-LineChartSvg($FileName, $Title, $YLabel, $Series, $YMin = 0.0, $YMax = $null) {
    $YMin = [double]$YMin
    $W = 1000
    $H = 620
    $ML = 90
    $MR = 180
    $MT = 70
    $MB = 90
    $PlotW = $W - $ML - $MR
    $PlotH = $H - $MT - $MB
    $all = @()
    foreach ($s in $Series) { $all += $s.Values }
    if ($null -eq $YMax) {
        $maxVal = ($all | Measure-Object -Maximum).Maximum
        $YMax = [Math]::Ceiling(($maxVal * 1.12) * 10.0) / 10.0
    }
    $YMax = [double]$YMax
    $xStep = $PlotW / ($Loads.Count - 1)
    $svg = New-Object System.Collections.ArrayList
    [void]$svg.Add("<svg xmlns=""http://www.w3.org/2000/svg"" width=""$W"" height=""$H"" viewBox=""0 0 $W $H"">")
    [void]$svg.Add("<rect width=""100%"" height=""100%"" fill=""#FFFFFF""/>")
    [void]$svg.Add("<text x=""$($W/2)"" y=""34"" text-anchor=""middle"" font-family=""Arial, sans-serif"" font-size=""22"" font-weight=""700"" fill=""#111827"">$(EscapeXml $Title)</text>")
    [void]$svg.Add("<line x1=""$ML"" y1=""$($MT+$PlotH)"" x2=""$($ML+$PlotW)"" y2=""$($MT+$PlotH)"" stroke=""#111827"" stroke-width=""1.6""/>")
    [void]$svg.Add("<line x1=""$ML"" y1=""$MT"" x2=""$ML"" y2=""$($MT+$PlotH)"" stroke=""#111827"" stroke-width=""1.6""/>")
    for ($t = 0; $t -le 5; $t++) {
        $val = $YMin + (($YMax - $YMin) * $t / 5.0)
        $yy = $MT + (($YMax - $val) / ($YMax - $YMin) * $PlotH)
        $valText = ([double]$val).ToString("F1", [Globalization.CultureInfo]::InvariantCulture)
        [void]$svg.Add("<line x1=""$ML"" y1=""$yy"" x2=""$($ML+$PlotW)"" y2=""$yy"" stroke=""#E5E7EB"" stroke-width=""1""/>")
        [void]$svg.Add("<text x=""$($ML-12)"" y=""$($yy+4)"" text-anchor=""end"" font-family=""Arial, sans-serif"" font-size=""12"" fill=""#374151"">$valText</text>")
    }
    for ($i = 0; $i -lt $Loads.Count; $i++) {
        $xx = $ML + ($i * $xStep)
        $loadText = ([double]$Loads[$i]).ToString("F2", [Globalization.CultureInfo]::InvariantCulture)
        [void]$svg.Add("<line x1=""$xx"" y1=""$($MT+$PlotH)"" x2=""$xx"" y2=""$($MT+$PlotH+6)"" stroke=""#111827""/>")
        [void]$svg.Add("<text x=""$xx"" y=""$($MT+$PlotH+26)"" text-anchor=""middle"" font-family=""Arial, sans-serif"" font-size=""13"" fill=""#111827"">$loadText</text>")
    }
    [void]$svg.Add("<text x=""$($ML+$PlotW/2)"" y=""$($H-24)"" text-anchor=""middle"" font-family=""Arial, sans-serif"" font-size=""14"" fill=""#111827"">Arrival time multiplier (lower = higher arrival density)</text>")
    [void]$svg.Add("<text x=""24"" y=""$($MT+$PlotH/2)"" text-anchor=""middle"" font-family=""Arial, sans-serif"" font-size=""14"" fill=""#111827"" transform=""rotate(-90 24 $($MT+$PlotH/2))"">$(EscapeXml $YLabel)</text>")
    foreach ($s in $Series) {
        $pts = New-Object System.Collections.ArrayList
        for ($i = 0; $i -lt $s.Values.Count; $i++) {
            $xx = $ML + ($i * $xStep)
            $yy = $MT + (($YMax - [double]$s.Values[$i]) / ($YMax - $YMin) * $PlotH)
            [void]$pts.Add("$xx,$yy")
        }
        [void]$svg.Add("<polyline points=""$($pts -join ' ')"" fill=""none"" stroke=""$($s.Color)"" stroke-width=""3""/>")
        for ($i = 0; $i -lt $s.Values.Count; $i++) {
            $xx = $ML + ($i * $xStep)
            $yy = $MT + (($YMax - [double]$s.Values[$i]) / ($YMax - $YMin) * $PlotH)
            [void]$svg.Add("<circle cx=""$xx"" cy=""$yy"" r=""5"" fill=""#FFFFFF"" stroke=""$($s.Color)"" stroke-width=""3""/>")
            [void]$svg.Add("<text x=""$xx"" y=""$($yy-10)"" text-anchor=""middle"" font-family=""Arial, sans-serif"" font-size=""11"" fill=""$($s.Color)"">$(([double]$s.Values[$i]).ToString("F1", [Globalization.CultureInfo]::InvariantCulture))</text>")
        }
    }
    $ly = $MT + 20
    foreach ($s in $Series) {
        [void]$svg.Add("<line x1=""$($ML+$PlotW+35)"" y1=""$ly"" x2=""$($ML+$PlotW+65)"" y2=""$ly"" stroke=""$($s.Color)"" stroke-width=""3""/>")
        [void]$svg.Add("<circle cx=""$($ML+$PlotW+50)"" cy=""$ly"" r=""4"" fill=""#FFFFFF"" stroke=""$($s.Color)"" stroke-width=""2""/>")
        [void]$svg.Add("<text x=""$($ML+$PlotW+75)"" y=""$($ly+4)"" font-family=""Arial, sans-serif"" font-size=""13"" fill=""#111827"">$(EscapeXml $s.Name)</text>")
        $ly += 24
    }
    [void]$svg.Add("</svg>")
    Set-Content -LiteralPath (Join-Path $OutDir $FileName) -Value ($svg -join "`n") -Encoding UTF8
}

function SeriesFor($MetricName, $Source) {
    $series = New-Object System.Collections.ArrayList
    foreach ($arch in $ArchOrder) {
        $values = New-Object System.Collections.ArrayList
        foreach ($load in $Loads) {
            $k = Key $load $arch
            if ($Source -eq "runs") {
                $row = $RunsBy[$k]
                if ($MetricName -eq "completion_min") { [void]$values.Add((N $row.simulation_duration_seconds) / 60.0) }
            }
            else {
                $row = $SummaryBy[$k]
                if ($MetricName -eq "critical_p95") { [void]$values.Add((N $row.p95_critical_time_to_triage_min)) }
                if ($MetricName -eq "breach_all") { [void]$values.Add((N $row.time_to_triage_over_15min_pct)) }
            }
        }
        [void]$series.Add([pscustomobject]@{ Name = $ArchLabel[$arch]; Color = $Colors[$arch]; Values = @($values) })
    }
    return @($series)
}

Write-LineChartSvg "figure_1_completion_time.svg" "Completion Time by Workload Density" "Completion time (min)" (SeriesFor "completion_min" "runs")
Write-LineChartSvg "figure_2_critical_p95_time_to_assessment.svg" "Critical Patient Time to Assessment" "Critical P95 time to assessment (min)" (SeriesFor "critical_p95" "summary")
Write-LineChartSvg "figure_3_protocol_breach_rate.svg" "Protocol Breaches: Time to Assessment > 15 min" "Patients over threshold (%)" (SeriesFor "breach_all" "summary")

$PenaltyMean = New-Object System.Collections.ArrayList
$PenaltyP95 = New-Object System.Collections.ArrayList
foreach ($row in $Table5) {
    [void]$PenaltyMean.Add((N $row."Mean penalty vs baseline average (min)"))
    [void]$PenaltyP95.Add((N $row."P95 penalty vs baseline average (min)"))
}
Write-LineChartSvg "figure_4_normal_delay_penalty.svg" "Normal Patient Delay Penalty of Proposed Controller" "Penalty vs baseline average (min)" @(
    [pscustomobject]@{ Name = "Mean normal delay penalty"; Color = "#DC2626"; Values = @($PenaltyMean) }
    [pscustomobject]@{ Name = "P95 normal delay penalty"; Color = "#F97316"; Values = @($PenaltyP95) }
) -YMin -1.0

function To-MdTable($Rows) {
    $arr = @($Rows)
    if ($arr.Count -eq 0) { return "" }
    $headers = @($arr[0].PSObject.Properties | ForEach-Object { $_.Name })
    $lines = New-Object System.Collections.ArrayList
    [void]$lines.Add("| " + ($headers -join " | ") + " |")
    [void]$lines.Add("| " + (($headers | ForEach-Object { "---" }) -join " | ") + " |")
    foreach ($row in $arr) {
        [void]$lines.Add("| " + (($headers | ForEach-Object { ([string]$row.$_).Replace("|", "/") }) -join " | ") + " |")
    }
    return ($lines -join "`n")
}

$TablesMd = @"
# Results Tables

## Table 1. Experimental Workload and Environment

$(To-MdTable $Table1)

## Table 2. Completion Time and Throughput

$(To-MdTable $Table2)

## Table 3. Access Time and Protocol Metrics

$(To-MdTable $Table3)

## Table 4. Proposed Controller vs Best Baseline

$(To-MdTable $Table4)

## Table 5. Normal-Patient Delay Penalty

$(To-MdTable $Table5)
"@
Set-Content -LiteralPath (Join-Path $OutDir "results_tables.md") -Value $TablesMd -Encoding UTF8

$Captions = @"
# Results Figures and Captions

## Figure 1
![Completion time](figure_1_completion_time.svg)

**Caption.** Total simulated time required for each controller to complete all 300 scheduled arrivals. The arrival multiplier stretches the same arrival trace; therefore, lower values correspond to denser workload conditions.

## Figure 2
![Critical P95](figure_2_critical_p95_time_to_assessment.svg)

**Caption.** P95 time to assessment for critical patients. Under the densest workload (arrival multiplier 1.00), the proposed environment-mediated GOAP controller produced the lowest critical tail latency.

## Figure 3
![Protocol breaches](figure_3_protocol_breach_rate.svg)

**Caption.** Percentage of patients whose estimated time to assessment exceeded the 15 min threshold. No critical patient exceeded the threshold in any run; breaches were caused by normal-priority patients.

## Figure 4
![Normal delay penalty](figure_4_normal_delay_penalty.svg)

**Caption.** Additional waiting cost imposed on normal patients by the proposed controller relative to the average of the three baselines. Positive values indicate delayed normal patients; negative values indicate lower waiting time than the baseline average.
"@
Set-Content -LiteralPath (Join-Path $OutDir "figure_captions.md") -Value $Captions -Encoding UTF8

$Narrative = @"
# Results Narrative Draft

## Main Finding

All four controllers completed all 300 scheduled patients in every workload condition. The proposed environment-mediated GOAP controller showed a load-dependent effect. Under the densest workload (arrival multiplier 1.00), it achieved the lowest total completion time and the lowest critical-patient time to assessment. Under less dense workloads, the proposed controller introduced additional switching and replanning overhead, which increased completion time relative to the simpler baselines.

## Critical Patients

The strongest result appears in the densest workload. At arrival multiplier 1.00, the proposed controller reduced mean critical time to assessment to 1.35 min, compared with 1.55 min for the best baseline. Critical P95 time to assessment was also reduced to 2.61 min, compared with 3.24 min for the best baseline. No critical patient exceeded the 15 min time-to-assessment threshold in any controller or workload condition.

## Overall Throughput

At the densest workload, the proposed controller completed the full cohort in 50.81 min, slightly faster than the best baseline, Priority Queue Dispatcher, at 50.83 min. This difference is small, but it is important because the critical-patient benefit did not require a measurable loss of total throughput in the highest-density condition. In lower-density workloads, however, the proposed controller was slower, indicating that environment-mediated switching is most useful when contention is high enough to justify replanning overhead.

## Normal-Patient Delay

The prioritization mechanism delayed normal patients under moderate and low-density workloads. Compared with the average baseline normal-patient time to assessment, the proposed controller added approximately 1-2 min of mean normal delay at arrival multipliers 2.00, 1.50, and 1.25. At the densest workload, this penalty disappeared: normal-patient mean time to assessment was 0.66 min lower than the baseline average. This suggests that the proposed controller is not simply prioritizing critical patients at the expense of normal patients; at high contention, it can also reduce global blocking effects.

## Protocol Compliance

The service/contact duration was constrained to the 2-5 min triage contact window, and no run produced a contact-time violation. Time-to-assessment violations above 15 min occurred only among normal patients. At the densest workload, the proposed controller had the lowest overall 15 min breach rate (40.00%) among the four controllers.

## Interpretation

The results support a transport-control interpretation of the method. The main benefit of the proposed approach is not that it makes every route faster. Instead, it changes the environment so that critical agents can access short paths and resources when the system becomes congested. The same conditional access rules may force normal agents to wait or take detours, but this trade-off becomes beneficial under dense arrival conditions because it reduces conflicts around shared corridors, wing access, and cubicle assignment.

## Reporting Limitation

These results are based on a single trace-driven comparative batch rather than repeated independent seeds. For final statistical reporting, the same workload generation procedure should be repeated with multiple random seeds and reported as mean +/- standard deviation or confidence intervals. The workload should be described as MIMIC-demo-informed / trace-inspired synthetic stress testing, not as a direct replay of a representative real hospital day.
"@
Set-Content -LiteralPath (Join-Path $OutDir "results_narrative_draft.md") -Value $Narrative -Encoding UTF8

$Readme = @"
# Paper Results Package

Generated from latest complete batch:

- $RunDir
- $AnalysisDir

Recommended manuscript files:

- results_tables.md
- results_narrative_draft.md
- figure_captions.md
- figure_1_completion_time.svg
- figure_2_critical_p95_time_to_assessment.svg
- figure_3_protocol_breach_rate.svg
- figure_4_normal_delay_penalty.svg

CSV tables are provided for spreadsheet/Word import.
"@
Set-Content -LiteralPath (Join-Path $OutDir "README.md") -Value $Readme -Encoding UTF8

Write-Output "Generated results package: $OutDir"
