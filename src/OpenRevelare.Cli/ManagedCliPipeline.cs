using OpenRevelare.ColorManagement;
using OpenRevelare.Core;

/// <summary>
/// Testable static seam for the CLI's normal image route. Every operation delegates to the same
/// typed managed-v2 boundaries used by other front ends; the caller owns the one process-scoped
/// <see cref="IColorManagementEngine"/> and must dispose it.
/// </summary>
internal static class ManagedCliPipeline
{
    internal const ColorPipelineVersion PipelineVersion = ColorPipelineVersion.ManagedV2;

    internal static WorkingFrame LoadWorking(
        string path,
        bool inputIsSrgb,
        IColorManagementEngine colorManagement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(colorManagement);

        if (!RawDecode.IsRawExtension(path))
        {
            return TiffIO.LoadWorkingFrame(
                path,
                inputIsSrgb,
                PipelineVersion,
                colorManagement);
        }

        return RawDecode.DecodeRawWorking(
            path,
            RawDecode.RawBackend.LibRaw,
            RawDecode.FbddMode.Off,
            out _);
    }

    internal static RenderedFrame Render(
        WorkingFrame source,
        FrameParams parameters,
        IColorManagementEngine colorManagement)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(colorManagement);
        return OpenRevelare.Core.Pipeline.Render(
            source,
            parameters,
            PipelineVersion,
            colorManagement);
    }

    internal static void ExportJpeg(
        RenderedFrame frame,
        string path,
        int quality,
        string? description = null)
    {
        // The typed exporter rejects scene-linear frames instead of silently clipping and
        // labelling them sRGB. Exact ICC embedding is the only normal CLI policy.
        JpegIO.ExportJpeg(
            frame,
            path,
            quality,
            description,
            ExportProfilePolicy.EmbedExact);
    }

    internal static void ExportTiff(
        RenderedFrame frame,
        string path,
        TiffIO.CompressionMode compression,
        string? description = null)
    {
        TiffIO.ExportTiff(
            frame,
            path,
            compression,
            description,
            ExportProfilePolicy.EmbedExact);
    }
}
