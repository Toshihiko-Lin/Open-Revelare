using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class DecoupleCalibrationRobustnessTests
{
    [Fact]
    public void Low_exposure_but_separated_calibration_is_scale_invariant()
    {
        double[,] matrix = DecoupleCalibration.DecoupleMatrixFromRoiMeans(
            [1e-6, 0.0, 0.0],
            [0.0, 1e-6, 0.0],
            [0.0, 0.0, 1e-6]);

        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                Assert.Equal(r == c ? 1.0 : 0.0, matrix[r, c], 12);
    }

    [Fact]
    public void Bright_but_nearly_collinear_calibration_is_rejected()
    {
        // det = 1000, so the former absolute |det| < 1e-12 check accepted this even though
        // separating the lights requires roughly thousand-fold noise amplification.
        Assert.Throws<ArgumentException>(() =>
            DecoupleCalibration.DecoupleMatrixFromRoiMeans(
                [1000.0, 0.0, 0.0],
                [1000.0, 1.0, 0.0],
                [1000.0, 0.0, 1.0]));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-0.1)]
    public void Invalid_calibration_observations_are_rejected_before_inversion(double invalid)
    {
        Assert.Throws<ArgumentException>(() =>
            DecoupleCalibration.DecoupleMatrixFromRoiMeans(
                [1.0, 0.0, invalid], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0]));
    }
}
