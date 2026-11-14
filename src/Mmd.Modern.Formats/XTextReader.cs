// Text mesh/frame/material layout references tinyxfileloader by benikabocha (2017), MIT.
// C# adaptation adds bounded parsing, indexed normals, named materials and planar triangulation.
// Full notice: LICENSE.TinyXFileLoader.txt. Coordinates and units remain exactly as stored in DirectX .x.
using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using System.Text;
using Mmd.Modern.Core;

namespace Mmd.Modern.Formats;

public sealed class XTextReadOptions
{
    public int AnsiCodePage { get; init; } = 932;
    public int MaxInputBytes { get; init; } = 64 * 1024 * 1024;
    public int MaxVertices { get; init; } = 2_000_000;
    public int MaxTriangleVertices { get; init; } = 6_000_000;
    public int MaxDepth { get; init; } = 64;
    public int MaxTokens { get; init; } = 20_000_000;
    public int MaxTokenCharacters { get; init; } = 65_536;
    public int MaxTriangulationOperations { get; init; } = 20_000_000;
    public int MaxDecompressedBytes { get; init; } = 64 * 1024 * 1024;
    public int MaxCompressedChunks { get; init; } = 4096;
}
public sealed record XTextMaterial(string Name, Vector4 Diffuse, float SpecularPower, Vector3 Specular, Vector3 Emissive, string Texture);
public sealed record XTextMaterialRange(int StartIndex, int IndexCount, XTextMaterial Material);
public sealed class XTextDocument
{
    public ReadOnlyCollection<SceneVertex> Vertices { get; }
    public ReadOnlyCollection<uint> Indices { get; }
    public ReadOnlyCollection<XTextMaterialRange> Materials { get; }
    public ReadOnlyCollection<string> Diagnostics { get; }
    /// <summary>RGBA colors aligned with expanded Vertices; unspecified colors are white. Rendering consumption is separate.</summary>
    public ReadOnlyCollection<Vector4> VertexColors { get; }
    internal XTextDocument(List<SceneVertex> vertices, List<uint> indices, List<XTextMaterialRange> materials, List<string> diagnostics, List<Vector4> colors)
    { Vertices = vertices.AsReadOnly(); Indices = indices.AsReadOnly(); Materials = materials.AsReadOnly(); Diagnostics = diagnostics.AsReadOnly(); VertexColors = colors.AsReadOnly(); }
}

/// <summary>Bounded DirectX text/binary/MSZIP .x reader. Skinning and animation reject explicitly.
/// 0032/0064 numeric values convert to finite core float32 values. Frame matrices use the file's
/// row-vector convention; no OpenGL handedness or unit conversion is applied.</summary>
public static class XTextReader
{
    public static XTextDocument Read(Stream input, XTextReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(input); options ??= new();
        if (options.MaxInputBytes is < 16 or > 256 * 1024 * 1024 || options.MaxVertices is < 1 or > 10_000_000
            || options.MaxTriangleVertices is < 3 or > 30_000_000 || options.MaxDepth is < 1 or > 128
            || options.MaxTokens is < 1 or > 100_000_000 || options.MaxTokenCharacters is < 1 or > 1_048_576
            || options.MaxTriangulationOperations is < 1 or > 100_000_000
            || options.MaxDecompressedBytes is < 16 or > 256 * 1024 * 1024
            || options.MaxCompressedChunks is < 1 or > 65_536)
            throw new ArgumentOutOfRangeException(nameof(options));
        using var bytes = new MemoryStream(); byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int count = input.Read(buffer, 0, Math.Min(buffer.Length, options.MaxInputBytes - (int)bytes.Length + 1));
            if (count == 0) break;
            if (bytes.Length + count > options.MaxInputBytes) throw new InvalidDataException(".x exceeds the input size limit.");
            bytes.Write(buffer, 0, count);
        }
        byte[] raw = bytes.ToArray();
        if (raw.Length < 16) throw new InvalidDataException("Truncated .x header.");
        string header = Encoding.ASCII.GetString(raw.AsSpan(0, 16));
        if (!header.StartsWith("xof ", StringComparison.Ordinal)) throw new InvalidDataException("Invalid .x magic.");
        string storage = header.Substring(8, 4);
        if (storage is not ("txt " or "bin " or "tzip" or "bzip") || header.Substring(12, 4) is not ("0032" or "0064"))
            throw new NotSupportedException("Supported .x storage is text/binary/MSZIP with 0032/0064 numeric precision.");
        if (header.Substring(4, 4) is not ("0302" or "0303")) throw new NotSupportedException("Unsupported .x version.");
        int start = 16;
        if (storage is "tzip" or "bzip") { raw = XMszipReader.Decode(raw, options); start = 0; }
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(options.AnsiCodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        try
        {
            return storage is "bin " or "bzip"
                ? new Parser("", options, new(raw, start, header.Substring(12, 4) == "0064", encoding, options)).Read()
                : new Parser(encoding.GetString(raw, start, raw.Length - start), options).Read();
        }
        catch (DecoderFallbackException error) { throw new InvalidDataException(".x strings cannot be decoded using the explicitly selected code page.", error); }
    }

