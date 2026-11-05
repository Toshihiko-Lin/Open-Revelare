using System.Reflection;
using OpenRevelare.Core;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.ViewModels;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class LensCorrectionCommandTests
{
    private static void Set(MainViewModel vm, string name, object value) =>
        typeof(MainViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, value);

    private static LensCorrectionEstimate Estimate() => new("Test", 100, 8, 0.01, 0.2, 2.5, "Embedded");

    [Fact]
    public void SyncCopiesOnlyResidualsAcrossDifferentNativeBases()
    {
        string path=Path.GetTempFileName();
        try {
            using var vm=new MainViewModel();
            var first=new RollFrame(path); var target=new RollFrame(path);
            target.Params.Lensfun=LensfunCorrectionTests.Calibration();target.Params.LensfunEnabled=true;
            vm.Frames.Add(first);vm.Frames.Add(target);Set(vm,"_currentFrame",first);Set(vm,"_paramsLoaded",true);
            Set(vm,"_embeddedLens",new EmbeddedLensCalibration("native",Warp:[[1,.02,0,0,0,0]]));
            Set(vm,"_lensCorrectionOrigin","native");vm.DistortionK1=.005;
            vm.SyncLensSliders(true);
            Assert.True(target.Params.LensfunEnabled);Assert.Null(target.Params.EmbeddedLens);
            Assert.Equal(.005,target.Params.DistortionK1);Assert.Empty(target.Params.LensCorrectionOrigin);
        } finally { try {File.Delete(path);}catch(IOException){} }
    }

    [Fact]
    public void DisablingNativeBasePreservesFineTuningAndUndoRestoresTheBase()
    {
        string path=Path.GetTempFileName();
        try {
            using var vm=new MainViewModel();
            var native=new EmbeddedLensCalibration("test",Warp:[[.999726,.000048,0,0,0,0]],VignettePolynomial:[.2,0,0,0,0]);
            var frame=new RollFrame(path); vm.Frames.Add(frame);
            Set(vm,"_currentFrame",frame);Set(vm,"_paramsLoaded",true);Set(vm,"_embeddedLens",native);
            vm.DistortionK1=.02;vm.VignetteAmount=.03;
            vm.DisableAutoLensCorrection(false);
            Assert.Null(frame.Params.EmbeddedLens);Assert.Equal(.02,vm.DistortionK1);Assert.Equal(.03,vm.VignetteAmount);
            vm.Undo(); Assert.NotNull(frame.Params.EmbeddedLens); Assert.Equal(.02,vm.DistortionK1);
        } finally { try {File.Delete(path);}catch(IOException){} }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void AutoSelectionUsesOneSourceAndClearsPreviousAdjustments(bool embedded, bool lensfun)
    {
        var choice = AutoLensCorrection.Select(embedded ? Estimate() : null,
            lensfun ? LensfunCorrectionTests.Calibration() : null);
        var p = new FrameParams { DistortionK1 = 0.4, VignetteAmount = 0.8, LensfunEnabled = true };
        choice.Apply(p, false);
        Assert.Equal(embedded ? 0.01 : 0, p.DistortionK1);
        Assert.Equal(embedded ? 0.2 : 0, p.VignetteAmount);
        Assert.Equal(!embedded && lensfun, p.LensfunEnabled);
    }

    [Fact]
    public async Task AutoCanReplaceAnActiveLensfunWithEmbeddedWithoutStacking()
    {
        string path = Path.GetTempFileName();
        try
        {
            using var vm = new MainViewModel(); var frame = new RollFrame(path);
            vm.Frames.Add(frame); Set(vm, "_currentFrame", frame); Set(vm, "_paramsLoaded", true);
            Set(vm, "_lensfunCalibration", LensfunCorrectionTests.Calibration()); Set(vm, "_lensfunEnabled", true);
            Set(vm, "_readLensCorrectionAsync", (Func<string, Task<LensCorrectionEstimate?>>)(_ => Task.FromResult<LensCorrectionEstimate?>(Estimate())));
            Set(vm, "_matchLensfunAsync", (Func<string, Task<LensfunCalibration?>>)(_ => throw new Exception("must not match after embedded hit")));
            await vm.AutoLensCorrectionAsync(false);
            Assert.False(vm.LensfunEnabled); Assert.Equal(0.01, vm.DistortionK1);
            Assert.Contains("Embedded", frame.Params.LensCorrectionOrigin);
            vm.Undo(); Assert.True(vm.LensfunEnabled);
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    [Fact]
    public void LensOriginSurvivesCloneAndProjectRoundTrip()
    {
        string path = Path.GetTempFileName();
        try
        {
            var p = new FrameParams { LensCorrectionOrigin = "Sony firmware", LensCorrectionHasVignette = true };
            var clone = p.Clone();
            Assert.Equal(p.LensCorrectionOrigin, clone.LensCorrectionOrigin);
            Assert.True(clone.LensCorrectionHasVignette);
            Project.Save(path, new Project.Data { Frames = new() { new() { SourcePath = "frame.dng", Params = p } } });
            var loaded = Project.Load(path).Frames[0].Params;
            Assert.Equal(p.LensCorrectionOrigin, loaded.LensCorrectionOrigin);
            Assert.True(loaded.LensCorrectionHasVignette);
        }
        finally { try { File.Delete(path); } catch (IOException) { /* Preview readers release asynchronously. */ } }
    }

    [Fact]
    public async Task MetadataBlocksLensfunUntilExplicitResetAndUndoRestoresOrigin()
    {
        string path = Path.GetTempFileName();
        try
        {
            using var vm = new MainViewModel();
            var frame = new RollFrame(path);
            vm.Frames.Add(frame);
            Set(vm, "_currentFrame", frame); Set(vm, "_paramsLoaded", true);
            Set(vm, "_lensfunCalibration", LensfunCorrectionTests.Calibration());
            Set(vm, "_readLensCorrectionAsync", (Func<string, Task<LensCorrectionEstimate?>>)(_ =>
                Task.FromResult<LensCorrectionEstimate?>(Estimate())));
            await vm.AutoLensCorrectionAsync(false);
            Set(vm, "_editVersion", 50);
            vm.DistortionK1 = 0.02; // fine tuning must not discard provenance or switching protection
            vm.LensfunEnabled = true;
            Assert.False(vm.LensfunEnabled);
            Set(vm, "_lccAvailable", true);
            vm.LccEnabled = true;
            Assert.False(vm.LccEnabled);
            vm.ResetLensSliders(true);
            Assert.Equal(0, vm.DistortionK1);
            vm.Undo();
            Assert.Equal(0.02, vm.DistortionK1);
            vm.LensfunEnabled = true;
            Assert.False(vm.LensfunEnabled);
            vm.ResetLensSliders(true);
            vm.LensfunEnabled = true;
            Assert.True(vm.LensfunEnabled);
        }
        finally { try { File.Delete(path); } catch (IOException) { /* Preview readers release asynchronously. */ } }
    }

    [Fact]
    public void SyncCopiesLiveSlidersOnlyToExplicitTargetsAndCanUndo()
    {
        string path = Path.GetTempFileName();
        try
        {
            using var vm = new MainViewModel();
            var first = new RollFrame(path); var selected = new RollFrame(path) { IsSelected = true };
            var other = new RollFrame(path);
            vm.Frames.Add(first); vm.Frames.Add(selected); vm.Frames.Add(other);
            Set(vm, "_currentFrame", first); Set(vm, "_paramsLoaded", true);
            vm.DistortionK1 = 0.08; vm.VignetteAmount = 0.3;
            vm.SyncLensSliders(false);
            Assert.Equal(0.08, selected.Params.DistortionK1);
            Assert.Equal(0.3, selected.Params.VignetteAmount);
            Assert.Equal(0, other.Params.DistortionK1);
            vm.Undo();
            Assert.Equal(0, selected.Params.DistortionK1);
            Assert.Equal(0.08, vm.DistortionK1);
            vm.SyncLensSliders(true);
            Assert.Equal(0.08, other.Params.DistortionK1);
        }
        finally { try { File.Delete(path); } catch (IOException) { /* Preview readers release asynchronously. */ } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingSourceDoesNotPublishUnknownOrHeuristicResults(bool heuristic)
    {
        using var vm = new MainViewModel();
        var frame = new RollFrame("unused.raw");
        vm.Frames.Add(frame);
        Set(vm, "_currentFrame", frame);
        Set(vm, "_paramsLoaded", true);
        vm.DistortionK1 = 0.07;
        vm.VignetteAmount = 0.4;
        Set(vm, "_readLensCorrectionAsync", (Func<string, Task<LensCorrectionEstimate?>>)(_ =>
            Task.FromResult<LensCorrectionEstimate?>(Estimate() with
            { HasDistortion = heuristic, HasVignette = heuristic, IsHeuristic = heuristic })));
        await vm.AutoLensCorrectionAsync(false);
        Assert.Equal(0.07, vm.DistortionK1);
        Assert.Equal(0.4, vm.VignetteAmount);
    }

    [Theory]
    [InlineData("frame")]
    [InlineData("edit")]
    [InlineData("selection")]
    [InlineData("dispose")]
    [InlineData("navigation")]
    public async Task StaleResultsAndDuplicateClicksCannotChangeParameters(string change)
    {
        using var vm = new MainViewModel();
        var frame = new RollFrame("unused.raw");
        vm.Frames.Add(frame);
        Set(vm, "_currentFrame", frame);
        Set(vm, "_paramsLoaded", true);
        var completion = new TaskCompletionSource<LensCorrectionEstimate?>();
        int calls = 0;
        Set(vm, "_readLensCorrectionAsync", (Func<string, Task<LensCorrectionEstimate?>>)(_ =>
        { calls++; return completion.Task; }));
        var pending = vm.AutoLensCorrectionAsync(true);
        await vm.AutoLensCorrectionAsync(true);
        Assert.Equal(1, calls);
        switch (change)
        {
            case "frame": Set(vm, "_currentFrame", new RollFrame("other.raw")); break;
            case "edit": Set(vm, "_editVersion", 99); break;
            case "selection": frame.IsSelected = true; break;
            case "navigation": Set(vm, "_lensCorrectionGeneration", 1); break;
            case "dispose": vm.Dispose(); break;
        }
        completion.SetResult(Estimate());
        await pending;
        Assert.Equal(0, vm.DistortionK1);
        Assert.Equal(0, vm.VignetteAmount);
        Assert.False(vm.IsReadingLensCorrection);
    }

    [Fact]
    public async Task BatchIsOneUndoStepAndClearsOldComponentsWhenReSelecting()
    {
        string path = Path.GetTempFileName();
        try
        {
            using var vm = new MainViewModel();
            var first = new RollFrame(path);
            var second = new RollFrame(path);
            vm.Frames.Add(first);
            vm.Frames.Add(second);
            Set(vm, "_currentFrame", first);
            Set(vm, "_paramsLoaded", true);
            vm.VignetteAmount = 0.4;
            second.Params.VignetteAmount = 0.6;
            Set(vm, "_readLensCorrectionAsync", (Func<string, Task<LensCorrectionEstimate?>>)(_ =>
                Task.FromResult<LensCorrectionEstimate?>(Estimate() with { HasVignette = false })));
            await vm.AutoLensCorrectionAsync(true);
            Assert.Equal(0.01, vm.DistortionK1);
            Assert.Equal(0.01, second.Params.DistortionK1);
            Assert.Equal(0, vm.VignetteAmount);
            Assert.Equal(0, second.Params.VignetteAmount);
            Assert.True(vm.CanUndo);
            vm.Undo();
            Assert.Equal(0, vm.DistortionK1);
            Assert.Equal(0, second.Params.DistortionK1);
            Assert.False(vm.CanUndo);
        }
        finally { try { File.Delete(path); } catch (IOException) { /* Preview readers release asynchronously. */ } }
    }
}
