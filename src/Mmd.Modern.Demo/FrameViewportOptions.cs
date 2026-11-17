using System.Globalization;
using Mmd.Modern.Core;

namespace Mmd.Modern.Demo;

public sealed record FrameViewportOptions(bool Enabled = false, string? ModelPath = null, string? ToonDirectory = null,
    bool SelfTest = false, int SelfTestFrames = 320, string? OutputDirectory = null,
    RayGiOptions? RayGi = null, bool RayGiSelfTest = false, bool DemoRoom = false, bool FitShadow = false,
    float RoomEmission = 0, FrameAntialiasing Antialiasing = FrameAntialiasing.Off,string? MotionPath=null,bool AutoPlayMotion=false,string? ProjectPath=null,bool EngineDemo=false)
{
    public static FrameViewportOptions Parse(string[] args)
    {
        if (!args.Contains("--frame-viewer", StringComparer.OrdinalIgnoreCase)) return new();
        string? model = null, toon = null, output = null,motion=null,project=null;
        bool autoPlay=false,engineDemo=false;
        bool selfTest = false, giSelfTest = false, qualitySpecified = false, demoRoom = false, fitShadow = false; int frames = 320, spp = 16;
        float strength = .5f, occlusionStrength = 0, roomEmission = 0; LightingMode giMode = LightingMode.Legacy;
        RayGiComposition giComposition = RayGiComposition.Additive;
        RayGiDenoiser giDenoiser = RayGiDenoiser.Off;
        FrameAntialiasing antialiasing = FrameAntialiasing.Off;
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal)
                ? args[i] : throw new ArgumentException($"{args[i - 1]} requires a value.");
            switch (args[i].ToLowerInvariant())
            {
                case "--frame-viewer": break;
                case "--antialiasing":
                    antialiasing = Value().ToLowerInvariant() switch
                    { "off" => FrameAntialiasing.Off, "smaa" => FrameAntialiasing.Smaa1X,
                        _ => throw new ArgumentException("--antialiasing must be off or smaa.") };
                    break;
                case "--demo-room": demoRoom = true; break;
                case "--room-emission":
                    if (!float.TryParse(Value(), NumberStyles.Float, CultureInfo.InvariantCulture, out roomEmission)
                        || !float.IsFinite(roomEmission) || roomEmission is < 0 or > 1)
                        throw new ArgumentException("--room-emission must be finite and in 0..1.");
                    break;
                case "--shadow-projection":
                    fitShadow = Value().ToLowerInvariant() switch
                    { "legacy" => false, "fit" => true, _ => throw new ArgumentException("--shadow-projection must be legacy or fit.") };
                    break;
                case "--model": model = Value(); break;
                case "--project": project=Value();break;
                case "--engine-demo": engineDemo=true;break;
                case "--motion": motion=Value();break;
                case "--play-motion": autoPlay=true;break;
                case "--toon-dir": toon = Value(); break;
                case "--effect": throw new NotSupportedException("MME is available only in the legacy compatibility renderer; modern D3D12 does not support --effect.");
                case "--viewport-self-test": selfTest = true; break;
                case "--ray-gi-self-test": selfTest = giSelfTest = true; break;
                case "--gi":
                    giMode = Value().ToLowerInvariant() switch
                    {
                        "off" => LightingMode.Legacy, "software" => LightingMode.SoftwareRayTracing,
                        "hardware" => LightingMode.HardwareRayTracing,
                        _ => throw new ArgumentException("--gi must be off, software or hardware.")
                    }; break;
                case "--gi-strength":
                    if (!float.TryParse(Value(), NumberStyles.Float, CultureInfo.InvariantCulture, out strength)
                        || !float.IsFinite(strength) || strength is < 0 or > 100)
                        throw new ArgumentException("--gi-strength must be finite and in 0..100.");
                    break;
                case "--gi-composition":
                    giComposition = Value().ToLowerInvariant() switch
                    { "additive" => RayGiComposition.Additive, "toon" => RayGiComposition.ToonPalette,
                        _ => throw new ArgumentException("--gi-composition must be additive or toon.") };
                    break;
                case "--gi-denoiser":
                    giDenoiser = Value().ToLowerInvariant() switch
                    { "off" => RayGiDenoiser.Off, "spatial" => RayGiDenoiser.Spatial,
                        _ => throw new ArgumentException("--gi-denoiser must be off or spatial.") };
                    break;
                case "--gi-occlusion":
                    occlusionStrength = Value().ToLowerInvariant() switch
                    { "on" => 1, "off" => 0, _ => throw new ArgumentException("--gi-occlusion must be on or off.") };
                    break;
                case "--gi-quality":
                    qualitySpecified = true;
                    spp = Value().ToLowerInvariant() switch
                    { "low" => 4, "medium" => 16, "high" => 64, _ => throw new ArgumentException("--gi-quality must be low, medium or high.") };
                    break;
                case "--self-test-frames":
                    if (!int.TryParse(Value(), out frames) || frames is < 300 or > 3000) throw new ArgumentException("Self-test frame count must be in 300..3000.");
                    break;
                case "--output": output = Value(); break;
                default: throw new ArgumentException($"Unknown frame-viewer argument '{args[i]}'.");
            }
        }
        if(project is not null&&(model is not null||motion is not null||demoRoom))throw new ArgumentException("--project cannot be combined with --model, --motion or --demo-room.");
        if(engineDemo&&(model is not null||project is not null||motion is not null||demoRoom))throw new ArgumentException("--engine-demo uses its own scene and cannot be combined with external assets.");
        if (selfTest && !engineDemo && string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Viewport self-test requires --model path.pmx.");
        if (giSelfTest && giMode != LightingMode.Legacy) throw new ArgumentException("GI lifecycle self-test must start Off; omit --gi or select off.");
        if (giSelfTest && !qualitySpecified) spp = 4;
        var rayGi = new RayGiOptions(giMode, strength, spp, OcclusionStrength: occlusionStrength,
            Composition: giComposition, Denoiser: giDenoiser);
        rayGi.Validate();
        return new(true, model, toon, selfTest, frames, output,
            rayGi, giSelfTest, demoRoom, fitShadow, roomEmission, antialiasing,motion,autoPlay,project,engineDemo);
    }
}
