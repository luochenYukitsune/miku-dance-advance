using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using global::Veldrid;
using global::Veldrid.SPIRV;

namespace Mmd.Modern.Rendering.Veldrid;

/// <summary>Owns an actual offscreen device. Frames are copied to host memory for Avalonia presentation.</summary>
public sealed class VeldridRenderBackend : IRenderBackend
{
    private readonly GraphicsDevice device;
    private bool disposed;

    public VeldridRenderBackend(GraphicsBackend? backend = null)
    {
        var selected = backend ?? (OperatingSystem.IsWindows() ? GraphicsBackend.Direct3D11 : GraphicsBackend.Vulkan);
        var options = new GraphicsDeviceOptions(debug: false, swapchainDepthFormat: null, syncToVerticalBlank: false, ResourceBindingModel.Improved, preferDepthRangeZeroToOne: true, preferStandardClipSpaceYDirection: true);
        device = selected switch
        {
            GraphicsBackend.Direct3D11 => GraphicsDevice.CreateD3D11(options),
            GraphicsBackend.Vulkan => GraphicsDevice.CreateVulkan(options),
            _ => throw new NotSupportedException("This offscreen bootstrap explicitly supports D3D11 or Vulkan device creation.")
        };
    }

    public RenderedFrame Render(SceneSnapshot scene, int width, int height)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (width < 16 || height < 16 || width > 4096 || height > 4096) throw new ArgumentOutOfRangeException(nameof(width));
        var resources = new List<IDisposable>();
        T Own<T>(T item) where T : IDisposable { resources.Add(item); return item; }
        try
        {
            var factory = device.ResourceFactory;
            var color = Own(factory.CreateTexture(TextureDescription.Texture2D((uint)width, (uint)height, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.RenderTarget)));
            var depth = Own(factory.CreateTexture(TextureDescription.Texture2D((uint)width, (uint)height, 1, 1, PixelFormat.R32_Float, TextureUsage.DepthStencil)));
            var staging = Own(factory.CreateTexture(TextureDescription.Texture2D((uint)width, (uint)height, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Staging)));
            var framebuffer = Own(factory.CreateFramebuffer(new FramebufferDescription(depth, color)));
            var rotation = scene.World;
            Matrix4x4.Invert(rotation, out var inverse);
            var normalTransform = Matrix4x4.Transpose(inverse);
            var vertices = new GpuVertex[scene.Vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = new(Vector3.Transform(scene.Vertices[i].Position, rotation), Vector3.TransformNormal(scene.Vertices[i].Normal, normalTransform));
            var vb = vertices.Length==0 ? null : Own(factory.CreateBuffer(new BufferDescription((uint)(vertices.Length * 24), BufferUsage.VertexBuffer)));
            var ib = scene.Indices.IsEmpty ? null : Own(factory.CreateBuffer(new BufferDescription((uint)(scene.Indices.Length * 4), BufferUsage.IndexBuffer)));
            if(vb is not null)device.UpdateBuffer(vb, 0, vertices);
            if(ib is not null)device.UpdateBuffer(ib, 0, scene.Indices.ToArray());
            var layout = Own(factory.CreateResourceLayout(new ResourceLayoutDescription(new ResourceLayoutElementDescription("Parameters", ResourceKind.UniformBuffer, ShaderStages.Vertex | ShaderStages.Fragment))));
            // SPIRV-Cross emits main for D3D11; Vulkan uses the original SPIR-V entry points.
            var shaders = factory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, ShaderBytes("model.vert.spv"), device.BackendType == GraphicsBackend.Vulkan ? "VSMain" : "main"),
                new ShaderDescription(ShaderStages.Fragment, ShaderBytes("model.frag.spv"), device.BackendType == GraphicsBackend.Vulkan ? "PSMain" : "main"));
            foreach (var shader in shaders) Own(shader);
            var vertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("Normal", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3));
            var pipeline = Own(factory.CreateGraphicsPipeline(new GraphicsPipelineDescription(
                BlendStateDescription.SingleOverrideBlend,
                DepthStencilStateDescription.DepthOnlyLessEqual,
                new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.CounterClockwise, depthClipEnabled: true, scissorTestEnabled: false),
                PrimitiveTopology.TriangleList,
                new ShaderSetDescription([vertexLayout], shaders), [layout], framebuffer.OutputDescription)));
            // System.Numerics uses row vectors. The HLSL cbuffer is explicitly row_major, and mul(v,M).
            var transform = scene.Camera.ViewProjection(width, height);
            var sets = new ResourceSet[scene.Materials.Count];
            for (int i = 0; i < sets.Length; i++)
            {
                var uniform = Own(factory.CreateBuffer(new BufferDescription(80, BufferUsage.UniformBuffer)));
                device.UpdateBuffer(uniform, 0, new Parameters(transform, scene.Materials[i].Diffuse));
                sets[i] = Own(factory.CreateResourceSet(new ResourceSetDescription(layout, uniform)));
            }
            var commands = Own(factory.CreateCommandList());
            commands.Begin(); commands.SetFramebuffer(framebuffer);
            commands.ClearColorTarget(0, new RgbaFloat(20f / 255, 24f / 255, 32f / 255, 1));
            commands.ClearDepthStencil(1); commands.SetPipeline(pipeline);
            if(vb is not null && ib is not null){commands.SetVertexBuffer(0, vb); commands.SetIndexBuffer(ib, IndexFormat.UInt32);}
            int draws = 0;
            for (int i = 0; i < sets.Length; i++)
            {
                var material = scene.Materials[i];
                if (material.IndexCount == 0) continue;
                commands.SetGraphicsResourceSet(0, sets[i]);
                commands.DrawIndexed((uint)material.IndexCount, 1, (uint)material.StartIndex, 0, 0); draws++;
            }
            commands.CopyTexture(color, staging); commands.End(); device.SubmitCommands(commands); device.WaitForIdle();
            var mapping = device.Map(staging, MapMode.Read);
            var rgba = new byte[width * height * 4];
            try
            {
                for (int y = 0; y < height; y++)
                    Marshal.Copy(IntPtr.Add(mapping.Data, checked(y * (int)mapping.RowPitch)), rgba, y * width * 4, width * 4);
            }
            finally { device.Unmap(staging); }
            return new RenderedFrame(width, height, rgba, device.BackendType.ToString(), device.DeviceName, draws);
        }
        finally { for (int i = resources.Count - 1; i >= 0; i--) resources[i].Dispose(); }
    }

    private static byte[] ShaderBytes(string suffix)
    {
        var assembly = typeof(VeldridRenderBackend).Assembly;
        using var source = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith(suffix, StringComparison.Ordinal)))!;
        using var buffer = new MemoryStream(); source.CopyTo(buffer); return buffer.ToArray();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; device.WaitForIdle(); device.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct GpuVertex(Vector3 Position, Vector3 Normal);
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Parameters(Matrix4x4 ViewProjection, Vector4 Diffuse);
}
