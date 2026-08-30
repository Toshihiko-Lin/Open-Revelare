using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public class LittleCmsEngineTests
{
    private const string PinnedSourceSha256 =
        "bfc54f7bab59fbc921012014a8032e4cba4abd46db47d46b76416a8c0b2815c8";
    private const float ReferenceTolerance = 0.0025f;

    [Fact]
    public void Bundled_build_identity_and_RGB_profile_validation_are_diagnostic()
    {
        using var engine = new LittleCmsEngine();

        CmmBuildIdentity build = engine.Build;
        Assert.Equal("LittleCMS", build.Product);
        Assert.Equal("2.19.1", build.RequiredRelease);
        Assert.Equal(2190, build.EncodedNativeVersion);
        Assert.Equal("2.19", build.ReportedNativeVersion);
        Assert.Equal("lcms2", build.LogicalLibraryName);
        Assert.Equal(LittleCmsEngine.FixedTransformFlags, build.FixedFlags);
        Assert.Equal(PinnedSourceSha256, build.SourceSha256);
        Assert.Equal(Path.GetFullPath(NativePath), build.NativePath);
        Assert.Equal(HashFile(NativePath), build.NativeSha256);
        Assert.False(string.IsNullOrWhiteSpace(build.BuildOptions));
        using (JsonDocument buildOptions = JsonDocument.Parse(build.BuildOptions))
            Assert.Equal(JsonValueKind.Object, buildOptions.RootElement.ValueKind);

        foreach (ColorProfileRef profile in FiveBuiltInProfiles())
        {
            ProfileValidationResult validation = engine.Validate(profile);
            Assert.True(validation.IsValid, $"{profile}: {validation.Message}");
            Assert.Equal(profile.Identity, validation.Identity);
            Assert.Equal(0x52474220u, validation.ColorSpaceSignature); // 'RGB '
            Assert.Equal(0x58595A20u, validation.PcsSignature);        // 'XYZ '
            Assert.NotNull(validation.EncodedIccVersion);
        }

        ColorProfileRef truncated = ColorProfileRef.Create(
            new byte[] { 0x00, 0x00, 0x00, 0x04 },
            "truncated test payload",
            ProfileRole.Input,
            new ProfileSource.Generated("OpenRevelare.Tests", "M1"));
        ProfileValidationResult invalid = engine.Validate(truncated);
        Assert.False(invalid.IsValid);
        Assert.Contains("truncated", invalid.Message, StringComparison.OrdinalIgnoreCase);

        CmmDiagnosticsSnapshot diagnostics = engine.GetDiagnostics();
        Assert.Equal(6, diagnostics.ProfileValidations);
        Assert.Equal(1, diagnostics.ProfileValidationFailures);
        Assert.Equal(0, diagnostics.NativeErrors);
    }

    [Fact]
    public void Manifest_version_mismatch_is_rejected_before_native_resolution()
    {
        string directory = CopyBundleToTemporaryDirectory();
        try
        {
            string manifestPath = Path.Combine(directory, "lcms2.manifest.json");
            JsonObject manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            manifest["version"] = "2.19.0";
            File.WriteAllText(manifestPath, manifest.ToJsonString());

            ColorManagementException error = Assert.Throws<ColorManagementException>(
                () => new LittleCmsEngine(directory));
            Assert.Contains("manifest version", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("2.19.0", error.Message);
            Assert.Contains("2.19.1", error.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Manifest_artifact_hash_mismatch_is_rejected_before_native_resolution()
    {
        string directory = CopyBundleToTemporaryDirectory();
        try
        {
            string manifestPath = Path.Combine(directory, "lcms2.manifest.json");
            JsonObject manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            JsonObject artifact = manifest["artifacts"]!.AsArray()
                .Select(node => node!.AsObject())
                .Single(item => item["file"]!.GetValue<string>() == NativeFileName);
            artifact["sha256"] = new string('0', 64);
            File.WriteAllText(manifestPath, manifest.ToJsonString());

            ColorManagementException error = Assert.Throws<ColorManagementException>(
                () => new LittleCmsEngine(directory));
            Assert.Contains("SHA-256 mismatch", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("manifest=", error.Message);
            Assert.Contains("actual=", error.Message);
            Assert.Contains(NativeFileName, error.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("sRGB")]
    [InlineData("DisplayP3")]
    [InlineData("AdobeRGB")]
    [InlineData("Rec709")]
    public void RGB_float_profiles_match_existing_matrix_and_TRC_reference_in_both_directions(
        string displaySpaceName)
    {
        ColorSpaceDef displaySpace = ColorSpaces.ByName(displaySpaceName, default);
        ColorProfileRef displayProfile = BuiltInColorProfiles.For(displaySpace, ProfileRole.Input);
        ColorProfileRef workingProfile = BuiltInColorProfiles.LinearAcesCg(ProfileRole.Working);
        using var engine = new LittleCmsEngine();

        float[] encodedDisplaySamples =
        {
            0.18f, 0.42f, 0.73f,
            0.62f, 0.48f, 0.31f,
        };
        float[] expectedWorking = (float[])encodedDisplaySamples.Clone();
        OutputRender.Decode(expectedWorking, displaySpace);
        OutputRender.Convert(expectedWorking, displaySpace, ColorSpaces.AcesCg, GamutMapping.Clip);

        float[] actualWorking = Transform(
            engine,
            new ColorTransformRequest(
                displayProfile,
                workingProfile,
                TransformPurpose.InputToWorking),
            encodedDisplaySamples);
        AssertWithinReference(
            expectedWorking,
            actualWorking,
            ReferenceTolerance,
            $"{displaySpaceName} encoded -> linear ACEScg");

        float[] linearWorkingSamples =
        {
            0.18f, 0.22f, 0.28f,
            0.42f, 0.34f, 0.26f,
        };
        float[] expectedDisplay = (float[])linearWorkingSamples.Clone();
        OutputRender.Convert(expectedDisplay, ColorSpaces.AcesCg, displaySpace, GamutMapping.Clip);
        OutputRender.Encode(expectedDisplay, displaySpace);

        float[] actualDisplay = Transform(
            engine,
            new ColorTransformRequest(
                workingProfile,
                BuiltInColorProfiles.For(displaySpace, ProfileRole.Output),
                TransformPurpose.WorkingToOutput),
            linearWorkingSamples);
        AssertWithinReference(
            expectedDisplay,
            actualDisplay,
            ReferenceTolerance,
            $"linear ACEScg -> {displaySpaceName} encoded");
    }

    [Fact]
    public void Cache_reports_hit_miss_and_uses_the_complete_transform_key()
    {
        using var engine = new LittleCmsEngine();
        ColorTransformRequest firstRequest = Request();
        ColorTransformKey firstKey;

        using (IColorTransformLease first = engine.Lease(firstRequest))
            firstKey = first.Key;

        using (IColorTransformLease second = engine.Lease(Request()))
            Assert.Equal(firstKey, second.Key);

        CmmDiagnosticsSnapshot reused = engine.GetDiagnostics();
        Assert.Equal(2, reused.LeaseRequests);
        Assert.Equal(1, reused.CacheMisses);
        Assert.Equal(1, reused.CacheHits);
        Assert.Equal(1, reused.TransformsCreated);
        Assert.Equal(1, reused.CachedTransforms);
        Assert.Equal(firstRequest.Source.Identity, firstKey.Source);
        Assert.Equal(firstRequest.Destination.Identity, firstKey.Destination);
        Assert.Equal(TransformPurpose.Test, firstKey.Purpose);
        Assert.Equal(RenderingIntent.RelativeColorimetric, firstKey.Intent);
        Assert.False(firstKey.BlackPointCompensation);
        Assert.Equal(BitConverter.DoubleToInt64Bits(1.0), firstKey.AdaptationStateBits);
        Assert.Equal(PixelFormatDescriptor.RgbFloat32, firstKey.SourceFormat);
        Assert.Equal(PixelFormatDescriptor.RgbFloat32, firstKey.DestinationFormat);
        Assert.Equal(2190, firstKey.EncodedCmmVersion);
        Assert.Equal(LittleCmsEngine.FixedTransformFlags, firstKey.EffectiveFlags);

        ColorTransformKey differentPurpose;
        using (IColorTransformLease purposeLease = engine.Lease(Request(TransformPurpose.Proof)))
            differentPurpose = purposeLease.Key;
        Assert.NotEqual(firstKey, differentPurpose);

        ColorTransformKey withBpc;
        using (IColorTransformLease bpcLease = engine.Lease(Request(blackPointCompensation: true)))
            withBpc = bpcLease.Key;
        Assert.NotEqual(firstKey, withBpc);
        Assert.True(withBpc.EffectiveFlags.HasFlag(CmmTransformFlags.BlackPointCompensation));

        CmmDiagnosticsSnapshot final = engine.GetDiagnostics();
        Assert.Equal(4, final.LeaseRequests);
        Assert.Equal(3, final.CacheMisses);
        Assert.Equal(1, final.CacheHits);
        Assert.Equal(3, final.TransformsCreated);
        Assert.Equal(3, final.CachedTransforms);
        Assert.Equal(0, final.ActiveOperations);
    }

    [Fact]
    public void Same_cached_transform_cannot_be_leased_twice_and_lease_disposal_is_idempotent()
    {
        using var engine = new LittleCmsEngine();
        IColorTransformLease first = engine.Lease(Request());

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => engine.Lease(Request()));
        Assert.Contains("active lease", error.Message, StringComparison.OrdinalIgnoreCase);

        first.Dispose();
        first.Dispose();
        using IColorTransformLease afterRelease = engine.Lease(Request());

        CmmDiagnosticsSnapshot diagnostics = engine.GetDiagnostics();
        Assert.Equal(3, diagnostics.LeaseRequests);
        Assert.Equal(1, diagnostics.CacheMisses);
        Assert.Equal(2, diagnostics.CacheHits);
        Assert.Equal(1, diagnostics.ActiveOperations);
    }

    [Fact]
    public async Task Lease_apply_is_rejected_on_a_different_managed_thread()
    {
        using var engine = new LittleCmsEngine();
        using IColorTransformLease lease = engine.Lease(Request());
        float[] source = { 0.18f, 0.42f, 0.73f };
        float[] destination = new float[3];

        lease.Apply(source, destination, pixelCount: 1);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Task.Run(() => lease.Apply(source, destination, pixelCount: 1)));

        Assert.Contains("thread-affine", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("acquired", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dispose_is_deterministic_and_rejects_an_active_same_thread_lease()
    {
        var engine = new LittleCmsEngine();
        IColorTransformLease lease = engine.Lease(Request());

        InvalidOperationException active = Assert.Throws<InvalidOperationException>(engine.Dispose);
        Assert.Contains("Dispose the lease first", active.Message, StringComparison.OrdinalIgnoreCase);

        lease.Dispose();
        engine.Dispose();
        engine.Dispose();

        Assert.Throws<ObjectDisposedException>(() => engine.GetDiagnostics());
        Assert.Throws<ObjectDisposedException>(() => engine.Validate(
            BuiltInColorProfiles.Srgb(ProfileRole.Input)));
        Assert.Throws<ObjectDisposedException>(() => engine.Lease(Request()));
    }

    [Fact]
    public async Task Concurrent_threads_receive_independent_contexts_and_cached_transforms()
    {
        const int workerCount = 4;
        using var engine = new LittleCmsEngine();
        using var ready = new CountdownEvent(workerCount);
        using var start = new ManualResetEventSlim(initialState: false);
        ColorTransformRequest request = Request();
        float[] source = { 0.18f, 0.42f, 0.73f, 0.62f, 0.48f, 0.31f };

        Task<WorkerResult>[] workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Factory.StartNew(
                () =>
                {
                    ready.Signal();
                    start.Wait();
                    using IColorTransformLease lease = engine.Lease(request);
                    float[] destination = new float[source.Length];
                    lease.Apply(source, destination, pixelCount: source.Length / 3);
                    return new WorkerResult(
                        Environment.CurrentManagedThreadId,
                        lease.Key,
                        destination);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();

        bool allWorkersReady = ready.Wait(TimeSpan.FromSeconds(15));
        start.Set();
        WorkerResult[] results = await Task.WhenAll(workers);
        Assert.True(allWorkersReady, "Dedicated LittleCMS workers did not start within 15 seconds.");

        Assert.Equal(workerCount, results.Select(result => result.ManagedThreadId).Distinct().Count());
        Assert.All(results, result => Assert.Equal(results[0].Key, result.Key));
        Assert.All(results.Skip(1), result => Assert.Equal(results[0].Output, result.Output));

        CmmDiagnosticsSnapshot diagnostics = engine.GetDiagnostics();
        Assert.Equal(workerCount, diagnostics.ThreadCaches);
        Assert.Equal(workerCount, diagnostics.CacheMisses);
        Assert.Equal(0, diagnostics.CacheHits);
        Assert.Equal(workerCount, diagnostics.TransformsCreated);
        Assert.Equal(workerCount, diagnostics.CachedTransforms);
        Assert.Equal(workerCount, diagnostics.TransformCalls);
        Assert.Equal(workerCount * (source.Length / 3), diagnostics.PixelsTransformed);
        Assert.Equal(0, diagnostics.ActiveOperations);
    }

    private static IEnumerable<ColorProfileRef> FiveBuiltInProfiles()
    {
        yield return BuiltInColorProfiles.Srgb(ProfileRole.Input);
        yield return BuiltInColorProfiles.DisplayP3(ProfileRole.Input);
        yield return BuiltInColorProfiles.AdobeRgb(ProfileRole.Input);
        yield return BuiltInColorProfiles.Rec709(ProfileRole.Input);
        yield return BuiltInColorProfiles.LinearAcesCg(ProfileRole.Working);
    }

    private static ColorTransformRequest Request(
        TransformPurpose purpose = TransformPurpose.Test,
        bool blackPointCompensation = false) => new(
            BuiltInColorProfiles.DisplayP3(ProfileRole.Input),
            BuiltInColorProfiles.LinearAcesCg(ProfileRole.Working),
            purpose,
            RenderingIntent.RelativeColorimetric,
            blackPointCompensation);

    private static float[] Transform(
        LittleCmsEngine engine,
        ColorTransformRequest request,
        float[] source)
    {
        float[] destination = new float[source.Length];
        using IColorTransformLease lease = engine.Lease(request);
        lease.Apply(source, destination, source.Length / 3);
        return destination;
    }

    private static void AssertWithinReference(
        float[] expected,
        float[] actual,
        float tolerance,
        string label)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            float difference = MathF.Abs(expected[i] - actual[i]);
            Assert.True(
                float.IsFinite(actual[i]) && difference <= tolerance,
                $"{label} sample {i}: expected={expected[i]:R}, actual={actual[i]:R}, " +
                $"abs-error={difference:R}, tolerance={tolerance:R}");
        }
    }

    private static string CopyBundleToTemporaryDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"OpenRevelare-lcms-M1-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.Copy(ManifestPath, Path.Combine(directory, "lcms2.manifest.json"));
        File.Copy(NativePath, Path.Combine(directory, NativeFileName));
        return directory;
    }

    private static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string ManifestPath =>
        Path.Combine(AppContext.BaseDirectory, "lcms2.manifest.json");

    private static string NativePath =>
        Path.Combine(AppContext.BaseDirectory, NativeFileName);

    private static string NativeFileName =>
        OperatingSystem.IsWindows() ? "lcms2.dll"
        : OperatingSystem.IsLinux() ? "liblcms2.so"
        : OperatingSystem.IsMacOS() ? "liblcms2.dylib"
        : throw new PlatformNotSupportedException();

    private sealed record WorkerResult(
        int ManagedThreadId,
        ColorTransformKey Key,
        float[] Output);
}
