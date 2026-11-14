// PMM v2 field order adapted from nanoem/ext/document.c and document_p.h
// @ 30acffaa29f5d2eb9e997d69418f2e4b97b5894f, MIT, Copyright (c) 2015-2023 hkrn.
using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Mmd.Modern.Formats;

/// <summary>Reads PMM v1/v2 without opening any referenced asset or owning the input stream.</summary>
public static class PmmReader
{
    public static PmmDocument Read(Stream stream, PmmReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead) throw new ArgumentException("PMM stream must be readable.", nameof(stream));
        options ??= new();
        if (options.AnsiCodePage is not (932 or 936)) throw new ArgumentOutOfRangeException(nameof(options), "PMM ANSI code page must be 932 or 936.");
        if (options.MaxElementCount < 0 || options.MaxInputBytes < 30) throw new ArgumentOutOfRangeException(nameof(options));
        if (stream.CanSeek && stream.Length - stream.Position > options.MaxInputBytes)
            throw new PmmFormatException("input.length", 0, "input exceeds configured byte limit");
        using var copy = new MemoryStream();
        byte[] buffer = new byte[65536];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (copy.Length + count > options.MaxInputBytes) throw new PmmFormatException("input.length", copy.Length, "input exceeds configured byte limit");
            copy.Write(buffer, 0, count);
        }
        return new Parser(copy.ToArray(), options).Read();
    }

    private sealed class Parser
    {
        private readonly byte[] data;
        private readonly PmmReadOptions options;
        private readonly Encoding encoding;
        private int position;
        private string field = "header";
        private int version;
        private bool V2 => version == 2;
        public Parser(byte[] data, PmmReadOptions options)
        {
            this.data = data;
            this.options = options;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            // Raw buffers are preserved even if this selected code page cannot decode them.
            encoding = Encoding.GetEncoding(options.AnsiCodePage);
        }
        private void Need(int n)
        {
            if (n < 0 || n > data.Length - position) throw new PmmFormatException(field, position, $"truncated input: need {n} bytes, have {data.Length - position}");
        }
        private byte B() { Need(1); return data[position++]; }
        private bool Flag() => B() != 0;
        private int I() { Need(4); int result = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(position)); position += 4; return result; }
        private float F()
        {
            int offset = position;
            float result = BitConverter.Int32BitsToSingle(I());
            if (!float.IsFinite(result)) throw new PmmFormatException(field, offset, "non-finite float");
            return result;
        }
        private Vector3 V() => new(F(), F(), F());
        private Quaternion Q() => new(F(), F(), F(), F());
        private IReadOnlyList<byte> Bytes(int n) { Need(n); var result = data.AsSpan(position, n).ToArray(); position += n; return Array.AsReadOnly(result); }
        private int Count(int minRecordBytes)
        {
            int offset = position, n = I();
            if (n < 0 || n > options.MaxElementCount || (long)n * minRecordBytes > data.Length - position)
                throw new PmmFormatException(field + ".count", offset, $"invalid count {n}; remaining {data.Length - position} bytes");
            return n;
        }
        private IReadOnlyList<T> ArrayOf<T>(int n, int minRecordBytes, Func<int, T> read)
        {
            if (n < 0 || n > options.MaxElementCount || (long)n * minRecordBytes > data.Length - position)
                throw new PmmFormatException(field + ".count", position, $"invalid count {n}");
            var result = new T[n];
            for (int i = 0; i < n; i++) result[i] = read(i);
            return Array.AsReadOnly(result);
        }
        private IReadOnlyList<T> Counted<T>(int minRecordBytes, Func<int, T> read) => ArrayOf(Count(minRecordBytes), minRecordBytes, read);
        private PmmString String(int length = -1)
        {
            int offset = position;
            if (length < 0) length = B();
            byte[] raw = Bytes(length).ToArray();
            int nul = System.Array.IndexOf(raw, (byte)0);
            return new(offset, encoding.GetString(raw, 0, nul < 0 ? raw.Length : nul), System.Array.AsReadOnly(raw));
        }
        private T Frame<T>(bool extra, int index, Func<T> read) where T : PmmKeyframe
        {
            int offset = position, objectIndex = extra ? I() : index;
            int frame = I(), previous = I(), next = I();
            if (frame < 0) throw new PmmFormatException(field + ".frame", offset + (extra ? 4 : 0), "negative frame");
            T result = read();
            result.Offset = offset; result.RecordIndex = index; result.ObjectIndex = objectIndex;
            result.Frame = frame; result.PreviousIndex = previous; result.NextIndex = next;
            return result;
        }
        private PmmBoneKeyframe Bone(bool extra, int index) => Frame(extra, index, () => new PmmBoneKeyframe
        {
            Interpolation = Bytes(16), Translation = V(), Rotation = Q(), Selected = Flag(), PhysicsDisabled = V2 && Flag()
        });
        private PmmMorphKeyframe Morph(bool extra, int index) => Frame(extra, index, () => new PmmMorphKeyframe { Weight = F(), Selected = Flag() });
        private PmmOutsideParent Parent() => new(position, I(), I());
        private PmmModelKeyframe ModelFrame(bool extra, int index, int constraints, int parents) => Frame(extra, index, () => new PmmModelKeyframe
        {
            Visible = Flag(), ConstraintEnabled = ArrayOf(constraints, 1, _ => Flag()), OutsideParents = ArrayOf(parents, 8, _ => Parent()), Selected = Flag()
        });
        private PmmCameraKeyframe CameraFrame(bool extra, int index) => Frame(extra, index, () => new PmmCameraKeyframe
        {
            Distance = F(), LookAt = V(), Angle = V(), ParentModelIndex = V2 ? I() : -1, ParentBoneIndex = V2 ? I() : -1,
            Interpolation = Bytes(24), Perspective = B() == 0, Fov = I(), Selected = Flag()
        });
        private PmmLightKeyframe LightFrame(bool extra, int index) => Frame(extra, index, () => new PmmLightKeyframe { Color = V(), Direction = V(), Selected = Flag() });
        private PmmAccessoryKeyframe AccessoryFrame(bool extra, int index) => Frame(extra, index, () => new PmmAccessoryKeyframe
        {
            OpacityAndVisibility = B(), ParentModelIndex = I(), ParentBoneIndex = I(), Translation = V(), Rotation = V(), Scale = F(), ShadowEnabled = Flag(), Selected = Flag()
        });
        private PmmGravityKeyframe GravityFrame(bool extra, int index) => Frame(extra, index, () => new PmmGravityKeyframe
        {
            NoiseEnabled = Flag(), Noise = I(), Acceleration = F(), Direction = V(), Selected = Flag()
        });
        private PmmSelfShadowKeyframe ShadowFrame(bool extra, int index) => Frame(extra, index, () => new PmmSelfShadowKeyframe { Mode = B(), Distance = F(), Selected = Flag() });

        public PmmDocument Read()
        {
            IReadOnlyList<byte> header = Bytes(30);
            if (header.Take(24).SequenceEqual("Polygon Movie maker 0001"u8.ToArray()))
                version = 1;
            else if (header.Take(24).SequenceEqual("Polygon Movie maker 0002"u8.ToArray())) version = 2;
            else throw new PmmFormatException("header.version", 0, "expected Polygon Movie maker 0001 or 0002");
            int width = I(), height = I(), timeline = I(); float fov = F();
            IReadOnlyList<byte> panels = Bytes(V2 ? 7 : 6);
            byte selectedModel = B(); int modelCount = B();
            var modelNames = V2 ? System.Array.Empty<PmmString>() : ArrayOf(modelCount, 20, _ => String(20));
            var models = ArrayOf(modelCount, 280, index => { field = $"models[{index}]"; return Model(index); });
            field = "camera";
            var camera = new PmmCamera
            {
                Offset = position, InitialKeyframe = CameraFrame(false, 0), Keyframes = Counted(V2 ? 82 : 74, i => CameraFrame(true, i + 1)),
                LookAt = V(), Position = V(), Angle = V(), Perspective = B() == 0
            };
            field = "light";
            var light = new PmmLight
            {
                Offset = position, InitialKeyframe = LightFrame(false, 0), Keyframes = Counted(41, i => LightFrame(true, i + 1)), Color = V(), Direction = V()
            };
            field = "accessories";
            byte selectedAccessory = B(); int accessoryScroll = I(); int accessoryCount = B();
            var accessoryNames = ArrayOf(accessoryCount, 100, _ => String(100));
            var accessories = ArrayOf(accessoryCount, 400, index => { field = $"accessories[{index}]"; return Accessory(); });
            field = "settings";
            int currentFrame = I();
            // Settings surround the gravity/self-shadow sections in the file.
            int settingsOffset = position;
            int scroll = I(), thumb = I(), edit = I(); byte lookMode = B(); bool loop = Flag(), beginOn = Flag(), endOn = Flag();
            int begin = I(), end = I(); bool audioOn = Flag(); PmmString audio = String(256);
            int vx = I(), vy = I(); float vs = F(); PmmString video = String(256); int videoEnabled = I();
            int ix = I(), iy = I(); float scale = F(); PmmString image = String(256);
            bool imageOn = Flag(), info = Flag(), grid = Flag(), ground = Flag(); float fps = F(); int capture = I(), accessoryAfter = I();
            float shadowBrightness = F(); bool translucent = V2 && Flag(); byte physicsMode = V2 ? B() : (byte)0;
            field = "gravity";
            var gravity = V2 ? new PmmGravity
            {
                Offset = position, Acceleration = F(), Noise = I(), Direction = V(), NoiseEnabled = Flag(),
                InitialKeyframe = GravityFrame(false, 0), Keyframes = Counted(38, i => GravityFrame(true, i + 1))
            } : new();
            field = "selfShadow";
            var shadow = V2 ? new PmmSelfShadow
            {
                Offset = position, Enabled = Flag(), Distance = F(), InitialKeyframe = ShadowFrame(false, 0), Keyframes = Counted(22, i => ShadowFrame(true, i + 1))
            } : new();
            field = "settings.tail";
            var edge = V2 ? ArrayOf(3, 4, _ => I()) : System.Array.Empty<int>(); bool black = V2 && Flag(); int lookModel = V2 ? I() : 0, lookBone = V2 ? I() : 0;
            var matrix = V2 ? Bytes(64) : System.Array.Empty<byte>(); byte following = V2 ? B() : (byte)0, unknown = V2 ? B() : (byte)0; bool physicsGround = V2 && Flag(); int frameText = V2 ? I() : 0;
            byte? trailer = V2 && position < data.Length ? B() : null;
            IReadOnlyList<PmmModelSelection> selections = [];
            if (trailer is > 0) selections = ArrayOf(modelCount, 5, _ => new PmmModelSelection(position, B(), I()));
            var settings = new PmmSettings
            {
                Offset = settingsOffset, HorizontalScroll = scroll, HorizontalScrollThumb = thumb, EditingMode = edit, CameraLookMode = lookMode,
                LoopEnabled = loop, BeginFrameEnabled = beginOn, EndFrameEnabled = endOn, BeginFrame = begin, EndFrame = end,
                AudioEnabled = audioOn, AudioPath = audio, VideoOffsetX = vx, VideoOffsetY = vy, VideoScale = vs, VideoPath = video, VideoEnabledRaw = videoEnabled,
                ImageOffsetX = ix, ImageOffsetY = iy, ImageScale = scale, ImagePath = image, ImageEnabled = imageOn,
                InformationShown = info, GridAndAxisShown = grid, GroundShadowShown = ground, PreferredFps = fps, ScreenCaptureMode = capture,
                AccessoryIndexAfterModels = accessoryAfter, GroundShadowBrightness = shadowBrightness, TranslucentGroundShadowEnabled = translucent, PhysicsSimulationMode = physicsMode,
                EdgeColorRgb = edge, BlackBackgroundEnabled = black, CameraLookAtModelIndex = lookModel, CameraLookAtBoneIndex = lookBone,
                UnknownMatrix = matrix, FollowingLookAtRaw = following, UnknownBooleanRaw = unknown, PhysicsGroundEnabled = physicsGround, CurrentFrameText = frameText,
                SelectionTrailerFlag = trailer, ModelSelections = selections
            };
            ValidateLinks([camera.InitialKeyframe], camera.Keyframes, "camera");
            ValidateLinks([light.InitialKeyframe], light.Keyframes, "light");
            ValidateLinks([gravity.InitialKeyframe], gravity.Keyframes, "gravity");
            ValidateLinks([shadow.InitialKeyframe], shadow.Keyframes, "selfShadow");
            foreach (var accessory in accessories) ValidateLinks([accessory.InitialKeyframe], accessory.Keyframes, "accessory");
            int parsed = position; var tail = Bytes(data.Length - position);
            return new PmmDocument
            {
                Version = version, V1ModelNames = modelNames, AnsiCodePage = options.AnsiCodePage, HeaderBytes = header, OutputWidth = width, OutputHeight = height, TimelineWidth = timeline, CameraFov = fov,
                PanelStates = panels, SelectedModelIndex = selectedModel, Models = models, Camera = camera, Light = light,
                SelectedAccessoryIndex = selectedAccessory, AccessoryHorizontalScroll = accessoryScroll, AccessoryNames = accessoryNames, Accessories = accessories,
                CurrentFrame = currentFrame, Settings = settings, Gravity = gravity, SelfShadow = shadow, ParsedLength = parsed, BytesConsumed = data.Length, UnknownTail = tail,
                Diagnostics = tail.Count == 0 ? [] : [$"Unrecognized PMM tail: {tail.Count} bytes at offset {parsed}; preserved without interpretation."]
            };
        }
        private PmmModel Model(int modelPosition)
        {
            int offset = position; byte index = B(); PmmString name = String(V2 ? -1 : 20), english = V2 ? String() : new(0, "", []), path = String(256);
            byte fixedTracks = V2 ? B() : (byte)0;
            IReadOnlyList<PmmString> bones, morphs; IReadOnlyList<int> constraints, parentSubjects;
            if (V2)
            {
                bones = Counted(1, _ => String()); morphs = Counted(1, _ => String());
                constraints = Counted(4, _ => I()); parentSubjects = Counted(4, _ => I());
            }
            else
            {
                var metadata = options.V1ModelMetadataResolver?.Invoke(new(path, modelPosition, index));
                if (metadata is null) throw new PmmFormatException(field + ".metadata", position, "PMM 0001 requires explicit external model track metadata");
                if (metadata.BoneNames.Count > options.MaxElementCount || metadata.MorphNames.Count > options.MaxElementCount || metadata.ConstraintBoneIndices.Count > options.MaxElementCount)
                    throw new PmmFormatException(field + ".metadata", position, "PMM 0001 metadata exceeds configured element limit");
                bones = System.Array.AsReadOnly(metadata.BoneNames.Select(value => new PmmString(-1, value, [])).ToArray());
                morphs = System.Array.AsReadOnly(metadata.MorphNames.Select(value => new PmmString(-1, value, [])).ToArray());
                constraints = metadata.ConstraintBoneIndices; parentSubjects = [];
            }
            byte drawOrder = B(); bool visible = Flag(); int selectedBone = I(); var selectedMorphs = ArrayOf(4, 4, _ => I());
            var expansion = Bytes(B()); int vertical = I(), lastFrame = I();
            var initialBones = ArrayOf(bones.Count, V2 ? 58 : 57, i => Bone(false, i)); var boneFrames = Counted(V2 ? 62 : 61, i => Bone(true, bones.Count + i));
            var initialMorphs = ArrayOf(morphs.Count, 17, i => Morph(false, i)); var morphFrames = Counted(21, i => Morph(true, morphs.Count + i));
            var initialModel = ModelFrame(false, 0, constraints.Count, parentSubjects.Count);
            var modelFrames = Counted(18 + constraints.Count + parentSubjects.Count * 8, i => ModelFrame(true, i + 1, constraints.Count, parentSubjects.Count));
            var boneStates = ArrayOf(bones.Count, V2 ? 31 : 34, _ => BoneState());
            var morphStates = ArrayOf(morphs.Count, 4, _ => new PmmMorphState(position, F()));
            var constraintStates = ArrayOf(constraints.Count, 1, _ => new PmmConstraintState(position, Flag()));
            var parentStates = ArrayOf(parentSubjects.Count, 16, _ => new PmmOutsideParentState(position, I(), I(), Parent()));
            bool blend = V2 && Flag(); float edge = V2 ? F() : 0; bool selfShadow = V2 && Flag(); byte transform = V2 ? B() : (byte)0;
            foreach (int boneIndex in constraints)
                if (boneIndex < 0 || boneIndex >= bones.Count) throw new PmmFormatException(field + ".boneIndex", offset, $"invalid bone index {boneIndex}");
            ValidateLinks(initialBones, boneFrames, field + ".boneFrames"); ValidateLinks(initialMorphs, morphFrames, field + ".morphFrames");
            ValidateLinks([initialModel], modelFrames, field + ".modelFrames");
            return new PmmModel
            {
                Offset = offset, Index = index, Name = name, EnglishName = english, Path = path, FixedTrackCount = fixedTracks,
                BoneNames = bones, MorphNames = morphs, ConstraintBoneIndices = constraints, OutsideParentSubjectBoneIndices = parentSubjects,
                DrawOrderIndex = drawOrder, Visible = visible, SelectedBoneIndex = selectedBone, SelectedMorphIndices = selectedMorphs, ExpansionStates = expansion,
                VerticalScroll = vertical, LastFrame = lastFrame, InitialBoneKeyframes = initialBones, BoneKeyframes = boneFrames, InitialMorphKeyframes = initialMorphs, MorphKeyframes = morphFrames,
                InitialModelKeyframe = initialModel, ModelKeyframes = modelFrames, BoneStates = boneStates, MorphStates = morphStates, ConstraintStates = constraintStates,
                OutsideParentStates = parentStates, BlendEnabled = blend, EdgeWidth = edge, SelfShadowEnabled = selfShadow, TransformOrderIndex = transform
            };
        }
        private PmmBoneState BoneState()
        {
            int offset = position; var translation = V(); var rotation = Q(); int unknown = V2 ? 0 : I();
            bool dirty = Flag(), disabled = V2 && Flag(); byte selected = B();
            return new(offset, translation, rotation, dirty, disabled, selected) { V1UnknownValue = unknown };
        }
        private PmmAccessory Accessory()
        {
            int offset = position; byte index = B(); var name = String(100); var path = String(256); byte draw = B();
            var initial = AccessoryFrame(false, 0); var frames = Counted(55, i => AccessoryFrame(true, i + 1));
            return new PmmAccessory
            {
                Offset = offset, Index = index, Name = name, Path = path, DrawOrderIndex = draw, InitialKeyframe = initial, Keyframes = frames,
                OpacityAndVisibility = B(), ParentModelIndex = I(), ParentBoneIndex = I(), Translation = V(), Scale = F(), Rotation = V(), ShadowEnabled = Flag(), AddBlendEnabled = V2 && Flag()
            };
        }
        private static void ValidateLinks<T>(IReadOnlyList<T> initial, IReadOnlyList<T> additional, string name) where T : PmmKeyframe
        {
            T[] records = initial.Concat(additional).ToArray();
            var byId = new Dictionary<int, T>();
            foreach (T record in records)
                if (record.ObjectIndex < 0 || !byId.TryAdd(record.ObjectIndex, record))
                    throw new PmmFormatException(name + ".objectIndex", record.Offset, $"negative or duplicate object index {record.ObjectIndex}");
            foreach (T record in records)
            {
                if (record.PreviousIndex < 0 || (record.PreviousIndex != 0 && !byId.ContainsKey(record.PreviousIndex)) ||
                    record.NextIndex < 0 || (record.NextIndex != 0 && !byId.ContainsKey(record.NextIndex)))
                    throw new PmmFormatException(name + ".links", record.Offset, $"invalid link prev={record.PreviousIndex}, next={record.NextIndex}, count={records.Length}");
            }
            // Initial records define track roots. Their preserved previous values
            // may point to themselves or a later key; they do not extend the track
            // backwards. Ownership is checked from each root's forward chain below.
            // Detect cycles in effective links without quadratic chain walking.
            foreach (bool next in new[] { false, true })
            {
                var visited = new Dictionary<int, byte>();
                foreach (T start in records)
                {
                    int cursor = start.ObjectIndex;
                    var chain = new List<int>();
                    while (!visited.ContainsKey(cursor))
                    {
                        visited[cursor] = 1; chain.Add(cursor);
                        T current = byId[cursor];
                        if (!next && current.RecordIndex < initial.Count) break;
                        int link = next ? current.NextIndex : current.PreviousIndex;
                        if (link == 0) break;
                        cursor = link;
                        if (visited.TryGetValue(cursor, out byte state) && state == 1) throw new PmmFormatException(name + ".links", byId[cursor].Offset, "cyclic keyframe link");
                    }
                    foreach (int item in chain) visited[item] = 2;
                }
            }
            var owners = new Dictionary<int, int>();
            foreach (T root in initial)
            {
                T cursor = root;
                while (true)
                {
                    if (!owners.TryAdd(cursor.ObjectIndex, root.ObjectIndex))
                        throw new PmmFormatException(name + ".links", cursor.Offset, "keyframe belongs to multiple initial tracks");
                    if (cursor.NextIndex == 0) break;
                    T following = byId[cursor.NextIndex];
                    if (following.PreviousIndex != cursor.ObjectIndex)
                        throw new PmmFormatException(name + ".links", following.Offset, "next/previous links disagree");
                    cursor = following;
                }
            }
            foreach (T record in additional)
                if (!owners.ContainsKey(record.ObjectIndex))
                    throw new PmmFormatException(name + ".links", record.Offset, "keyframe is disconnected from its initial track");
        }
    }
}
