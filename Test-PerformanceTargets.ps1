[CmdletBinding()]
param(
	[string]$ResultsDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'BenchmarkDotNet.Artifacts\results')
)

$ErrorActionPreference = 'Stop'

$targets = @(
	[pscustomobject]@{ BenchmarkClass = 'PerformanceCI.Benchmarks.PortableBenchmarks'; Benchmark = 'PortableBenchmarks'; Method = 'CachePrediction'; MaximumNanoseconds = 1000; MaximumAllocatedBytes = 512; FilePattern = '*PortableBenchmarks*-report.csv'; Job = $null }
	[pscustomobject]@{ BenchmarkClass = 'PerformanceCI.Benchmarks.PortableBenchmarks'; Benchmark = 'PortableBenchmarks'; Method = 'NormalizePhrase'; MaximumNanoseconds = 1000; MaximumAllocatedBytes = 512; FilePattern = '*PortableBenchmarks*-report.csv'; Job = $null }
	[pscustomobject]@{ BenchmarkClass = 'PerformanceCI.Integration.SqliteVocabularyBenchmarks'; Benchmark = 'SqliteVocabularyBenchmarks'; Method = 'ReadByCategory'; MaximumNanoseconds = 100000; MaximumAllocatedBytes = 4096; FilePattern = '*SqliteVocabularyBenchmarks*-report.csv'; Job = $null }
	[pscustomobject]@{ BenchmarkClass = 'PerformanceCI.Integration.SqliteVocabularyBenchmarks'; Benchmark = 'SqliteVocabularyBenchmarks'; Method = 'InsertTile'; MaximumMilliseconds = 250; MaximumAllocatedBytes = 20MB; FilePattern = '*SqliteVocabularyBenchmarks*-report.csv'; Job = $null }
)

function ConvertTo-Nanoseconds([string]$value) {
	if ([string]::IsNullOrWhiteSpace($value) -or $value -eq 'NA') { return $null }
	$normalized = $value.Replace(',', '').Trim()
	if ($normalized -match '^([0-9.]+)\s*ns$') { return [double]$Matches[1] }
	if ($normalized -match '^([0-9.]+)\s*us$') { return [double]$Matches[1] * 1000 }
	if ($normalized -match '^([0-9.]+)\s*ms$') { return [double]$Matches[1] * 1000000 }
	if ($normalized -match '^([0-9.]+)\s*s$') { return [double]$Matches[1] * 1000000000 }
	throw "Unsupported BenchmarkDotNet time value: $value"
}

$failures = [System.Collections.Generic.List[string]]::new()
$checked = 0

foreach ($target in $targets) {
	$csvFile = Get-ChildItem -Path $ResultsDirectory -Filter $target.FilePattern -File -ErrorAction SilentlyContinue |
		Sort-Object LastWriteTime -Descending |
		Select-Object -First 1
	if (-not $csvFile) {
		$failures.Add("Missing results for $($target.Benchmark).$($target.Method) ($($target.FilePattern)).")
		continue
	}

	$rows = Import-Csv $csvFile.FullName | Where-Object {
		$_.Method -eq $target.Method -and ($null -eq $target.Job -or $_.Job -eq $target.Job) -and $_.Mean -ne 'NA'
	}
	if ($rows.Count -eq 0) {
		$failures.Add("No valid measured row for $($target.Benchmark).$($target.Method).")
		continue
	}

	$row = $rows | Select-Object -First 1
	$meanNanoseconds = ConvertTo-Nanoseconds $row.Mean
	$allocatedBytes = [double]($row.Allocated -replace '[^0-9.]', '')
	$maximumNanoseconds = if ($target.MaximumMilliseconds) { $target.MaximumMilliseconds * 1000000 } else { $target.MaximumNanoseconds }
	$checked++

	Write-Host ("{0}.{1}: {2} ns, {3} B allocated (targets: <= {4} ns, <= {5} B)" -f $target.Benchmark, $target.Method, [math]::Round($meanNanoseconds, 2), [math]::Round($allocatedBytes, 2), $maximumNanoseconds, $target.MaximumAllocatedBytes)
	if ($meanNanoseconds -gt $maximumNanoseconds) { $failures.Add("$($target.Benchmark).$($target.Method) exceeded time target: $meanNanoseconds ns > $maximumNanoseconds ns.") }
	if ($allocatedBytes -gt $target.MaximumAllocatedBytes) { $failures.Add("$($target.Benchmark).$($target.Method) exceeded allocation target: $allocatedBytes B > $($target.MaximumAllocatedBytes) B.") }
}

if ($checked -eq 0 -or $failures.Count -gt 0) {
	$failures | ForEach-Object { Write-Error $_ }
	exit 1
}

Write-Host "Performance targets passed for $checked benchmark areas."