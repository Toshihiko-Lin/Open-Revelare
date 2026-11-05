namespace OpenRevelare.Core;

public static class EmbeddedLensCorrections
{
    public static void ApplyVignetting(ImageBuffer image, EmbeddedLensCalibration c, FrameRegion? sourceRegion = null)
    {
        if (!c.HasVignette) return;
        var region = sourceRegion ?? FrameRegion.Whole(image.Width,image.Height);
        var (cx,cy,radius) = EmbeddedLensCalibration.Coordinates(region.FrameWidth,region.FrameHeight,c.VignetteCenterX,c.VignetteCenterY);
        Parallel.For(0,image.Height,y => {
            for (int x=0; x<image.Width; x++) {
                double dx=x+region.OffsetX-cx,dy=y+region.OffsetY-cy;
                float gain=(float)c.Gain(Math.Sqrt(dx*dx+dy*dy)/radius);
                int i=(y*image.Width+x)*3;
                for(int ch=0;ch<3;ch++) image.Data[i+ch]*=gain;
            }
        });
    }

    /// <summary>Conservative interval bounds, including interior extrema and all color planes.</summary>
    public static (double X0,double Y0,double X1,double Y1) SourceBounds(
        double x0,double y0,double x1,double y1,EmbeddedLensCalibration c,int width,int height)
    {
        var (cx,cy,n)=EmbeddedLensCalibration.Coordinates(width,height,c.CenterX,c.CenterY);
        (double L,double H) Add((double L,double H) a,(double L,double H) b)=>(a.L+b.L,a.H+b.H);
        (double L,double H) Mul((double L,double H) a,(double L,double H) b) {
            double[] v=[a.L*b.L,a.L*b.H,a.H*b.L,a.H*b.H]; return(v.Min(),v.Max());
        }
        (double L,double H) Scale((double L,double H) a,double k)=>Mul(a,(k,k));
        (double L,double H) Square((double L,double H) a)=>
            (a.L<=0&&a.H>=0?0:Math.Min(a.L*a.L,a.H*a.H),Math.Max(a.L*a.L,a.H*a.H));
        var dx=((Math.Clamp(x0,0,width-1)-cx)/n,(Math.Clamp(x1,0,width-1)-cx)/n);
        var dy=((Math.Clamp(y0,0,height-1)-cy)/n,(Math.Clamp(y1,0,height-1)-cy)/n);
        var dx2=Square(dx); var dy2=Square(dy); var r2=Add(dx2,dy2);
        double minX=double.PositiveInfinity,minY=minX,maxX=double.NegativeInfinity,maxY=maxX;
        int planes=c.Warp?.Length??1;
        for(int plane=0;plane<planes;plane++) {
            (double L,double H) radial=(1,1),tx=(0,0),ty=(0,0);
            if(c.Warp is {} warp) {
                var k=warp[plane]; radial=(k[3],k[3]);
                for(int j=2;j>=0;j--) radial=Add((k[j],k[j]),Mul(r2,radial));
                tx=Add(Scale(Mul(dx,dy),2*k[4]),Scale(Add(r2,Scale(dx2,2)),k[5]));
                ty=Add(Scale(Mul(dx,dy),2*k[5]),Scale(Add(r2,Scale(dy2,2)),k[4]));
            } else if(c.DistortionKnots is {} knots) {
                double r0=Math.Sqrt(r2.L),r1=Math.Sqrt(r2.H);
                var gains=new List<double>{EmbeddedLensCalibration.Interpolate(knots,r0),EmbeddedLensCalibration.Interpolate(knots,r1)};
                for(int i=0;i<knots.Length;i++) if((double)i/(knots.Length-1)>=r0&&(double)i/(knots.Length-1)<=r1) gains.Add(knots[i]);
                radial=(gains.Min(),gains.Max());
            }
            var xs=Add(Mul(dx,radial),tx); var ys=Add(Mul(dy,radial),ty);
            minX=Math.Min(minX,cx+n*xs.L);maxX=Math.Max(maxX,cx+n*xs.H);
            minY=Math.Min(minY,cy+n*ys.L);maxY=Math.Max(maxY,cy+n*ys.H);
        }
        return(minX,minY,maxX,maxY);
    }

    public static ImageBuffer Apply(ImageBuffer image, FrameParams p, FrameRegion? sourceRegion = null)
    {
        if(p.EmbeddedLens is not { IsUsable:true } c) return image;
        var region=sourceRegion??FrameRegion.Whole(image.Width,image.Height);
        var src=new ImageBuffer(image.Width,image.Height,(float[])image.Data.Clone()).InheritSourceFrom(image);
        if(p.LccFlatField is {} lcc) Lcc.Apply(src.Data,src.Width,src.Height,lcc,region);
        else ApplyVignetting(src,c,region);
        if(!c.HasDistortion) return src;
        var output=new ImageBuffer(src.Width,src.Height).InheritSourceFrom(image);
        Parallel.For(0,src.Height,y=>{
            for(int x=0;x<src.Width;x++) for(int ch=0;ch<3;ch++) {
                var mapped=c.Map(x+region.OffsetX,y+region.OffsetY,ch,region.FrameWidth,region.FrameHeight);
                double sx=Math.Clamp(mapped.X-region.OffsetX,0,src.Width-1),sy=Math.Clamp(mapped.Y-region.OffsetY,0,src.Height-1);
                int x0=(int)sx,y0=(int)sy,x1=Math.Min(x0+1,src.Width-1),y1=Math.Min(y0+1,src.Height-1);
                double fx=sx-x0,fy=sy-y0;
                output.Data[(y*src.Width+x)*3+ch]=(float)(
                    (src.Data[(y0*src.Width+x0)*3+ch]*(1-fx)+src.Data[(y0*src.Width+x1)*3+ch]*fx)*(1-fy)
                    +(src.Data[(y1*src.Width+x0)*3+ch]*(1-fx)+src.Data[(y1*src.Width+x1)*3+ch]*fx)*fy);
            }
        });
        return output;
    }
}
