using OpenRevelare.Core;
using System.Text;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// Recovering the transfer function a Flextight declares in its own settings block.
///
/// The scanner writes an Apple plist into private TIFF tag 50457 and sets EmbedProfile=true while
/// embedding no ICC profile, so the encoding gamma there is the ONLY statement of the transfer
/// function in the file. Reading it wrong is not a subtle error: on the reference scan, treating
/// the samples as linear puts D_max at 0.55/0.69/0.77 against a documented physical range of
/// 1.0–1.5 (see FrameParams), i.e. the entire density scale is off by about half.
///
/// The tests below pin that the value is READ and never guessed — including that an absent or
/// out-of-range declaration produces NO correction rather than a default, which is the difference
/// between "uncharacterised, calibrate it yourself" and "silently wrong".
/// </summary>
public class FlextightMetaTests
{
    /// <summary>Wraps plist XML the way the scanner does: a short binary prefix before the
    /// declaration and zero padding well past the document, since the tag is a fixed-size buffer.
    /// Parsing must survive both.</summary>
    private static byte[] Payload(string xml, int pad = 256, int prefix = 4)
    {
        var body = Encoding.UTF8.GetBytes(xml);
        var buf = new byte[prefix + body.Length + pad];
        body.CopyTo(buf, prefix);
        return buf;
    }

    private static string Plist(string gammaElement, int currentIx = 0, string name = "Negative RGB standard") => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>CurrentIx</key><integer>{currentIx}</integer>
          <key>ImageSettings</key>
          <array>
            <dict>
              <key>ImageCorrection</key>
              <dict>
                <key>Contrast</key><integer>0</integer>
                {gammaElement}
                <key>Saturation</key><integer>15</integer>
              </dict>
              <key>Name</key><string>{name}</string>
            </dict>
            <dict>
              <key>ImageCorrection</key>
              <dict><key>Gamma</key><real>1.8</real></dict>
              <key>Name</key><string>second image</string>
            </dict>
          </array>
        </dict>
        </plist>
        """;

    /// <summary>The declared gamma is read from the plist, not assumed.</summary>
    [Fact]
    public void Reads_declared_gamma()
    {
        var s = FlextightMeta.Parse(Payload(Plist("<key>Gamma</key><real>2.0</real>")));
        Assert.Equal(2.0, s.Gamma);
        Assert.True(s.HasEncodingGamma);
        Assert.Equal("Negative RGB standard", s.ColorSpaceName);
    }

    /// <summary>CurrentIx selects which image's settings are live; the wrong one carries a
    /// different gamma, so picking by index rather than by position must be pinned.</summary>
    [Fact]
    public void Honours_CurrentIx()
    {
        var s = FlextightMeta.Parse(Payload(Plist("<key>Gamma</key><real>2.0</real>", currentIx: 1)));
        Assert.Equal(1.8, s.Gamma);
    }

    /// <summary>Gamma 1.0 IS a valid declaration — the scan is already linear — and must produce
    /// no correction rather than being mistaken for a missing value.</summary>
    [Fact]
    public void Linear_declaration_needs_no_correction()
    {
        var s = FlextightMeta.Parse(Payload(Plist("<key>Gamma</key><real>1.0</real>")));
        Assert.Equal(1.0, s.Gamma);
        Assert.False(s.HasEncodingGamma);
        Assert.Null(FlextightMeta.BuildGammaLuts(1.0));
    }

    /// <summary>No Gamma key: nothing is known, so nothing is applied. A default here would be a
    /// silent guess applied to every pixel.</summary>
    [Fact]
    public void Missing_gamma_yields_no_correction()
    {
        var s = FlextightMeta.Parse(Payload(Plist("<key>Brightness</key><integer>0</integer>")));
        Assert.Null(s.Gamma);
        Assert.False(s.HasEncodingGamma);
    }

    /// <summary>Values outside the admissible band are not transfer functions we understand and
    /// must be rejected rather than clamped into something plausible-looking.</summary>
    [Theory]
    [InlineData("0.2")]
    [InlineData("-2.0")]
    [InlineData("9.5")]
    [InlineData("not-a-number")]
    public void Out_of_range_gamma_is_rejected(string value)
        => Assert.Null(FlextightMeta.Parse(Payload(Plist($"<key>Gamma</key><real>{value}</real>"))).Gamma);

    /// <summary>Malformed, empty and non-plist payloads must never throw — a broken metadata block
    /// cannot be allowed to stop a readable image from opening.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("<?xml version=\"1.0\"?><plist><dict><key>Truncated</key>")]
    [InlineData("just some bytes that are not xml at all, padded out to length")]
    public void Malformed_payload_is_safe(string xml)
    {
        var s = FlextightMeta.Parse(Payload(xml));
        Assert.Null(s.Gamma);
        Assert.False(s.HasEncodingGamma);
    }

    [Fact]
    public void Null_and_short_payloads_are_safe()
    {
        Assert.Null(FlextightMeta.Parse(null).Gamma);
        Assert.Null(FlextightMeta.Parse(new byte[4]).Gamma);
    }

    /// <summary>A file that is not a Flextight scan (and one that does not exist) yields nothing
    /// and does not throw.</summary>
    [Fact]
    public void Non_flextight_file_yields_nothing()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".tif");
        File.WriteAllBytes(path, new byte[] { (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8, 0, 0 });
        try { Assert.Null(FlextightMeta.Read(path).Gamma); } finally { File.Delete(path); }

        Assert.Null(FlextightMeta.Read(Path.Combine(Path.GetTempPath(), "nope.fff")).Gamma);
    }

    /// <summary>
    /// The LUT must actually invert the declared encoding: a value encoded as v**(1/g) has to come
    /// back as v. This is the step that moves D_max from 0.55 to 1.10 on the reference scan, so an
    /// inverted exponent here would be a plausible-looking image with a halved density scale.
    /// </summary>
    [Fact]
    public void Lut_inverts_the_encoding()
    {
        const double gamma = 2.0;
        var luts = FlextightMeta.BuildGammaLuts(gamma);
        Assert.NotNull(luts);
        Assert.Equal(3, luts!.Length);

        foreach (double linear in new[] { 0.05, 0.25, 0.5, 0.75, 1.0 })
        {
            double encoded = Math.Pow(linear, 1.0 / gamma);
            float got = luts[0][(int)(encoded * 65535.0 + 0.5)];
            Assert.InRange(got, linear - 1e-4, linear + 1e-4);
        }

        Assert.Equal(0f, luts[0][0]);            // black stays black
        Assert.Equal(1f, luts[0][65535], 5);     // white stays white
    }
}
