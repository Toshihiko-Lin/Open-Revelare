using OpenRevelare.Gui.Controls;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// The slider ↔ spin-box bridge inside <see cref="SliderRow"/>.
///
/// The bug these pin: <see cref="SliderRow.SpinValue"/> is <c>decimal?</c> because NumericUpDown
/// is, and an EMPTY NumericUpDown writes null — not an unparseable one ("abc" and whitespace both
/// leave the number alone), but an empty one, which is the state the box passes through every time
/// the user selects the digits and deletes them on the way to typing a different value.
///
/// The bridge refused that null so it could not reach Value, and stopped there. That protected the
/// number but left the two halves disagreeing: the box was bound to null and rendered empty while
/// Value still held the real number, and nothing re-synced them, because the only thing that pushes
/// Value → SpinValue is a CHANGE to Value — which never came. The row read as cleared for the rest
/// of its life, and the next edit committed from an empty baseline.
///
/// No Avalonia app is started here. SliderRow is a UserControl, but the property system it inherits
/// works standalone, which is what lets a UI-side rule be pinned by an ordinary test.
/// </summary>
public class SliderRowBridgeTests
{
    private static SliderRow Row(double value) =>
        new() { Minimum = -1, Maximum = 1, Value = value };

    /// <summary>THE regression: emptying the box must leave the row whole, not half-null.</summary>
    [Fact]
    public void Emptying_the_spin_box_restores_it_instead_of_leaving_it_null()
    {
        var row = Row(0.42);

        row.SpinValue = null;   // what an emptied NumericUpDown writes

        Assert.Equal(0.42, row.Value, 9);      // the number was never at risk …
        Assert.Equal(0.42m, row.SpinValue);    // … and the box is showing it again
    }

    /// <summary>
    /// The null must not be laundered into an edit. Restoring the box is a re-sync, so the value
    /// has to come back byte-for-byte rather than via the slider's range or a rounded default.
    /// </summary>
    [Fact]
    public void Restoring_after_a_null_does_not_move_the_value()
    {
        foreach (double v in new[] { -1.0, -0.375, 0.0, 0.125, 1.0 })
        {
            var row = Row(v);
            row.SpinValue = null;
            Assert.Equal(v, row.Value, 9);
            Assert.Equal((decimal)v, row.SpinValue);
        }
    }

    /// <summary>A real number typed into the box still drives the value — the guard's other half.</summary>
    [Fact]
    public void A_number_in_the_spin_box_still_reaches_the_value()
    {
        var row = Row(0.0);

        row.SpinValue = -0.25m;

        Assert.Equal(-0.25, row.Value, 9);
    }

    /// <summary>And the reverse direction, which the null case must not have broken.</summary>
    [Fact]
    public void Setting_the_value_still_updates_the_spin_box()
    {
        var row = Row(0.0);

        row.Value = 0.6;

        Assert.Equal(0.6m, row.SpinValue);
    }

    /// <summary>
    /// Emptying the box twice running must be as harmless as once. The re-sync writes SpinValue
    /// from inside SpinValue's own change handler, so a missing re-entrancy guard would show up
    /// here as a stack overflow rather than a wrong number.
    /// </summary>
    [Fact]
    public void Emptying_repeatedly_is_harmless()
    {
        var row = Row(0.42);

        for (int i = 0; i < 5; i++) row.SpinValue = null;

        Assert.Equal(0.42, row.Value, 9);
        Assert.Equal(0.42m, row.SpinValue);
    }
}
