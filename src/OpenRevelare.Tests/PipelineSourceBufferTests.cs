using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// <see cref="Pipeline.ProcessFrame"/> must not write the buffer it is handed.
///
/// WHY THIS IS PINNED. The caller's buffer is shared and long-lived: the GUI holds one decoded
/// preview per frame in <c>PreviewCache</c> and re-renders it on every slider move, and the
/// export path reuses one decode across the render and the sampling passes. If a pre-inversion
/// op wrote through, the second render would start from the first render's intermediate state —
/// a corruption that COMPOUNDS silently, looking like drift rather than a bug, and only on the
/// configurations that enable those ops.
///
/// The invariant used to be maintained by cloning unconditionally, up front, before asking which
/// ops were active. That was safe but wasteful: distortion resamples out of place and allocates
/// its own output, so on a distorted frame the clone was read once and dropped — a 288 MB
/// allocation and a full memcpy per 24 MP frame, for nothing. The clone is now conditional, which
/// makes the invariant a property of the CONTROL FLOW rather than of one unmissable statement.
/// Hence these tests: each drives one pre-inversion op and asserts the input is untouched.
/// </summary>
public class PipelineSourceBufferTests
{
    /// <summary>
    /// A small negative with structure in it — a flat field would hide a scatter/gather bug, and
    /// values must stay clear of zero so the density inversion has something finite to work on.
    /// </summary>
    private static ImageBuffer MakeNegative(int w = 24, int h = 16)
    {
        var img = new ImageBuffer(w, h);
        float[] d = img.Data;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 3;
                d[i] = 0.20f + 0.60f * x / (w - 1);
                d[i + 1] = 0.25f + 0.55f * y / (h - 1);
                d[i + 2] = 0.30f + 0.40f * ((x + y) % 7) / 6.0f;
            }
        return img;
    }

    private static void AssertUntouched(float[] before, ImageBuffer after)
        => Assert.Equal(before, after.Data);

    /// <summary>
    /// Distortion: the op that made the old unconditional clone redundant, and therefore the one
    /// whose output now DOUBLES as the working copy. If it ever became in-place this fails.
    /// </summary>
    [Fact]
    public void Distortion_DoesNotWriteSourceBuffer()
    {
        ImageBuffer src = MakeNegative();
        float[] before = (float[])src.Data.Clone();

        Pipeline.ProcessFrame(src, new FrameParams { DistortionK1 = 0.08 });

        AssertUntouched(before, src);
    }

    /// <summary>
    /// Vignette alone: no distortion runs, so nothing has produced a private buffer and the
    /// conditional clone is the only thing standing between this in-place op and the caller.
    /// This is the case the new control flow could most easily have got wrong.
    /// </summary>
    [Fact]
    public void Vignette_DoesNotWriteSourceBuffer()
    {
        ImageBuffer src = MakeNegative();
        float[] before = (float[])src.Data.Clone();

        Pipeline.ProcessFrame(src, new FrameParams { VignetteAmount = 0.5 });

        AssertUntouched(before, src);
    }

    /// <summary>
    /// Distortion AND an in-place op together: the clone must be skipped (distortion already
    /// supplied a private buffer) without the in-place op reaching back to the caller's.
    /// </summary>
    [Fact]
    public void DistortionThenVignette_DoesNotWriteSourceBuffer()
    {
        ImageBuffer src = MakeNegative();
        float[] before = (float[])src.Data.Clone();

        Pipeline.ProcessFrame(src, new FrameParams { DistortionK1 = 0.08, VignetteAmount = 0.5 });

        AssertUntouched(before, src);
    }

    /// <summary>
    /// The input-primaries transform, which is applied to <c>src.Data</c> in place well after the
    /// clone decision is made. Nothing in the app sets it today — a project file can — so it is
    /// exactly the kind of op that gets forgotten when the copy becomes conditional.
    /// </summary>
    [Fact]
    public void InputPrimaries_DoesNotWriteSourceBuffer()
    {
        ImageBuffer src = MakeNegative();
        float[] before = (float[])src.Data.Clone();

        // Rec709/sRGB primaries, declared explicitly so InputTransform.ToWorking yields a real
        // (non-identity, since working is ACEScg) matrix rather than null.
        var cal = new FrameParams
        {
            InputPrimaries = new[,] { { 0.640, 0.330 }, { 0.300, 0.600 }, { 0.150, 0.060 } },
        };

        Pipeline.ProcessFrame(src, cal);

        AssertUntouched(before, src);
    }

    /// <summary>
    /// The no-op configuration: no pre-inversion op is active, so no copy should be needed and
    /// the caller's buffer must still come back intact.
    /// </summary>
    [Fact]
    public void PlainRender_DoesNotWriteSourceBuffer()
    {
        ImageBuffer src = MakeNegative();
        float[] before = (float[])src.Data.Clone();

        Pipeline.ProcessFrame(src, new FrameParams());

        AssertUntouched(before, src);
    }

    /// <summary>
    /// Rendering the SAME buffer twice must give the same picture. This is the failure the
    /// invariant actually prevents — a write-through corrupts the second render, not the first,
    /// which is why a single-render test would miss it.
    /// </summary>
    [Fact]
    public void RepeatedRenders_AreStable()
    {
        ImageBuffer src = MakeNegative();
        var cal = new FrameParams { DistortionK1 = 0.08, VignetteAmount = 0.5 };

        float[] first = (float[])Pipeline.ProcessFrame(src, cal).Data.Clone();
        float[] second = Pipeline.ProcessFrame(src, cal).Data;

        Assert.Equal(first, second);
    }
}
