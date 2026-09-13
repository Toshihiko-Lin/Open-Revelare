using OpenRevelare.Core;
using OpenRevelare.Gui.ViewModels;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// The film-style picker's rows and selection across the states that rebuild it (D-033 /
/// D-034): construction, the HDR toggle, and the output declaration.
/// </summary>
public sealed class PrintLutPickerTests
{
    [Fact]
    public void The_picker_is_populated_at_construction_with_the_standard_rendering_selected()
    {
        using var vm = new MainViewModel();

        // 标准 + six shipped stocks + 选择文件…
        Assert.Equal(8, vm.PrintLutNames.Count);
        Assert.Equal(0, vm.PrintLutIndex);
        Assert.False(vm.HasPrintLut);
        Assert.Contains(vm.PrintLutNames, n => n.Contains("Kodak 2383 D65", StringComparison.Ordinal));
    }

    [Fact]
    public void Toggling_HDR_keeps_the_rows_and_the_selection()
    {
        using var vm = new MainViewModel();
        vm.PrintLutIndex = 1;
        Assert.True(vm.HasPrintLut);

        vm.HdrEnabled = true;
        Assert.Equal(8, vm.PrintLutNames.Count);
        Assert.Equal(1, vm.PrintLutIndex);
        Assert.Equal("印片色 · HDR 影调", vm.PrintLutTargetNote);

        vm.HdrEnabled = false;
        Assert.Equal(8, vm.PrintLutNames.Count);
        Assert.Equal(1, vm.PrintLutIndex);
        Assert.Equal("", vm.PrintLutTargetNote);

        vm.PrintLutIndex = 0;
        vm.HdrEnabled = true;
        Assert.Equal(0, vm.PrintLutIndex);
        vm.HdrEnabled = false;
        Assert.Equal(0, vm.PrintLutIndex);
    }

    [Fact]
    public void A_cube_whose_header_declares_its_output_offers_no_declaration()
    {
        // The shipped film looks say "Display: ITU-Rec.709, Gamma 2.4" themselves; overriding
        // that is how gamma-2.4 codes end up read as PQ luminance.
        using var vm = new MainViewModel();
        vm.PrintLutIndex = 1;
        Assert.True(vm.HasPrintLut);
        Assert.False(vm.CanEditPrintLutContract);
        vm.HdrEnabled = true;
        Assert.False(vm.CanEditPrintLutContract);
    }

    [Fact]
    public void The_output_declaration_reaches_the_render_parameters()
    {
        using var vm = new MainViewModel();
        vm.PrintLutIndex = 1;
        Assert.Equal(0, vm.PrintLutOutputIndex);            // header prefill: Rec709
        Assert.Equal("Rec709", vm.RenderParamsForTest().PrintLutOutput);

        vm.PrintLutOutputIndex = 1;                          // DCI-P3
        Assert.Equal("DciP3", vm.RenderParamsForTest().PrintLutOutput);
        Assert.Contains("DCI-P3", vm.PrintLutContractText, StringComparison.Ordinal);

        vm.PrintLutIndex = 0;
        Assert.Equal("", vm.RenderParamsForTest().PrintLutOutput);
        Assert.Equal("", vm.RenderParamsForTest().PrintLut);
    }
}
