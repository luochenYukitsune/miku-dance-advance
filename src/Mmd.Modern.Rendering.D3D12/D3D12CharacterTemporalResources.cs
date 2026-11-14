using Mmd.Modern.Core;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace Mmd.Modern.Rendering.D3D12;

internal sealed partial class D3D12TemporalInputPass
{
    // One replaceable texture set, leased through every using frame. This keeps animated texture replacement
    // bounded instead of accumulating a content-addressed texture cache throughout a long rendering session.
    private sealed class CharacterTextureIdentity(SceneSnapshot scene) : IEquatable<CharacterTextureIdentity>
    {
        private readonly SceneTexture[] textures = scene.Textures.ToArray();
        public bool Equals(CharacterTextureIdentity? other) => other is not null && textures.Length == other.textures.Length
            && textures.Zip(other.textures).All(pair => ReferenceEquals(pair.First, pair.Second));
        public override bool Equals(object? obj) => obj is CharacterTextureIdentity other && Equals(other);
        public override int GetHashCode() => textures.Length;
    }
    private sealed class CharacterTextureSet : IDisposable
    {
        internal Dictionary<int, ID3D12Resource> Resources { get; } = [];
        public void Dispose() { foreach (var texture in Resources.Values) texture.Dispose(); Resources.Clear(); }
    }

    private CharacterTextureSet CreateCharacterTextures(D3D12RecordingContext context, SceneSnapshot scene)
    {
        var result = new CharacterTextureSet();
        try
        {
            Upload(new("temporal-white", 1, 1, [255, 255, 255, 255]), -1);
            // All source descriptors are populated; the toon texture's alpha is deliberately never sampled.
            for (int i = 0; i < scene.Textures.Count; i++) Upload(scene.Textures[i], i);
            return result;
        }
        catch { result.Dispose(); throw; }

        void Upload(SceneTexture texture, int index)
        {
            var device = context.Device;
            var description = ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, (uint)texture.Width, (uint)texture.Height, mipLevels: 1);
            var gpu = device.CreateCommittedResource(HeapType.Default, description, ResourceStates.CopyDest);
            result.Resources.Add(index, gpu);
            var footprint = new PlacedSubresourceFootPrint[1];
            device.GetCopyableFootprints(description, 0, 1, 0, footprint, new int[1], new ulong[1], out ulong size);
            var upload = context.Own(device.CreateCommittedResource(HeapType.Upload, ResourceDescription.Buffer(size), ResourceStates.GenericRead));
            var mapped = upload.Map<byte>(0, checked((int)size));
            for (int y = 0; y < texture.Height; y++) texture.Rgba.Slice(y * texture.Width * 4, texture.Width * 4)
                .CopyTo(mapped.Slice(y * footprint[0].Footprint.RowPitch, texture.Width * 4));
            upload.Unmap(0);
            context.Commands.CopyTextureRegion(new TextureCopyLocation(gpu, 0), 0, 0, 0, new TextureCopyLocation(upload, footprint[0]));
            context.Commands.ResourceBarrierTransition(gpu, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
            runtime.TextureUploads++;
        }
    }
}
