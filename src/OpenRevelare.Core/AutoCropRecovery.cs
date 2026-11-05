namespace OpenRevelare.Core;

/// <summary>Geometry before the latest automatic crop, retained across save/reopen.</summary>
public sealed record AutoCropRecovery(bool HadCrop, double X, double Y, double W, double H, double Rotation, int QuarterTurns = 0, bool FlipH = false, bool FlipV = false)
{
    public static AutoCropRecovery Capture(FrameParams p) => p.CropRect is {} c
        ? new(true,c.X,c.Y,c.W,c.H,p.Rotation,p.QuarterTurns,p.FlipH,p.FlipV) : new(false,0,0,0,0,p.Rotation,p.QuarterTurns,p.FlipH,p.FlipV);
}
