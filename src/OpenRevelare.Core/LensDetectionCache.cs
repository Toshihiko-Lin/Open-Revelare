using System.Collections.Concurrent;
using MetadataExtractor;
namespace OpenRevelare.Core;

/// <summary>Bounded shared detection cache. Both priority layers consume one metadata snapshot.</summary>
public static class LensDetectionCache
{
    public sealed record Result(LensCorrectionEstimate? Embedded, LensfunCalibration? Lensfun, string Reason);
    private static readonly ConcurrentDictionary<(string Path,long Length,long Ticks),Lazy<Result>> Entries = new();
    public static Result Get(string path)
    {
        try
        {
            var info=new FileInfo(path); var key=(info.FullName,info.Length,info.LastWriteTimeUtc.Ticks);
            if(Entries.Count>=128) Entries.Clear();
            return Entries.GetOrAdd(key,_=>new Lazy<Result>(()=>Read(path))).Value;
        }
        catch { return Failure(null,null,"元数据无法读取"); }
    }
    private static Result Failure(LensCorrectionEstimate? e,LensfunCalibration? c,string reason)
    {
        e ??= new LensCorrectionEstimate("",0,0,0,0,2.5,reason) { HasDistortion=false, HasVignette=false };
        return new(e with { Diagnostic=reason },c,reason);
    }
    private static Result Read(string path)
    {
        try
        {
            var directories=ImageMetadataReader.ReadMetadata(path);
            var embedded=LensMetadata.EstimateCorrections(directories);
            if(AutoLensCorrection.IsUsable(embedded)) return new(embedded,null,"");
            bool present=directories.Any(d=>d.ContainsTag(0xc74e)||d.ContainsTag(0x7037)||d.ContainsTag(0x7032));
            var database=LensfunIdentificationDatabase.Bundled.Value;
            var capture=LensCaptureInfo.Read(directories); var match=database.Identify(capture);
            var calibration=match.Profile is {} profile ? database.CreateCalibration(profile,capture) : null;
            return Failure(embedded,calibration,present?"内嵌校正数据无效或暂不支持":"未检测到内嵌校正数据");
        }
        catch { return Failure(null,null,"元数据无法读取"); }
    }
}
