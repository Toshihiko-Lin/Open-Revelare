using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class FilmAutoCropRegressionTests
{
    [Theory]
    [InlineData(false, -1.2, false)]
    [InlineData(false, -1.2, true)]
    [InlineData(true, 1.8, true)]
    [InlineData(true, -2.0, false)]
    public void Dark_raw_values_with_saved_rotation_and_flip_keep_the_perforation_ruler(bool positive, double angle, bool flip)
    {
        // Display-white rotation corners overwhelmed the 99th percentile in dim RAWs. The
        // fallback could then crop the film/holder rectangle, as in the real DSC01913 failure.
        var source = Geometry.ApplyRotation(FilmAutoCropTests.Perforated(positive), angle, 0);
        for (int i=0;i<source.Data.Length;i++) source.Data[i] *= .02f;
        var parameters = new FrameParams { FlipH = flip, Rotation = flip ? angle : -angle };
        var d = FilmAutoCrop.Detect(FilmAutoCrop.Prepare(source, parameters));
        Assert.NotNull(d); Assert.True(d.HasPerforationRuler);
        Assert.Equal(positive ? FilmPolarity.Positive : FilmPolarity.Negative, d.Polarity);
        Assert.InRange(d.Image.W * 640, 345, 370);
        Assert.InRange(d.Image.X + d.Image.W/2, (flip ? 310d : 330d)/640 - .02, (flip ? 310d : 330d)/640 + .02);
    }

    [Fact]
    public void Thin_negative_base_is_not_merged_into_the_bright_perforations()
    {
        var source=FilmAutoCropTests.Perforated(false);
        for(int y=65;y<415;y++) for(int x=0;x<640;x++)
        {
            if(x>=150 && x<510 && y>=120 && y<360) continue;
            int at=(y*640+x)*3;
            if(source.Data[at]>.85) continue;
            for(int c=0;c<3;c++) source.Data[at+c]=.86f;
        }
        var d=FilmAutoCrop.Detect(source);
        Assert.NotNull(d);Assert.True(d.HasPerforationRuler);
        Assert.InRange(d.Image.X*640,145,155);Assert.InRange(d.Image.W*640,345,370);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Scene_touching_the_top_and_bottom_base_does_not_destroy_the_frame_location(bool positive)
    {
        var source=FilmAutoCropTests.Perforated(positive);
        float value=positive ? .025f : .65f;
        for(int y=120;y<360;y++) for(int x=150;x<510;x++)
            if(y<130 || y>=350)
            {
                int at=(y*640+x)*3;source.Data[at]=value;source.Data[at+1]=value*.6f;source.Data[at+2]=value*.25f;
            }
        var d=FilmAutoCrop.Detect(source);
        Assert.NotNull(d);Assert.True(d.HasPerforationRuler);
        Assert.InRange(d.Image.X*640,145,155);Assert.InRange(d.Image.W*640,345,370);
    }

    [Fact]
    public void Common_content_size_fits_every_reliable_frame_instead_of_skipping_smaller_exposures()
    {
        var detections=new[] {
            new FilmAutoCrop.Detection(new(.2,.25,.5,.5),1,6000,4000),
            new FilmAutoCrop.Detection(new(.23,.24,.49,.47),1,6000,4000),
            new FilmAutoCrop.Detection(new(.21,.23,.505,.50),1,6000,4000) };
        var plan=FilmAutoCrop.Plan(detections,FilmCropMode.ImageArea);
        Assert.All(plan,r=>Assert.NotNull(r));
        Assert.Equal((plan[0]!.Value.W,plan[0]!.Value.H),(plan[2]!.Value.W,plan[2]!.Value.H));
        Assert.True(plan[0]!.Value.W>=.5*.99);
        for(int i=0;i<plan.Count;i++)
        {
            var crop=plan[i]!.Value;var frame=detections[i].Image;
            Assert.Equal(1.5,crop.W*6000/(crop.H*4000),10);
            Assert.True(crop.X>=frame.X-.5/6000 && crop.Y>=frame.Y-.5/4000);
            Assert.True(crop.X+crop.W<=frame.X+frame.W+.5/6000 && crop.Y+crop.H<=frame.Y+frame.H+.5/4000);
        }
    }

    [Fact]
    public void Positive_and_negative_rolls_in_same_batch_use_separate_tight_content_sizes()
    {
        var detections=new[] {
            new FilmAutoCrop.Detection(new(.2,.25,3384d/7008,2306d/4672),1,7008,4672){Polarity=FilmPolarity.Positive},
            new FilmAutoCrop.Detection(new(.21,.25,3420d/7008,2288d/4672),1,7008,4672){Polarity=FilmPolarity.Positive},
            new FilmAutoCrop.Detection(new(.19,.24,3444d/7008,2300d/4672),1,7008,4672){Polarity=FilmPolarity.Negative},
            new FilmAutoCrop.Detection(new(.20,.24,3456d/7008,2312d/4672),1,7008,4672){Polarity=FilmPolarity.Negative} };
        var plan=FilmAutoCrop.Plan(detections,FilmCropMode.ImageArea);
        Assert.All(plan,r=>Assert.NotNull(r));
        Assert.Equal(plan[0]!.Value.W,plan[1]!.Value.W);
        Assert.Equal(plan[2]!.Value.W,plan[3]!.Value.W);
        Assert.True(plan[2]!.Value.W>plan[0]!.Value.W);
        Assert.True(plan[0]!.Value.W<=detections[0].Image.W && plan[0]!.Value.W>=detections[0].Image.W*.985);
    }

    [Fact]
    public void Internal_detection_downsample_does_not_change_reported_source_size()
    {
        var small=FilmAutoCropTests.Perforated(false);var large=new ImageBuffer(1280,960);
        for(int y=0;y<960;y++) for(int x=0;x<1280;x++) for(int c=0;c<3;c++)
            large.Data[(y*1280+x)*3+c]=small.Data[((y/2)*640+x/2)*3+c];
        var d=FilmAutoCrop.Detect(large);
        Assert.NotNull(d);Assert.Equal(1280,d.Width);Assert.Equal(960,d.Height);
    }
    [Fact]
    public void Burned_leader_with_two_different_bases_does_not_get_a_confident_frame()
    {
        var source=FilmAutoCropTests.Perforated(true);
        for(int y=65;y<415;y++) for(int x=0;x<320;x++)
        {
            int at=(y*640+x)*3;
            if(source.Data[at]>.85f) continue;
            float value=x>=150 && y>=120 && y<360 ? .68f : .65f;
            source.Data[at]=value;source.Data[at+1]=value*.6f;source.Data[at+2]=value*.25f;
        }
        Assert.Null(FilmAutoCrop.Detect(source));
    }

    [Fact]
    public void Near_black_slide_base_variation_is_not_a_burned_leader()
    {
        var source=FilmAutoCropTests.Perforated(true);
        for(int y=65;y<415;y++) for(int x=0;x<640;x++)
        {
            int at=(y*640+x)*3;
            if(source.Data[at]>.1f) continue;
            float value=x<320 ? .00003f : .0002f;
            source.Data[at]=value;source.Data[at+1]=value*.6f;source.Data[at+2]=value*.25f;
        }
        var d=FilmAutoCrop.Detect(source);
        Assert.NotNull(d);Assert.True(d.HasPerforationRuler);
        Assert.InRange(d.Image.X*640,145,155);
    }

    [Fact]
    public void Optical_preparation_does_not_write_through_the_shared_decoded_preview()
    {
        var source=FilmAutoCropTests.Perforated(false);var original=(float[])source.Data.Clone();
        var parameters=new FrameParams {DistortionK1=.017,VignetteAmount=.3,VignetteFalloff=2.5};
        var prepared=FilmAutoCrop.Prepare(source,parameters);
        Assert.Equal(original,source.Data);Assert.NotSame(source,prepared);
        parameters.VignetteAmount=0;
        Assert.Equal(prepared.Data,FilmAutoCrop.Prepare(source,parameters).Data);
        Assert.NotNull(FilmAutoCrop.Detect(prepared));
    }

    [Fact]
    public void Gradual_optical_burned_leader_does_not_get_a_confident_frame()
    {
        var source = FilmAutoCropTests.Perforated(true);
        for (int y = 65; y < 415; y++) for (int x = 0; x < 640; x++)
        {
            int at = (y * 640 + x) * 3;
            if (source.Data[at] > .85f) continue;
            double burn = Math.Clamp((360.0 - x) / 60.0, 0, 1);
            bool rightScene = x >= 355 && x < 510 && y >= 120 && y < 360;
            float unburned = rightScene ? .34f : .02f;
            float value = (float)(burn * .66 + (1 - burn) * unburned);
            source.Data[at] = value; source.Data[at + 1] = value * .6f; source.Data[at + 2] = value * .25f;
        }
        Assert.Null(FilmAutoCrop.Detect(source));
    }

    [Fact]
    public void Holder_occluded_bright_neighbour_is_not_mistaken_for_a_frame_border()
    {
        var source = FilmAutoCropTests.Perforated(true);
        for (int y = 0; y < 480; y++) for (int x = 0; x < 640; x++)
        {
            int at = (y * 640 + x) * 3;
            // Opaque holder sits 1.8 mm past hole 11 (x=545..564), matching DSC02043/02044.
            if (x >= 582) { source.Data[at] = source.Data[at + 1] = source.Data[at + 2] = .01f; continue; }
            if (y < 65 || y >= 415 || source.Data[at] > .85f) continue;
            bool window = x >= 222 && x < 275 && y >= 120 && y < 360;
            bool neighbour = x >= 445 && x < 582 && y >= 120 && y < 360;
            float value = neighbour ? .45f : window ? .28f : .018f;
            source.Data[at] = value; source.Data[at + 1] = value * .6f; source.Data[at + 2] = value * .25f;
        }
        Assert.Null(FilmAutoCrop.Detect(source));
    }

    [Fact]
    public void Dark_slide_fireworks_flanking_black_sky_do_not_invent_a_negative_frame()
    {
        var source = FilmAutoCropTests.Perforated(true);
        for (int y = 65; y < 415; y++) for (int x = 0; x < 640; x++)
        {
            int at = (y * 640 + x) * 3;
            if (source.Data[at] > .85f) continue;
            bool shoulderFlare = Math.Abs(y - 240) >= 121 && Math.Abs(y - 240) <= 126;
            bool burst = (x >= 180 && x < 235 || x >= 595 && x < 630) && y >= 140 && y < 340;
            float value = burst ? .42f : shoulderFlare ? .012f : .004f;
            source.Data[at] = value; source.Data[at + 1] = value * .6f; source.Data[at + 2] = value * .25f;
        }
        Assert.Null(FilmAutoCrop.Detect(source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Adjacent_half_frames_are_not_merged_into_one_full_frame(bool positive)
    {
        var source = FilmAutoCropTests.Perforated(positive);
        float baseVal = positive ? .025f : .65f;
        for (int y = 120; y < 360; y++) for (int x = 322; x < 338; x++)
        {
            int at = (y * 640 + x) * 3;
            source.Data[at] = baseVal; source.Data[at + 1] = baseVal * .6f; source.Data[at + 2] = baseVal * .25f;
        }
        Assert.Null(FilmAutoCrop.Detect(source));
    }

}
