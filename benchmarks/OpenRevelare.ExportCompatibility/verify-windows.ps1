param([Parameter(Mandatory=$true)][string]$Directory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase
$results = foreach ($fixture in (Get-Content -LiteralPath (Join-Path $Directory 'manifest.json') -Raw | ConvertFrom-Json)) {
    $path = (Resolve-Path -LiteralPath (Join-Path $Directory $fixture.Name)).Path
    try {
        $stream = [System.IO.File]::OpenRead($path)
        try {
            $decoder = [System.Windows.Media.Imaging.BitmapDecoder]::Create($stream,
                [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
                [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
            $frame = $decoder.Frames[0]
            $converted = [System.Windows.Media.Imaging.FormatConvertedBitmap]::new($frame,
                [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
            $stride = $frame.PixelWidth * 4
            $pixels = [byte[]]::new($stride * $frame.PixelHeight)
            $converted.CopyPixels($pixels, $stride, 0)
            [pscustomobject]@{ Name=$fixture.Name; Decoded=$true; Width=$frame.PixelWidth;
                Height=$frame.PixelHeight; NativeFormat=$frame.Format.ToString();
                FullResolution=($frame.PixelWidth -eq $fixture.Width -and $frame.PixelHeight -eq $fixture.Height) }
        } finally { $stream.Dispose() }
    } catch {
        [pscustomobject]@{ Name=$fixture.Name; Decoded=$false; Error=$_.Exception.Message }
    }
}
$results | ConvertTo-Json -Depth 4
