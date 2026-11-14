namespace Mmd.Modern.Tests;

/// <summary>Independent double-precision test oracle, not a product CPU renderer.
/// Opaque RGBA8; bilinear clamp occurs before squaring, X is quantized before vertical filtering.
/// GPU UNorm ties can require a justified one-byte tolerance; never use this oracle to change product output.</summary>
internal static class DiffusionReferenceImage
{
    internal static byte[] Apply(byte[] rgba,int width,int height,double strength,double extent)
    {
        if(width<1||height<1||rgba.Length!=checked(width*height*4))throw new ArgumentException("Invalid fixture size.");
        if(!double.IsFinite(strength)||strength<0||strength>1||!double.IsFinite(extent)||extent<0||extent>.01)
            throw new ArgumentOutOfRangeException(nameof(strength));
        for(int p=3;p<rgba.Length;p+=4)if(rgba[p]!=255)throw new ArgumentException("Opaque oracle only.");
        if(strength==0)return (byte[])rgba.Clone();
        var weights=Enumerable.Range(-7,15).Select(i=>Math.Exp(-.5*Math.Pow(i/3.5,2))).ToArray();
        double denominator=weights.Sum();
        double step=extent*height;
        var intermediate=new byte[rgba.Length];
        var output=new byte[rgba.Length];
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
        {
            int p=(y*width+x)*4;
            for(int c=0;c<3;c++)
            {
                double sum=0;
                for(int i=-7;i<=7;i++)
                {
                    double sampled=Sample(rgba,width,height,x+i*step,y,c);
                    sum+=weights[i+7]*sampled*sampled;
                }
                intermediate[p+c]=Quantize(sum/denominator);
            }
            intermediate[p+3]=255;
        }
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
        {
            int p=(y*width+x)*4;
            var squared=new double[3];
            var screen=new double[3];
            for(int c=0;c<3;c++)
            {
                double sum=0;
                for(int i=-7;i<=7;i++)sum+=weights[i+7]*Sample(intermediate,width,height,x,y+i*step,c);
                double original=rgba[p+c]/255d;
                squared[c]=original*original;
                double blur=sum/denominator;
                screen[c]=blur+squared[c]-blur*squared[c];
            }
            bool replace=Luma(squared)>Luma(screen);
            for(int c=0;c<3;c++)
            {
                double original=rgba[p+c]/255d;
                output[p+c]=Quantize(original+strength*((replace?squared[c]:screen[c])-original));
            }
            output[p+3]=255;
        }
        return output;
    }

    private static double Luma(double[] rgb)=>rgb[0]*.29+rgb[1]*.59+rgb[2]*.12;
    private static byte Quantize(double value)=>(byte)Math.Round(Math.Clamp(value,0,1)*255,MidpointRounding.ToEven);
    // x/y use pixel-center coordinates (integer zero is the first texel center), not texel corners.
    private static double Sample(byte[] image,int width,int height,double x,double y,int channel)
    {
        int x0=(int)Math.Floor(x),y0=(int)Math.Floor(y);
        double fx=x-x0,fy=y-y0;
        double P(int xx,int yy)=>image[(Math.Clamp(yy,0,height-1)*width+Math.Clamp(xx,0,width-1))*4+channel]/255d;
        double a=P(x0,y0)*(1-fx)+P(x0+1,y0)*fx;
        double b=P(x0,y0+1)*(1-fx)+P(x0+1,y0+1)*fx;
        return a*(1-fy)+b*fy;
    }
}
