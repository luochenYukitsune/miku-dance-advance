# Generate an MSBuild source override in the supplied experiment directory.
# Pass the resulting path as -p:CustomAfterMicrosoftCommonTargets=<path> to an isolated build.
param([Parameter(Mandatory)][string]$Directory, [switch]$BatchPipe, [switch]$TraceBullet)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
if (Test-Path -LiteralPath $Directory) { throw 'Choose a new directory for the overlay.' }
$out = (New-Item -ItemType Directory -Path $Directory).FullName

function Replace-Once([string]$Text, [string]$Old, [string]$New) {
    $Old = $Old.Replace("`r`n", "`n")
    if (!$Text.Contains($Old) -or $Text.IndexOf($Old) -ne $Text.LastIndexOf($Old)) {
        throw 'The source no longer matches this experiment.'
    }
    $Text.Replace($Old, $New.Replace("`r`n", "`n"))
}
function Read-Source([string]$Name) {
    [IO.File]::ReadAllText((Join-Path $repo $Name)).Replace("`r`n", "`n")
}
$runtime = Read-Source 'src/Mmd.Modern.Rendering.D3D12/D3D12GpuRuntime.cs'
$poll = '        while(fence.CompletedValue<value)'
$wait = @'
        if (!cancellationToken.CanBeCanceled && fence.CompletedValue < value)
            fence.SetEventOnCompletion(value, IntPtr.Zero).CheckError();
'@
[IO.File]::WriteAllText((Join-Path $out 'D3D12GpuRuntime.cs'), (Replace-Once $runtime $poll ($wait + "`n" + $poll)))
$overrides = @'
  <ItemGroup Condition="'$(MSBuildProjectName)' == 'Mmd.Modern.Rendering.D3D12'">
    <Compile Remove="D3D12GpuRuntime.cs" />
    <Compile Include="$(MSBuildThisFileDirectory)D3D12GpuRuntime.cs" />
  </ItemGroup>
'@
if ($BatchPipe) {
    $sink = Read-Source 'src/Mmd.Modern.ViewOutput.FFmpeg/FfmpegFrameSink.cs'
    $sink = Replace-Once $sink 'byte[] row = ArrayPool<byte>.Shared.Rent(checked(frame.Width * 4));' @'
const int rowsPerWrite = 32;
        int rowBytes = checked(frame.Width * 4);
        byte[] row = ArrayPool<byte>.Shared.Rent(checked(Math.Min(rowsPerWrite, frame.Height) * rowBytes));
'@
    $sink = Replace-Once $sink 'for (int y = 0; y < frame.Height; y++)' 'for (int y = 0; y < frame.Height; y += rowsPerWrite)'
    $oldLoop = @'
                CompositeRow(frame.Pixels.Span.Slice(y * frame.Stride, frame.Width * 4), row.AsSpan(0, frame.Width * 4), options);
                await process.Input.WriteAsync(row.AsMemory(0, frame.Width * 4), timeout.Token).ConfigureAwait(false);
'@
    $newLoop = @'
                int count = Math.Min(rowsPerWrite, frame.Height - y);
                for (int i = 0; i < count; i++)
                    CompositeRow(frame.Pixels.Span.Slice((y + i) * frame.Stride, rowBytes),
                        row.AsSpan(i * rowBytes, rowBytes), options);
                await process.Input.WriteAsync(row.AsMemory(0, count * rowBytes), timeout.Token).ConfigureAwait(false);
'@
    [IO.File]::WriteAllText((Join-Path $out 'FfmpegFrameSink.cs'), (Replace-Once $sink $oldLoop $newLoop))
    $overrides += @'

  <ItemGroup Condition="'$(MSBuildProjectName)' == 'Mmd.Modern.ViewOutput.FFmpeg'">
    <Compile Remove="FfmpegFrameSink.cs" />
    <Compile Include="$(MSBuildThisFileDirectory)FfmpegFrameSink.cs" />
  </ItemGroup>
'@
}
if ($TraceBullet) {
    $bullet = Read-Source 'src/Mmd.Modern.Physics.Bullet/BulletCharacterPhysicsBackend.cs'
    $call = '        Check(Native.Step(current, stepSeconds), "step world");'
    $timed = @'
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        int status = Native.Step(current, stepSeconds);
        long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - start;
        NativeStepEvents.Log.Step(ticks);
        Check(status, "step world");
'@
    $bullet = Replace-Once $bullet $call $timed
    $bullet += @'

[System.Diagnostics.Tracing.EventSource(Name = "FrameStudy-NativeBullet")]
internal sealed class NativeStepEvents : System.Diagnostics.Tracing.EventSource
{
    internal static readonly NativeStepEvents Log = new();
    [System.Diagnostics.Tracing.Event(1)]
    public void Step(long ticks) => WriteEvent(1, ticks);
}
'@
    [IO.File]::WriteAllText((Join-Path $out 'BulletCharacterPhysicsBackend.cs'), $bullet)
    $overrides += @'

  <ItemGroup Condition="'$(MSBuildProjectName)' == 'Mmd.Modern.Physics.Bullet'">
    <Compile Remove="BulletCharacterPhysicsBackend.cs" />
    <Compile Include="$(MSBuildThisFileDirectory)BulletCharacterPhysicsBackend.cs" />
  </ItemGroup>
'@
}
$targets = Join-Path $out 'variant.targets'
[IO.File]::WriteAllText($targets, "<Project>`n$overrides`n</Project>")
$targets
