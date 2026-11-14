using System.Numerics;

namespace Mmd.Modern.Core;

public sealed record RenderedFrame(int Width, int Height, byte[] Rgba, string Backend, string Device, int DrawCalls);
public interface IRenderBackend : IDisposable
{
    RenderedFrame Render(SceneSnapshot scene, int width, int height);
}
public readonly record struct PhysicsBodyId(int Value);
public interface IPhysicsBackend : IDisposable
{
    PhysicsBodyId AddDynamicSphere(Vector3 position, float radius, float mass);
    void AddGround(float y);
    void Step(float deltaTime);
    Vector3 GetPosition(PhysicsBodyId body);
    void Reset();
}
