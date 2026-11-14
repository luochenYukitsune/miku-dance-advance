using System.Numerics;
using System.Runtime.InteropServices;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Engine-defined numeric defaults, not model images. Three distinct 257-texel
/// rows emulate the pinned RGBA16F linear/clamp ColorBand representation.</summary>
internal static class D3D12EndfieldShadowLut
{
    private static readonly Vector4[] Table = Create();
    internal static ReadOnlySpan<Vector4> Values => Table;
    internal static ReadOnlySpan<byte> Bytes => MemoryMarshal.AsBytes(Table.AsSpan());

    private static Vector4[] Create()
    {
        Vector4[] result = new Vector4[3*257];
        for(int row=0;row<3;row++)
        {
            Vector3 left=row==2?Vector3.One:new(.5f);
            Vector3 middle=row switch {0=>new(.800000011920929f),1=>new(1,.8531801104545593f,.6814245581626892f),_=>new(1,.8999999761581421f,.75f)};
            for(int i=0;i<=256;i++)
            {
                float x=i/256f;
                float factor=x<=.5f?(x-.5f)/-.5f:(x-1)/-.5f;
                Vector3 a=x<=.5f?middle:Vector3.One,b=x<=.5f?left:middle;
                Vector3 color=(1-factor)*a+factor*b;
                result[row*257+i]=new((float)(Half)color.X,(float)(Half)color.Y,(float)(Half)color.Z,1);
            }
        }
        return result;
    }
}
