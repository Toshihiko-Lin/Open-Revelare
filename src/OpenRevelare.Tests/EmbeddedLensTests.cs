using System.Buffers.Binary;
using MetadataExtractor.Formats.Exif;
using OpenRevelare.Core;
using Xunit;
namespace OpenRevelare.Tests;

public sealed class EmbeddedLensTests
{
    private static byte[] Dng(double[][]? planes=null, double[]? vignette=null, double cx=.5, double cy=.5)
    {
        using var stream=new MemoryStream();
        void U(uint v) { var b=new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b,v); stream.Write(b); }
        byte[] Doubles(IEnumerable<double> values) { using var bytes=new MemoryStream(); foreach(var v in values) { var b=new byte[8]; BinaryPrimitives.WriteDoubleBigEndian(b,v); bytes.Write(b); } return bytes.ToArray(); }
        U((uint)((planes is null?0:1)+(vignette is null?0:1)));
        if(planes is not null) {
            U(1); U(0x01040000); U(0); U((uint)(4+planes.Length*48+16)); U((uint)planes.Length);
            stream.Write(Doubles(planes.SelectMany(p=>p).Concat(new[]{cx,cy})));
        }
        if(vignette is not null) { U(3); U(0x01040000); U(0); U(56); stream.Write(Doubles(vignette.Concat(new[]{cx,cy}))); }
        return stream.ToArray();
    }

    [Fact]
    public void NikonLikeDngRetainsNonUnitScaleHigherTermsOpticalCenterAndSeparatePlanes()
    {
        double[][] planes = [[.999726,.000048,.00001,0,.000002,0], [1,.00005,0,0,0,0], [1.0001,.00006,0,0,0,0]];
        var bytes=Dng(planes,cx:.48,cy:.52);
        Assert.True(EmbeddedLensCalibration.TryParseDng(bytes,out var c));
        Assert.NotNull(c); Assert.Equal(planes[0],c.Warp![0]); Assert.Equal(planes[2],c.Warp[2]);
        Assert.Equal(.48,c.CenterX); Assert.Equal(.52,c.CenterY);
        var dir=new ExifSubIfdDirectory(); dir.Set(0xc74e,bytes);
        Assert.NotNull(LensMetadata.EstimateCorrections([dir])!.Native);
        Assert.NotEqual(c.Map(80,50,0,101,101),c.Map(80,50,2,101,101));
    }

    [Fact]
    public void MappingMatchesIndependentDngEquationAndIdentityDoesNotFallback()
    {
        Assert.True(EmbeddedLensCalibration.TryParseDng(Dng([[.999726,.000048,0,0,0,0]]),out var c));
        var point=c!.Map(80,50,0,101,101);
        double r2=30.0*30/(50.0*50+50.0*50);
        Assert.Equal(50+30*(.999726+.000048*r2),point.X,10);
        Assert.Equal(50,point.Y,10);
        Assert.True(EmbeddedLensCalibration.TryParseDng(Dng([[1,0,0,0,0,0]]),out var identity));
        Assert.True(identity!.IsUsable);
    }

    [Fact]
    public void CompleteVignetteCurveCorrectsSyntheticFlatFieldAtEveryPixel()
    {
        var c=new EmbeddedLensCalibration("test",VignettePolynomial:[.2,.1,.03,.01,.005]);
        var image=new ImageBuffer(101,101);
        for(int y=0;y<101;y++) for(int x=0;x<101;x++) {
            double r=Math.Sqrt((x-50)*(x-50)+(y-50)*(y-50))/Math.Sqrt(5000);
            for(int ch=0;ch<3;ch++) image.Data[(y*101+x)*3+ch]=(float)(1/c.Gain(r));
        }
        EmbeddedLensCorrections.ApplyVignetting(image,c);
        Assert.All(image.Data,v=>Assert.InRange(Math.Abs(v-1),0,2e-7));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RegionalAndFullFrameRenderAgreeWithManualResidualsAndLcc(bool lcc)
    {
        var c=new EmbeddedLensCalibration("test",Warp:[[.999726,.02,.001,0,.0001,-.0002]],VignettePolynomial:[.2,.1,0,0,0]);
        var src=new ImageBuffer(121,81);
        for(int i=0;i<src.Data.Length;i++) src.Data[i]=.1f+(i%173)/300f;
        var p=new FrameParams { EmbeddedLens=c,DistortionK1=-.01,VignetteAmount=.03,OutputIntent=OutputIntent.None };
        if(lcc) p.LccFlatField=new ImageBuffer(121,81,Enumerable.Repeat(.9f,src.Data.Length).ToArray());
        var full=Pipeline.ProcessFrame(src,p); var patch=RegionRender.Render(src,p,new(.23,.21,.31,.36));
        int left=(int)Math.Round(patch.Realised.X*full.Width),top=(int)Math.Round(patch.Realised.Y*full.Height);
        for(int y=0;y<patch.Image.Height;y++) for(int x=0;x<patch.Image.Width;x++) for(int ch=0;ch<3;ch++)
            Assert.InRange(Math.Abs(patch.Image.Data[(y*patch.Image.Width+x)*3+ch]-full.Data[((y+top)*full.Width+x+left)*3+ch]),0,2e-6);
        var corrected=EmbeddedLensCorrections.Apply(src,p);
        var rest=p.Clone(); rest.EmbeddedLens=null; rest.LccFlatField=null;
        Assert.Equal(Pipeline.ProcessFrame(corrected,rest).Data,full.Data);
    }

    [Fact]
    public void CurvesSurviveProjectRoundtripAndCloneOwnsArrays()
    {
        var native=new EmbeddedLensCalibration("test",Warp:[[.999726,.000048,0,0,0,0]],VignettePolynomial:[.2,.1,0,0,0]);
        var p=new FrameParams { EmbeddedLens=native }; var clone=p.Clone();
        Assert.NotSame(native.Warp![0],clone.EmbeddedLens!.Warp![0]);
        string path=Path.GetTempFileName();
        try {
            Project.Save(path,new Project.Data {Frames=new(){new(){SourcePath="frame.dng",Params=p}}});
            var restored=Project.Load(path).Frames[0].Params.EmbeddedLens!;
            Assert.Equal(native.Warp[0],restored.Warp![0]); Assert.Equal(native.Gain(.6),restored.Gain(.6),12);
        } finally { File.Delete(path); }
    }

    [Fact]
    public void IntervalBoundsCoverInteriorSamplesAndAllPlanesWithoutDecodingTheWholeFrame()
    {
        var c=new EmbeddedLensCalibration("test",Warp:[[.999726,.02,-.001,.0001,.002,-.001],[1,.03,0,0,0,0],[1,-.01,0,0,-.001,.002]],CenterX:.48,CenterY:.52);
        var b=EmbeddedLensCorrections.SourceBounds(23,19,74,61,c,121,81);
        for(int y=19;y<=61;y++) for(int x=23;x<=74;x++) for(int ch=0;ch<3;ch++) {
            var p=c.Map(x,y,ch,121,81);
            Assert.InRange(p.X,b.X0-1e-10,b.X1+1e-10); Assert.InRange(p.Y,b.Y0-1e-10,b.Y1+1e-10);
        }
        Assert.True((b.X1-b.X0)*(b.Y1-b.Y0)<121*81);
    }

    [Fact]
    public void BadPlaneAndTruncatedPayloadAreRejected()
    {
        Assert.False(new EmbeddedLensCalibration("bad",Warp:[null!]).IsUsable);
        var data=Dng([[1,.01,0,0,0,0]]); Array.Resize(ref data,data.Length-1);
        Assert.False(EmbeddedLensCalibration.TryParseDng(data,out _));
    }

    [Fact]
    public void DetectionCacheInvalidatesOnSourceReplacement()
    {
        string path=Path.GetTempFileName();
        try {
            File.WriteAllText(path,"bad metadata"); var before=LensDetectionCache.Get(path);
            Assert.Equal("元数据无法读取",before.Reason);
            using(var t=BitMiracle.LibTiff.Classic.Tiff.Open(path,"w")!) {
                t.SetField(BitMiracle.LibTiff.Classic.TiffTag.IMAGEWIDTH,2);
                t.SetField(BitMiracle.LibTiff.Classic.TiffTag.IMAGELENGTH,2);
                t.SetField(BitMiracle.LibTiff.Classic.TiffTag.SAMPLESPERPIXEL,3);
                t.SetField(BitMiracle.LibTiff.Classic.TiffTag.BITSPERSAMPLE,8);
                t.SetField(BitMiracle.LibTiff.Classic.TiffTag.PHOTOMETRIC,BitMiracle.LibTiff.Classic.Photometric.RGB);
                t.SetField(BitMiracle.LibTiff.Classic.TiffTag.PLANARCONFIG,BitMiracle.LibTiff.Classic.PlanarConfig.CONTIG);
                t.SetField(BitMiracle.LibTiff.Classic.TiffTag.ROWSPERSTRIP,1);
                t.WriteScanline(new byte[6],0);t.WriteScanline(new byte[6],1);
            }
            var after=LensDetectionCache.Get(path);
            Assert.NotSame(before,after); Assert.Same(after,LensDetectionCache.Get(path));
            Assert.Equal("未检测到内嵌校正数据",after.Reason);
        } finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(-1)]
    public void InvalidProfilesAreRejected(double value)
    {
        Assert.False(EmbeddedLensCalibration.TryParseDng(Dng([[value,0,0,0,0,0]]),out _));
    }

    [Fact]
    public void SonyKnotsArePreservedRatherThanReducedToACornerGain()
    {
        var d=new ExifSubIfdDirectory(); short[] knots=[16,0,2,10,22,40,62,91,125,164,211,264,324,394,472,560,659];
        d.Set(0x7037,knots); d.Set(0x7032,knots);
        var estimate=LensMetadata.EstimateCorrections([d])!;
        Assert.Equal(16,estimate.Native!.DistortionKnots!.Length);
        Assert.Equal(1+125/16384.0,estimate.Native.DistortionKnots[7]);
        Assert.Equal(Math.Pow(2,125/16384.0),estimate.Native.Gain(7/15.0),10);
        var p=new FrameParams(); AutoLensCorrection.Select(estimate,null).Apply(p,false);
        Assert.NotNull(p.EmbeddedLens); Assert.Equal(0,p.DistortionK1); Assert.Equal(0,p.VignetteAmount);
    }
}