    private sealed class Node
    {
        internal Matrix4x4 Local = Matrix4x4.Identity;
        internal List<Node> Children = [];
        internal List<Mesh> Meshes = [];
    }
    private sealed class Mesh
    {
        internal string Name = "mesh";
        internal Vector3[] Positions = [], Normals = [];
        internal Vector2[] Uvs = [];
        internal Vector4[] Colors = [];
        internal int[][] Faces = [], NormalFaces = [];
        internal int[] FaceMaterials = [];
        internal List<object> Materials = [];
        internal bool HasNormals, HasUvs, HasMaterials, HasColors;
    }
    private sealed class Parser(string text, XTextReadOptions options, XBinaryTokenReader? binary = null)
    {
        private int position, totalVertices, totalFaces, blockCount, tokenCount;
        private long triangulationOperations;
        private string? lookahead;
        private readonly Dictionary<string, XTextMaterial> named = new(StringComparer.Ordinal);
        private readonly List<string> diagnostics = [];
        private readonly List<SceneVertex> vertices = [];
        private readonly List<Vector4> colors = [];
        private readonly List<uint> indices = [];
        private readonly List<XTextMaterialRange> materials = [];
        private static readonly XTextMaterial DefaultMaterial = new("default", Vector4.One, 0, Vector3.Zero, Vector3.Zero, "");
        internal XTextDocument Read()
        {
            var root = new Node(); ParseChildren(root, 0, false);
            Emit(root, Matrix4x4.Identity);
            if (indices.Count == 0) throw Error("No nonempty mesh was found.");
            return new(vertices, indices, materials, diagnostics, colors);
        }
        private void ParseChildren(Node parent, int depth, bool closing)
        {
            if (depth > options.MaxDepth) throw Error("Frame nesting exceeds the depth limit.");
            while (Peek() is { } type)
            {
                if (type == "}") { if (!closing) throw Error("Unexpected closing brace."); Take(); return; }
                (string childType, string name) = Block();
                switch (childType)
                {
                    case "Frame": var child = new Node(); ParseChildren(child, depth + 1, true); parent.Children.Add(child); break;
                    case "FrameTransformMatrix":
                        float[] m = Enumerable.Range(0, 16).Select(_ => Float()).ToArray(); Close();
                        parent.Local = new(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
                        ValidateMatrix(parent.Local); break;
                    case "Mesh": parent.Meshes.Add(ParseMesh(name)); break;
                    case "Material": var material = ParseMaterial(name); if (!named.TryAdd(name, material)) throw Error("Duplicate named material."); break;
                    case "AnimationSet": case "Animation": case "SkinWeights": case "XSkinMeshHeader":
                        throw new NotSupportedException("Animated/skinned .x requires a dedicated runtime and cannot be loaded as a static accessory.");
                    default: SkipBlock(depth + 1); if (childType != "template") diagnostics.Add("Ignored .x block: " + childType); break;
                }
            }
            if (closing) throw Error("Unterminated frame.");
        }
        private Mesh ParseMesh(string name)
        {
            var mesh = new Mesh { Name = name.Length == 0 ? "mesh" : name };
            int count = Count(options.MaxVertices); totalVertices = checked(totalVertices + count);
            if (totalVertices > options.MaxVertices) throw Error("Total .x vertex count exceeds the limit.");
            mesh.Positions = Enumerable.Range(0, count).Select(_ => V3()).ToArray();
            mesh.Faces = Faces(count); totalFaces = checked(totalFaces + mesh.Faces.Length);
            if (totalFaces > options.MaxTriangleVertices / 3) throw Error("Total .x face count exceeds the limit.");
            while (Peek() != "}")
            {
                if (Peek() is null) throw Error("Unterminated mesh.");
                (string type, _) = Block();
                switch (type)
                {
                    case "MeshNormals":
                        if (mesh.HasNormals) throw Error("Duplicate normal block."); mesh.HasNormals = true;
                        mesh.Normals = Enumerable.Range(0, Count(options.MaxVertices)).Select(_ => V3()).ToArray();
                        mesh.NormalFaces = Faces(mesh.Normals.Length); Close(); break;
                    case "MeshTextureCoords":
                        if (mesh.HasUvs) throw Error("Duplicate texture coordinates."); mesh.HasUvs = true;
                        mesh.Uvs = Enumerable.Range(0, Count(options.MaxVertices)).Select(_ => new Vector2(Float(), Float())).ToArray(); Close(); break;
                    case "MeshVertexColors":
                        if (mesh.HasColors) throw Error("Duplicate vertex colors."); mesh.HasColors = true;
                        int colorCount = Count(mesh.Positions.Length);
                        mesh.Colors = Enumerable.Repeat(Vector4.One, mesh.Positions.Length).ToArray();
                        var assigned = new HashSet<int>();
                        for (int i = 0; i < colorCount; i++)
                        {
                            int vertex = Index(mesh.Positions.Length);
                            if (!assigned.Add(vertex)) throw Error("Duplicate vertex color index.");
                            mesh.Colors[vertex] = new(Float(), Float(), Float(), Float());
                        }
                        Close(); diagnostics.Add("MeshVertexColors retained in the document; SceneVertex rendering does not consume them."); break;
                    case "MeshMaterialList":
                        if (mesh.HasMaterials) throw Error("Duplicate material list."); mesh.HasMaterials = true;
                        int materialCount = Count(65536), faceCount = Count(options.MaxTriangleVertices / 3);
                        if (faceCount != mesh.Faces.Length) throw Error("Material face count differs from mesh.");
                        mesh.FaceMaterials = Enumerable.Range(0, faceCount).Select(_ => Index(materialCount)).ToArray();
                        for (int i = 0; i < materialCount; i++)
                        {
                            if (Peek() == "{") { Take(); mesh.Materials.Add(Take() ?? throw Error("Missing material reference.")); Expect("}"); }
                            else { (string materialType, string materialName) = Block(); if (materialType != "Material") throw Error("Expected Material."); var mat = ParseMaterial(materialName); mesh.Materials.Add(mat); if (materialName.Length != 0 && !named.TryAdd(materialName, mat)) throw Error("Duplicate named material."); }
                        }
                        Close(); break;
                    case "SkinWeights": case "XSkinMeshHeader": throw new NotSupportedException("Skinned .x is not a static accessory.");
                    default: SkipBlock(1); diagnostics.Add("Ignored .x mesh block: " + type); break;
                }
            }
            Close();
            if (mesh.HasUvs && mesh.Uvs.Length != mesh.Positions.Length) throw Error("UV count differs from positions.");
            if (mesh.HasNormals && (mesh.NormalFaces.Length != mesh.Faces.Length
                || mesh.NormalFaces.Where((face, i) => face.Length != mesh.Faces[i].Length).Any())) throw Error("Normal faces differ from mesh topology.");
            return mesh;
        }
        private int[][] Faces(int vertexCount)
        {
            int count = Count(options.MaxTriangleVertices / 3); var result = new int[count][]; long expanded = 0;
            for (int i = 0; i < count; i++)
            {
                int size = Count(1024); if (size < 3) throw Error("A polygon requires at least three corners.");
                expanded += (long)(size - 2) * 3;
                if (expanded > options.MaxTriangleVertices) throw Error("Polygon triangulation exceeds the vertex budget.");
                result[i] = Enumerable.Range(0, size).Select(_ => Index(vertexCount)).ToArray();
            }
            return result;
        }
        private XTextMaterial ParseMaterial(string name)
        {
            Vector4 diffuse = new(Float(), Float(), Float(), Float()); float power = Float(); Vector3 specular = V3(), emissive = V3(); string texture = "";
            if (power < 0 || diffuse.W is < 0 or > 1 || Vector3.Min(emissive, Vector3.Zero) != Vector3.Zero) throw Error("Invalid material parameters.");
            while (Peek() != "}")
            {
                if (Peek() is null) throw Error("Unterminated material.");
                (string type, _) = Block();
                if (type == "TextureFilename") { string token = Take() ?? throw Error("Missing texture name."); if (!token.StartsWith('"')) throw Error("Texture filename must be quoted."); texture = token[1..]; Close(); }
                else { SkipBlock(1); diagnostics.Add("Ignored .x material block: " + type); }
            }
            Close(); return new(name.Length == 0 ? "material" : name, diffuse, power, specular, emissive, texture);
        }
        private void Emit(Node node, Matrix4x4 parent)
        {
            Matrix4x4 world = node.Local * parent; ValidateMatrix(world); Matrix4x4.Invert(world, out var inverse);
            var normalTransform = Matrix4x4.Transpose(inverse);
            foreach (var mesh in node.Meshes)
            {
                for (int faceIndex = 0; faceIndex < mesh.Faces.Length; faceIndex++)
                {
                    int[] face = mesh.Faces[faceIndex];
                    int[] triangles = XPolygonTriangulator.Triangulate(mesh.Positions, face, ref triangulationOperations,
                        options.MaxTriangulationOperations, out Vector3 normal);
                    int matId = mesh.FaceMaterials.Length == 0 ? -1 : mesh.FaceMaterials[faceIndex];
                    var mat = matId < 0 ? DefaultMaterial : mesh.Materials[matId] is XTextMaterial value ? value
                        : named.TryGetValue((string)mesh.Materials[matId], out var referenced) ? referenced : throw Error("Unresolved named material.");
                    int start = indices.Count;
                    foreach (int localCorner in triangles)
                    {
                        if (vertices.Count >= options.MaxTriangleVertices) throw Error("Expanded triangle vertex budget exceeded.");
                        int positionId = face[localCorner];
                        Vector3 n = mesh.NormalFaces.Length == 0 ? normal : mesh.Normals[mesh.NormalFaces[faceIndex][localCorner]];
                        n = Vector3.TransformNormal(n, normalTransform); if (n.LengthSquared() > 1e-20f) n = Vector3.Normalize(n);
                        vertices.Add(new(Vector3.Transform(mesh.Positions[positionId], world), n, mesh.Uvs.Length == 0 ? Vector2.Zero : mesh.Uvs[positionId]));
                        colors.Add(mesh.Colors.Length == 0 ? Vector4.One : mesh.Colors[positionId]);
                        indices.Add((uint)(vertices.Count - 1));
                    }
                    int length = indices.Count - start;
                    if (materials.Count != 0 && materials[^1].Material == mat) materials[^1] = materials[^1] with { IndexCount = materials[^1].IndexCount + length };
                    else materials.Add(new(start, length, mat));
                }
            }
            foreach (var child in node.Children) Emit(child, world);
        }
        private void ValidateMatrix(Matrix4x4 matrix)
        {
            if (matrix.M14 != 0 || matrix.M24 != 0 || matrix.M34 != 0 || matrix.M44 != 1 || !Matrix4x4.Invert(matrix, out _)
                || new[] { matrix.M11, matrix.M12, matrix.M13, matrix.M21, matrix.M22, matrix.M23, matrix.M31, matrix.M32, matrix.M33, matrix.M41, matrix.M42, matrix.M43 }.Any(v => !float.IsFinite(v)))
                throw Error("Frame matrix must be finite, affine and invertible.");
        }
        private (string Type, string Name) Block()
        {
            string type = Take() ?? throw Error("Expected block type."); string next = Take() ?? throw Error("Expected block opening.");
            if (++blockCount > 100_000) throw Error("Block count limit exceeded.");
            if (next == "{") return (type, ""); Expect("{"); return (type, next);
        }
        private void SkipBlock(int depth)
        {
            if (depth > options.MaxDepth) throw Error("Block nesting exceeds the limit.");
            while (Take() is { } token) { if (token == "}") return; if (token == "{") SkipBlock(depth + 1); }
            throw Error("Unterminated block.");
        }
        private int Count(int limit) { int value = Integer(); if (value < 0 || value > limit) throw Error("Count exceeds the supported limit."); return value; }
        private int Index(int count) { int value = Integer(); if (value < 0 || value >= count) throw Error("Index outside referenced table."); return value; }
        private int Integer()
        {
            string? token = Take();
            if (binary is not null && binary.NumberKind != XBinaryNumberKind.Integer
                || !int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)) throw Error("Expected integer.");
            return value;
        }
        private float Float()
        {
            string? token = Take();
            if (binary is not null && binary.NumberKind != XBinaryNumberKind.Float
                || !float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value)) throw Error("Expected finite float.");
            return value;
        }
        private Vector3 V3() => new(Float(), Float(), Float());
        private void Close() => Expect("}");
        private void Expect(string token) { if (Take() != token) throw Error("Expected " + token); }
        private string? Peek() => lookahead ??= NextToken();
        private string? Take() { string? value = Peek(); lookahead = null; return value; }
        private string? NextToken()
        {
            string? value = binary is null ? Lex() : binary.ReadNext();
            if (value is not null && ++tokenCount > options.MaxTokens) throw Error("Token count limit exceeded.");
            return value;
        }
        private string? Lex()
        {
            while (position < text.Length)
            {
                char c = text[position];
                if (char.IsWhiteSpace(c) || c is ',' or ';') { position++; continue; }
                if (c == '#' || c == '/' && position + 1 < text.Length && text[position + 1] == '/')
                { while (position < text.Length && text[position] is not ('\r' or '\n')) position++; continue; }
                if (c == '/' && position + 1 < text.Length && text[position + 1] == '*')
                { int end = text.IndexOf("*/", position + 2, StringComparison.Ordinal); if (end < 0) throw Error("Unterminated comment."); position = end + 2; continue; }
                if (c is '{' or '}') { position++; return c.ToString(); }
                int start = position++;
                if (c == '"')
                {
                    var value = new StringBuilder("\"");
                    while (position < text.Length && text[position] != '"')
                    {
                        char item = text[position++];
                        if (item == '\\' && position < text.Length && text[position] == '\\') position++;
                        value.Append(item);
                        if (value.Length > options.MaxTokenCharacters) throw Error("Token length limit exceeded.");
                    }
                    if (position == text.Length) throw Error("Unterminated quoted string."); position++; return value.ToString();
                }
                while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not ('{' or '}' or ';' or ','))
                { if (position - start >= options.MaxTokenCharacters) throw Error("Token length limit exceeded."); position++; }
                return text[start..position];
            }
            return null;
        }
        private InvalidDataException Error(string message) => new(binary is null ? $".x text at character {position}: {message}" : $".x binary at byte {binary.Position}: {message}");
    }
}
