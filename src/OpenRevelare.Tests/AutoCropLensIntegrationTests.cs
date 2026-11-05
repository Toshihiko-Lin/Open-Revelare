using OpenRevelare.Core;
using OpenRevelare.Gui.ViewModels;
using Xunit;
namespace OpenRevelare.Tests;

public sealed class AutoCropLensIntegrationTests
{
    [Theory]
    [InlineData(false,0,false)]
    [InlineData(false,1,true)]
    [InlineData(true,2,true)]
    [InlineData(true,3,false)]
    public void CropPreparationUsesTheRenderGeometryAndLeavesSourcePixelsUntouched(bool embedded,int turns,bool flip)
    {
        var source=FilmAutoCropTests.Perforated(false);
        var original=(float[])source.Data.Clone();
        var p=new FrameParams { QuarterTurns=turns,FlipH=flip,Rotation=1.3,DistortionK1=.008,VignetteAmount=.3 };
        ImageBuffer corrected;
        if(embedded) {
            p.EmbeddedLens=new EmbeddedLensCalibration("test",Warp:[[.999726,.015,.001,0,.0001,-.0001]],VignettePolynomial:[.3,.1,0,0,0]);
            var geometric=p.Clone();geometric.EmbeddedLens=geometric.EmbeddedLens! with {VignettePolynomial=null};
            corrected=EmbeddedLensCorrections.Apply(source,geometric);
        } else {
            p.Lensfun=LensfunCorrectionTests.Calibration();p.LensfunEnabled=true;
            var geometric=p.Clone();geometric.Lensfun=geometric.Lensfun! with {Vignetting=null};
            corrected=LensfunCorrections.Apply(source,geometric);
        }
        corrected=LensCorrections.ApplyDistortion(corrected,p.DistortionK1);
        var expected=Geometry.ApplyRotation(Geometry.ApplyOrientation(corrected,p.QuarterTurns,p.FlipH,p.FlipV),p.Rotation,fill:0);
        var actual=FilmAutoCrop.Prepare(source,p);
        Assert.Equal(expected.Data,actual.Data);
        Assert.Equal(original,source.Data);
        p.LccFlatField=new ImageBuffer(source.Width,source.Height,Enumerable.Repeat(.5f,source.Data.Length).ToArray());
        Assert.Equal(actual.Data,FilmAutoCrop.Prepare(source,p).Data); // lighting must not shift hole thresholds
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("cancel")]
    [InlineData("replace")]
    public async Task CropResultsCannotOverwriteChangedSelectionOrSources(string intervention)
    {
        string dir=Path.Combine(TestDataIsolation.Root,"crop-safety-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        string a=Path.Combine(dir,"a.tif"),b=Path.Combine(dir,"b.tif"),project=Path.Combine(dir,"roll.ncproj");
        TiffIO.ExportTiff16(FilmAutoCropTests.Perforated(false),a);File.Copy(a,b);
        Project.Save(project,new Project.Data {Meta=new(){InputType="tiff",TiffIsLinear=true},Frames=new(){new(){SourcePath=a},new(){SourcePath=b}}});
        using var vm=new MainViewModel();await vm.OpenProjectAsync(project);
        for(int i=0;i<400&&(vm.IsBusy||vm.RollAnalysisPending);i++) await Task.Delay(25);
        Assert.True(vm.HasImage);
        bool intervened=false;
        vm.PropertyChanged+=(_,e)=>{
            if(intervened) return;
            if(e.PropertyName==nameof(vm.IsBusy)&&vm.IsBusy) {
                if(intervention=="selection") {intervened=true;vm.Frames[1].IsSelected=true;}
                else if(intervention=="cancel") {intervened=true;vm.CancelAutoCrop();}
            }
            if(intervention=="replace"&&e.PropertyName==nameof(vm.StatusText)&&vm.StatusText.Contains("2/2")) {
                intervened=true;File.SetLastWriteTimeUtc(a,DateTime.UtcNow.AddMinutes(5));
            }
        };
        await vm.AutoCrop135Async(FilmCropMode.ImageArea,true);
        Assert.True(intervened);Assert.All(vm.Frames,f=>Assert.Null(f.Params.CropRect));
        Assert.False(vm.IsAutoCropping);Assert.False(vm.IsBusy);
    }
}
