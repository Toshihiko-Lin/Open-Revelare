using OpenRevelare.Core;
using OpenRevelare.Gui.ViewModels;
using Xunit;
namespace OpenRevelare.Tests;

public sealed class AutoCropFallbackTests
{
    [Fact]
    public async Task ReferenceCropRequiresAnAppliedBoxAndManualEditCompletesReview()
    {
        string dir=Path.Combine(TestDataIsolation.Root,"reference-crop-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        string a=Path.Combine(dir,"a.tif"),b=Path.Combine(dir,"b.tif"),project=Path.Combine(dir,"roll.ncproj");
        TiffIO.ExportTiff16(FilmAutoCropTests.Unreadable(),a);File.Copy(a,b);
        var old=(X:.12,Y:.2,W:.3,H:.2);
        Project.Save(project,new Project.Data {Meta=new(){InputType="tiff",TiffIsLinear=true},Frames=new(){new(){SourcePath=a},new(){SourcePath=b,Params=new(){CropRect=old}}}});
        using var vm=new MainViewModel();await vm.OpenProjectAsync(project);
        for(int i=0;i<400&&(vm.IsBusy||vm.RollAnalysisPending);i++)await Task.Delay(25);
        Assert.False(vm.CanSyncReferenceCrop);
        vm.ApplyCropToFrames();Assert.Equal(old,vm.Frames[1].Params.CropRect);
        await vm.AutoCrop135Async(FilmCropMode.ImageArea,false);Assert.True(vm.HasAutoCropReview);
        vm.SetCrop((.1,.1,.7,.6));
        Assert.False(vm.HasAutoCropReview);Assert.True(vm.CanSyncReferenceCrop);
        vm.ApplyCropToFrames();Assert.Equal(vm.CurrentCrop,vm.Frames[1].Params.CropRect);
    }

    [Fact]
    public async Task UncertainFramesKeepExistingGeometryByDefaultAndExposeManualFallback()
    {
        string dir=Path.Combine(TestDataIsolation.Root,"crop-default-fallback-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        string a=Path.Combine(dir,"a.tif"),b=Path.Combine(dir,"b.tif"),project=Path.Combine(dir,"roll.ncproj");
        TiffIO.ExportTiff16(FilmAutoCropTests.Unreadable(),a);File.Copy(a,b);
        var old=(X:.12,Y:.2,W:.3,H:.2);
        Project.Save(project,new Project.Data {Meta=new(){InputType="tiff",TiffIsLinear=true},Frames=new(){new(){SourcePath=a},new(){SourcePath=b,Params=new(){CropRect=old}}}});
        using var vm=new MainViewModel();await vm.OpenProjectAsync(project);
        for(int i=0;i<400&&(vm.IsBusy||vm.RollAnalysisPending);i++)await Task.Delay(25);
        Assert.False(vm.AllowAutoCropSuggestions);
        await vm.AutoCrop135Async(FilmCropMode.ImageArea,true);
        Assert.Null(vm.Frames[0].Params.CropRect);Assert.Equal(old,vm.Frames[1].Params.CropRect);
        Assert.Equal(0,vm.Frames[0].Params.Rotation);Assert.True(vm.HasAutoCropReview);
        Assert.False(vm.HasAutoCropRecovery);
        vm.SelectAutoCropReviewFrames();Assert.All(vm.Frames,f=>Assert.True(f.IsSelected));
        Assert.True(await vm.SaveProjectAsync(project));
        Assert.All(Project.Load(project).Frames,f=>Assert.NotEmpty(f.Params.AutoCropReviewReason));
    }

    [Fact]
    public async Task RecoverySurvivesSaveReopenAndRestoresPreviousCropAndAngle()
    {
        string dir=Path.Combine(TestDataIsolation.Root,"crop-recovery-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        string source=Path.Combine(dir,"a.tif"),project=Path.Combine(dir,"roll.ncproj");
        TiffIO.ExportTiff16(FilmAutoCropTests.Perforated(false),source);
        var old=(X:.15,Y:.2,W:.5,H:.45);
        Project.Save(project,new Project.Data {Meta=new(){InputType="tiff",TiffIsLinear=true},Frames=new(){new(){SourcePath=source,Params=new(){CropRect=old}}}});
        using var vm=new MainViewModel();await vm.OpenProjectAsync(project);
        for(int i=0;i<400&&(vm.IsBusy||vm.RollAnalysisPending);i++)await Task.Delay(25);
        await vm.AutoCrop135Async(FilmCropMode.ImageArea,false);
        Assert.True(vm.HasAutoCropRecovery);Assert.True(vm.HasAutoCropReview);
        var applied=vm.Frames[0].Params.CropRect;Assert.NotEqual(old,applied);
        vm.ConfirmAutoCropReview();Assert.False(vm.HasAutoCropReview);
        Assert.True(await vm.SaveProjectAsync(project));await vm.OpenProjectAsync(project);
        for(int i=0;i<400&&(vm.IsBusy||vm.RollAnalysisPending);i++)await Task.Delay(25);
        Assert.True(vm.HasAutoCropRecovery);Assert.False(vm.HasAutoCropReview);
        Assert.True(applied==vm.Frames[0].Params.CropRect,"reopen must keep the applied crop");
        Assert.False(vm.CanUndo);
        vm.RestoreAutoCropRecovery();Assert.Equal(old,vm.Frames[0].Params.CropRect);
        Assert.Equal(0,vm.Rotation);Assert.False(vm.HasAutoCropRecovery);
        Assert.True(vm.CanUndo);
        vm.Undo();Assert.True(applied==vm.Frames[0].Params.CropRect,"undo must restore the applied crop");Assert.True(vm.HasAutoCropRecovery);
    }
}
