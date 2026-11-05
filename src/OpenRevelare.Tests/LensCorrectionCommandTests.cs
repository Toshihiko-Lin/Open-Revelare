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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownAndHeuristicResultsPreserveManualAdjustments(bool heuristic)
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
    public async Task BatchIsOneUndoStepAndPreservesMissingComponent()
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
            Assert.Equal(0.4, vm.VignetteAmount);
            Assert.Equal(0.6, second.Params.VignetteAmount);
            Assert.True(vm.CanUndo);
            vm.Undo();
            Assert.Equal(0, vm.DistortionK1);
            Assert.Equal(0, second.Params.DistortionK1);
            Assert.False(vm.CanUndo);
        }
        finally { File.Delete(path); }
    }
}
