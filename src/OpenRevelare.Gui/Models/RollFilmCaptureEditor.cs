using OpenRevelare.Core;

namespace OpenRevelare.Gui.Models;

/// <summary>Film metadata follows export order, including virtual copies of one source.</summary>
public sealed class RollFilmCaptureEditor(int count)
{
    private readonly FilmCaptureMetadata[] _frames = Enumerable.Range(0, count).Select(_ => new FilmCaptureMetadata()).ToArray();
    private readonly bool[] _cameraSync = Enumerable.Repeat(true, count).ToArray();
    private readonly bool[] _lensSync = Enumerable.Repeat(true, count).ToArray();
    private CaptureDevice _camera = new();
    private CaptureDevice _lens = new();
    public FilmCaptureMetadata Get(int index) => _frames[index];
    public bool CameraSync(int index) => _cameraSync[index];
    public bool LensSync(int index) => _lensSync[index];
    public void Update(int index, FilmCaptureMetadata film)
    {
        _frames[index] = film;
        if (_cameraSync[index]) _camera = film.Camera;
        if (_lensSync[index]) _lens = film.Lens;
        for (int i = 0; i < _frames.Length; i++)
            _frames[i] = _frames[i] with
            {
                Camera = _cameraSync[i] ? _camera : _frames[i].Camera,
                Lens = _lensSync[i] ? _lens : _frames[i].Lens,
            };
    }
    public void SetSync(int index, bool camera, bool lens)
    {
        _cameraSync[index] = camera;
        _lensSync[index] = lens;
        _frames[index] = _frames[index] with
        {
            Camera = camera ? _camera : _frames[index].Camera,
            Lens = lens ? _lens : _frames[index].Lens,
        };
    }
    public FilmCaptureMetadata[] Snapshot() => _frames.ToArray();
}
