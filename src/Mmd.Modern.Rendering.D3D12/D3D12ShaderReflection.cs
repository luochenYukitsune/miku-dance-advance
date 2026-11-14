using System.Runtime.InteropServices;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Reads the bindings of the actual compiled SM5 bytecode using the Windows compiler ABI.
/// Built-in passes query compiled sampler and resource bindings without any effect-language dependency.</summary>
internal static class D3D12ShaderReflection
{
    internal sealed record Binding(string Name, int Kind, int Slot);
    internal sealed record Uniform(string Name,int ByteOffset,int ByteSize,int Class,int Rows,int Columns);
    internal sealed record Output(string Semantic,int Index);
    internal sealed record Result(IReadOnlyList<Binding> Resources, IReadOnlyList<Uniform> Uniforms,IReadOnlyList<Output> Outputs);
    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int D3DReflect(byte[] data, nuint length, in Guid iid, out nint reflection);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDesc(nint self, nint desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetIndexedDesc(nint self, uint index, nint desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate nint GetIndexed(nint self, uint index);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate nint GetChild(nint self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint Release(nint self);
    private static T Method<T>(nint self, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(self), slot * IntPtr.Size));

    internal static Result Read(byte[] bytecode)
    {
        Guid iid = new("8d536ca1-0cca-4956-a837-786963755584");
        Marshal.ThrowExceptionForHR(D3DReflect(bytecode, (nuint)bytecode.Length, iid, out nint reflection));
        nint desc = Marshal.AllocHGlobal(512);
        try
        {
            Marshal.ThrowExceptionForHR(Method<GetDesc>(reflection, 3)(reflection, desc));
            int buffers = Marshal.ReadInt32(desc, 20), bound = Marshal.ReadInt32(desc, 24), outputCount=Marshal.ReadInt32(desc,32);
            var resources = new List<Binding>(); var uniforms = new List<Uniform>();
            var outputs=new List<Output>();
            for(uint i=0;i<outputCount;i++)
            {
                Marshal.ThrowExceptionForHR(Method<GetIndexedDesc>(reflection,8)(reflection,i,desc));
                outputs.Add(new(Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(desc))!,Marshal.ReadInt32(desc,8)));
            }
            for (uint i = 0; i < bound; i++)
            {
                Marshal.ThrowExceptionForHR(Method<GetIndexedDesc>(reflection, 6)(reflection, i, desc));
                resources.Add(new(Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(desc))!, Marshal.ReadInt32(desc, 8), Marshal.ReadInt32(desc, 12)));
            }
            for (uint b = 0; b < buffers; b++)
            {
                nint buffer = Method<GetIndexed>(reflection, 4)(reflection, b);
                Marshal.ThrowExceptionForHR(Method<GetDesc>(buffer, 0)(buffer, desc));
                int variables = Marshal.ReadInt32(desc, 12);
                for (uint v = 0; v < variables; v++)
                {
                    nint variable = Method<GetIndexed>(buffer, 1)(buffer, v);
                    Marshal.ThrowExceptionForHR(Method<GetDesc>(variable, 0)(variable, desc));
                    if ((Marshal.ReadInt32(desc, 16) & 2) != 0)
                    {
                        string name=Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(desc))!;
                        int offset=Marshal.ReadInt32(desc,8),size=Marshal.ReadInt32(desc,12);
                        nint type=Method<GetChild>(variable,1)(variable);
                        Marshal.ThrowExceptionForHR(Method<GetDesc>(type,0)(type,desc));
                        uniforms.Add(new(name,offset,size,Marshal.ReadInt32(desc,0),Marshal.ReadInt32(desc,8),Marshal.ReadInt32(desc,12)));
                    }
                }
            }
            return new(resources, uniforms,outputs);
        }
        finally { Marshal.FreeHGlobal(desc); Method<Release>(reflection, 2)(reflection); }
    }
}
