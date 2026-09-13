using System.Runtime.InteropServices;
using OpenRevelare.Presentation.MacOS.Native;
using Xunit;

namespace OpenRevelare.Presentation.MacOS.Tests;

/// <summary>
/// Pins the managed side of the <c>orwm_*</c> ABI to the layout the Objective-C++ header
/// declares. The header carries matching <c>static_assert</c>s; if either side drifts, one of the
/// two fails before a byte crosses.
/// </summary>
public sealed class MacOSNativeAbiTests
{
    [Fact]
    public void Managed_struct_sizes_match_the_native_header()
    {
        Assert.Equal(24, Marshal.SizeOf<NativePresentationContract>());
        // 4+4+4+4 + 3*8 + 8 (reference EDR) + 128 + 128 = 304
        Assert.Equal(304, Marshal.SizeOf<NativeDisplayProbe>());
        // 9*4 = 36, padded to 40 for the ulongs; + 4*8 = 72; + 4 = 76, padded to 80; + 256 = 336
        Assert.Equal(336, Marshal.SizeOf<NativeDiagnostics>());
    }

    [Fact]
    public void Resolver_candidates_are_absolute_owned_paths_and_never_a_bare_search_name()
    {
        IReadOnlyList<string> candidates = MacOSNativeLibraryResolver.CandidatePaths(
            Path.Combine(Path.GetTempPath(), "app"),
            Path.Combine(Path.GetTempPath(), "app", "lib"));

        Assert.NotEmpty(candidates);
        Assert.All(candidates, path =>
        {
            Assert.True(Path.IsPathRooted(path));
            Assert.Equal(MacOSNativeLibraryResolver.LibraryName, Path.GetFileName(path));
        });
        Assert.Equal(candidates.Count, candidates.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Result_codes_shared_with_win32_keep_their_numbers()
    {
        // The two shims' diagnostics are read side by side; a code that means "stale revision" on
        // one platform must not mean "buffer too small" on the other.
        Assert.Equal(-1, (int)NativePresenterResult.InvalidArgument);
        Assert.Equal(-3, (int)NativePresenterResult.InvalidSize);
        Assert.Equal(-4, (int)NativePresenterResult.InvalidRowPitch);
        Assert.Equal(-5, (int)NativePresenterResult.BufferTooSmall);
        Assert.Equal(-7, (int)NativePresenterResult.ContractDisplayMismatch);
        Assert.Equal(-8, (int)NativePresenterResult.StaleRevision);
        Assert.Equal(-11, (int)NativePresenterResult.ColorSpaceFailure);
        Assert.Equal(-13, (int)NativePresenterResult.NotInitialized);
        Assert.Equal(-14, (int)NativePresenterResult.InternalFailure);
    }
}
