param(
    [string] $ResultsDirectory,
    [ValidateRange(1, 20)] [int] $Rounds = 3,
    [ValidateRange(1, 64)] [int] $LogicalCpus = 4,
    [ValidateRange(1, 64)] [int] $LoadThreads = 2
)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'この検証はWindowsで実行してください。' }
$repo = Split-Path -Parent $PSScriptRoot
$drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($repo))
if ($drive.DriveFormat -ne 'NTFS') { throw 'NTFS上のチェックアウトで実行してください。' }
if (!$ResultsDirectory) {
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
    $ResultsDirectory = Join-Path ([IO.Path]::GetTempPath()) "HWEncVppPreview-stress-$stamp-$PID"
}
$ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)

Add-Type @"
using System;
using System.Threading;

public sealed class PreviewTestCpuLoad : IDisposable
{
    private volatile bool stopped;
    private readonly Thread[] threads;

    public PreviewTestCpuLoad(int count)
    {
        threads = new Thread[count];
        for (int i = 0; i < count; ++i)
        {
            threads[i] = new Thread(() => {
                while (!stopped) Thread.SpinWait(8192);
            });
            threads[i].IsBackground = true;
            threads[i].Start();
        }
    }

    public void Dispose()
    {
        stopped = true;
        foreach (Thread thread in threads) thread.Join();
    }
}
"@

$process = [Diagnostics.Process]::GetCurrentProcess()
$originalAffinity = $process.ProcessorAffinity
$affinity = [long]0
$available = 0
for ($bit = 0; $bit -lt ([IntPtr]::Size * 8) -and $available -lt $LogicalCpus; $bit++) {
    $flag = [long]1 -shl $bit
    if ($flag -band $originalAffinity.ToInt64()) {
        $affinity = $affinity -bor $flag
        $available++
    }
}
if ($available -lt $LogicalCpus) { throw "利用可能な論理CPUが$LogicalCpus個未満です。" }
$null = New-Item -Path $ResultsDirectory -ItemType Directory -Force

Push-Location $repo
$load = $null
try {
    dotnet build HWEncVppPreview.sln -c Debug
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $process.ProcessorAffinity = [IntPtr]$affinity
    [Console]::WriteLine("論理CPU $LogicalCpus、負荷スレッド $LoadThreads、結果保存先 $ResultsDirectory")
    for ($round = 1; $round -le $Rounds; $round++) {
        $directory = Join-Path $ResultsDirectory "round-$round"
        if ([IO.Directory]::Exists($directory)) { throw "結果ディレクトリが既に存在します。$directory" }
        $null = [IO.Directory]::CreateDirectory($directory)
        [Console]::WriteLine("第${round}回の全件試験を開始します。")
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $load = [PreviewTestCpuLoad]::new($LoadThreads)
        try {
            dotnet test HWEncVppPreview.sln -c Debug --no-build --results-directory $directory --logger "trx;LogFilePrefix=round-$round" --blame-hang-timeout 5m
            $testExit = $LASTEXITCODE
        }
        finally {
            $load.Dispose()
            $load = $null
        }
        $watch.Stop()
        [Console]::WriteLine(("第{0}回 {1:F1}秒 終了コード {2}" -f $round, $watch.Elapsed.TotalSeconds, $testExit))
        if ($testExit -ne 0) { exit $testExit }
        $results = [IO.Directory]::GetFiles($directory, '*.trx')
        if ($results.Length -lt 2) { throw "第${round}回の全件試験結果がそろっていません。" }
        foreach ($file in $results) {
            $reader = [Xml.XmlReader]::Create($file)
            try {
                $hasCounters = $reader.ReadToFollowing('Counters', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
                if (!$hasCounters -or [int]$reader.GetAttribute('executed') -eq 0 -or [int]$reader.GetAttribute('failed') -ne 0) {
                    throw "検証に使える全件試験結果がありません。$file"
                }
            }
            finally { $reader.Dispose() }
        }
    }
}
finally {
    if ($null -ne $load) { $load.Dispose() }
    $process.ProcessorAffinity = $originalAffinity
    Pop-Location
}
