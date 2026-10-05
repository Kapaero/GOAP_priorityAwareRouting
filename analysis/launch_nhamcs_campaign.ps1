# Launch the NHAMCS campaign as detached shards (disjoint seed ranges) of the headless player.
# Every shard is deterministic (fixed step, -job-worker-count 0) and writes its own output directory
# (label + timestamp + process id) under <persistentDataPath>/GOAP_Diagnostics/Experiments.
# Usage: powershell -File launch_nhamcs_campaign.ps1 [-Seeds 16] [-Shards 16] [-BuildDir HeadlessTriageNhamcsMain]
param(
    [int]$Seeds = 16,
    [int]$Shards = 16,
    [string]$BuildDir = "HeadlessTriageNhamcsMain"
)
$exe = Join-Path "C:\UnityProgects\GOAP_5Attempt\Builds" (Join-Path $BuildDir "HeadlessTriage.exe")
if (-not (Test-Path $exe)) { throw "player not found: $exe" }
$workDir = Split-Path $exe
$logDir = Join-Path $workDir "logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$per = [int][math]::Ceiling($Seeds / $Shards)
for ($i = 0; $i -lt $Shards; $i++) {
    $a = $i * $per
    $b = [math]::Min($Seeds, $a + $per)
    if ($a -ge $Seeds) { break }
    $last = $b - 1
    $argList = @("-batchmode", "-nographics", "-job-worker-count", "0",
                 "-seedRange", "$a", "$b",
                 "-logFile", (Join-Path $logDir "shard_${a}-${last}.log"))
    Start-Process -FilePath $exe -ArgumentList $argList -WorkingDirectory $workDir -WindowStyle Hidden
    "started shard seeds $a..$last"
    Start-Sleep -Seconds 2
}
