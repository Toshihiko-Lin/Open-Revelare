using OpenRevelare.Core;

namespace OpenRevelare.Gui.Services;

internal sealed record ImportLensPreviewResult(LensCaptureInfo Capture,
    LensfunIdentificationDatabase Database, LensfunIdentification Identification);

/// <summary>Cache by first file, never by the entire selection. Appending a roll costs no reads.</summary>
internal sealed class ImportLensPreview
{
    private readonly Func<string, Task<ImportLensPreviewResult?>> _read;
    private string? _path;
    public Task<ImportLensPreviewResult?>? Current { get; private set; }

    public ImportLensPreview(Func<string, Task<ImportLensPreviewResult?>>? read = null)
        => _read = read ?? (path => Task.Run(() =>
        {
            if (!RawDecode.IsRawExtension(path)) return null;
            var capture = LensCaptureInfo.Read(path);
            var database = LensfunIdentificationDatabase.Bundled.Value;
            return new ImportLensPreviewResult(capture, database, database.Identify(capture));
        }));

    public Task<ImportLensPreviewResult?> Get(string? firstPath)
    {
        if (firstPath == _path && Current is not null) return Current;
        _path = firstPath;
        return Current = firstPath is null ? Task.FromResult<ImportLensPreviewResult?>(null) : _read(firstPath);
    }
}
