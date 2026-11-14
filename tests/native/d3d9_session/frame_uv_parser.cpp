// Original CPU-only fixtures. Include the product parser, with no D3D headers,
// DLL loading, window creation, product process, or user asset access.
#include "frame_packet.hpp"
#include <iostream>

namespace {
using Bytes = std::vector<uint8_t>;
constexpr uint64_t ObjectId = 0x1000000000000101;
constexpr size_t VersionOffset = 8;
constexpr size_t VertexCountOffset = 24;
constexpr float Identity[16] = {1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1};
int checks = 0, failures = 0;

void check(bool value, const std::string& name) {
    ++checks;
    if (!value) { ++failures; std::cerr << "FAIL " << name << '\n'; }
}
void u32(Bytes& b, uint32_t v) {
    for (int i = 0; i < 4; ++i) b.push_back(uint8_t(v >> (8 * i)));
}
void u64(Bytes& b, uint64_t v) { u32(b, uint32_t(v)); u32(b, uint32_t(v >> 32)); }
void f32(Bytes& b, float v) { uint32_t bits; std::memcpy(&bits, &v, 4); u32(b, bits); }
void f64(Bytes& b, double v) { uint64_t bits; std::memcpy(&bits, &v, 8); u64(b, bits); }
void text(Bytes& b, const std::string& s) { u32(b, uint32_t(s.size())); b.insert(b.end(), s.begin(), s.end()); }
void identity(Bytes& b) { for (float v : Identity) f32(b, v); }
void putU32(Bytes& b, size_t offset, uint32_t v) {
    for (int i = 0; i < 4; ++i) b.at(offset + i) = uint8_t(v >> (8 * i));
}
uint32_t bits(float value) { uint32_t result; std::memcpy(&result, &value, 4); return result; }

// A complete ordinary FRM1 body, including source metadata, one bone/morph,
// raw normals, material references, and one RGBA texture. No UV trailer here.
Bytes base(uint32_t version, bool empty = false, int32_t sphereMode = 0) {
    Bytes b{'M', 'D', 'A', '9', 'F', 'R', 'M', '1'};
    u32(b, version); u32(b, 32); u32(b, 48);
    u32(b, 1); u32(b, empty ? 0 : 3); u32(b, empty ? 0 : 3);
    u32(b, empty ? 0 : 1); u32(b, 1);
    f64(b, 1.5); f32(b, .05f); identity(b); identity(b);
    for (float value : {0.f, 0.f, -2.f, .12f, .23f, .37f, 1.f}) f32(b, value);
    u64(b, ObjectId); u32(b, 0); u32(b, empty ? 0 : 1); u32(b, empty ? 0 : 1);
    text(b, "diagnostic-uv.pmx"); text(b, "Original UV fixture");
    u32(b, 1); text(b, "fixture bone"); identity(b);
    u32(b, 1); text(b, "fixture morph"); f32(b, -.125f);
    if (!empty) {
        for (uint32_t v = 0; v < 3; ++v) {
            for (float value : {float(v) - 1.f, .5f, .25f, 0.f, 0.f, -1.25f, -.25f, 2.5f}) f32(b, value);
        }
        u32(b, 0); u32(b, 1); u32(b, 2);
        u64(b, ObjectId); u32(b, 0); u32(b, 0); u32(b, 3); u32(b, 3);
        for (float value : {.72f, .28f, .12f, 1.f, .25f, .5f, .75f, .1f, .07f, .03f, 8.f}) f32(b, value);
        u32(b, 0); u32(b, uint32_t(-1)); u32(b, 0); u32(b, uint32_t(sphereMode));
    }
    u32(b, 1); u32(b, 1); u32(b, 4);
    b.insert(b.end(), {17, 61, 193, 251});
    return b;
}

float uvValue(uint32_t vertex, uint32_t channel, uint32_t component) {
    // Values intentionally exceed [0,1]; higher undeclared channels also carry
    // data. Include signed zero, a subnormal, and a large finite raw magnitude.
    if (vertex == 0 && channel == 3 && component == 3) return -0.f;
    if (vertex == 1 && channel == 2 && component == 1) return std::numeric_limits<float>::denorm_min();
    if (vertex == 2 && channel == 1 && component == 2) return -32768.5f;
    float value = float((vertex + 1) * 100 + channel * 10 + component + 1) * .25f;
    return (channel + component) % 2 ? -value : value;
}
Bytes extended(uint32_t channels, bool empty = false, int32_t sphereMode = 0) {
    Bytes b = base(2, empty, sphereMode);
    u32(b, channels); u32(b, empty ? 0 : 3);
    if (!empty) for (uint32_t v = 0; v < 3; ++v)
        for (uint32_t channel = 0; channel < 4; ++channel)
            for (uint32_t component = 0; component < 4; ++component) f32(b, uvValue(v, channel, component));
    return b;
}
packet::Frame read(const Bytes& b) { return packet::Read(b.data(), b.size()); }
void rejected(const std::string& name, const Bytes& b, const char* reason) {
    try { read(b); check(false, name + " accepted"); }
    catch (const std::runtime_error& e) {
        check(std::string(e.what()) == std::string("frame packet: ") + reason, name + " reason: " + e.what());
    }
    catch (...) { check(false, name + " wrong exception"); }
}
void save(const char* path, const Bytes& b) {
    std::ofstream stream(path, std::ios::binary | std::ios::trunc);
    stream.write(reinterpret_cast<const char*>(b.data()), std::streamsize(b.size()));
    stream.close();
    check(bool(stream), std::string("write original fixture ") + path);
}
void common(const packet::Frame& f) {
    check(f.width == 32 && f.height == 48 && f.sourceFrame == 1.5 && f.frameSeconds == .05f, "frame identity/time");
    check(f.view == packet::Matrix{1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1} && f.view == f.projection, "matrices");
    check(f.lightDirection[2] == -2.f && f.clearColor[0] == .12f && f.clearColor[3] == 1.f, "light/clear raw values");
    check(f.objects.size() == 1 && f.objects[0].id == ObjectId && f.objects[0].sourcePath == "diagnostic-uv.pmx" && f.objects[0].name == "Original UV fixture", "object metadata");
    check(f.objects[0].bones.size() == 1 && f.objects[0].bones[0].name == "fixture bone" && f.objects[0].bones[0].world == f.view, "bone metadata");
    check(f.objects[0].morphs.size() == 1 && f.objects[0].morphs[0].name == "fixture morph" && f.objects[0].morphs[0].weight == -.125f, "morph metadata");
    check(f.vertices.size() == 3 && f.vertices[0].position[0] == -1.f && f.vertices[2].position[0] == 1.f && f.vertices[1].normal[2] == -1.25f && f.vertices[0].uv[0] == -.25f && f.vertices[0].uv[1] == 2.5f, "base vertices/raw normals/UV0");
    check(f.indices == std::vector<uint32_t>{0, 1, 2}, "indices");
    check(f.materials.size() == 1 && f.materials[0].objectId == ObjectId && f.materials[0].flags == 3 && f.materials[0].diffuse[0] == .72f && f.materials[0].diffuseTexture == 0 && f.materials[0].toonTexture == -1 && f.materials[0].sphereTexture == 0, "material payload");
    check(f.textures.size() == 1 && f.textures[0].width == 1 && f.textures[0].height == 1 && f.textures[0].rgba == Bytes{17, 61, 193, 251}, "texture payload");
}
}

int main() {
    try {
        const Bytes v1 = base(1), v2 = extended(1);
        const size_t trailer = v1.size();
        const auto old = read(v1); common(old);
        check(old.packetVersion == 1 && old.additionalUvChannelCount == 0 && old.additionalUvs.empty(), "v1 has no additional UV payload");
        save("original-v1.packet", v1);
        for (uint32_t channels = 1; channels <= 4; ++channels) {
            const Bytes b = extended(channels);
            const auto f = read(b); common(f);
            check(f.packetVersion == 2 && f.additionalUvChannelCount == channels && f.additionalUvs.size() == 3, "v2 version/channels/vertex count");
            bool exact = f.additionalUvs.size() == 3;
            if (exact) for (uint32_t v = 0; v < 3; ++v)
                for (uint32_t channel = 0; channel < 4; ++channel)
                    for (uint32_t component = 0; component < 4; ++component)
                        exact = exact && bits(f.additionalUvs[v].uv[channel][component]) == bits(uvValue(v, channel, component));
            check(exact, "v2 all four raw xyzw vectors are bit-exact, including undeclared channels");
            Bytes unchangedBody = b;
            unchangedBody.resize(trailer); putU32(unchangedBody, VersionOffset, 1);
            check(unchangedBody == v1, "v2 preserves complete v1 header/body layout");
            if (channels == 1 || channels == 4) save(channels == 1 ? "original-v2-uv1.packet" : "original-v2-uv4.packet", b);
        }
        const auto sphere1 = read(base(1, false, 3));
        const auto sphere2 = read(extended(1, false, 3));
        check(sphere1.materials[0].sphereMode == 3 && sphere2.materials[0].sphereMode == 3, "sphere mode 3 stays parser-legal in both versions");
        const auto empty1 = read(base(1, true));
        const auto empty2 = read(extended(4, true));
        check(empty1.vertices.empty() && empty1.additionalUvs.empty() && empty1.additionalUvChannelCount == 0, "empty v1");
        check(empty2.vertices.empty() && empty2.additionalUvs.empty() && empty2.additionalUvChannelCount == 4 && empty2.packetVersion == 2, "empty v2 count agrees with header");
        save("original-v2-empty.packet", extended(4, true));

        for (uint32_t version : {0u, 3u, std::numeric_limits<uint32_t>::max()}) {
            Bytes b = v1; putU32(b, VersionOffset, version); rejected("bad version", b, "bad magic or version");
        }
        Bytes bad = v2; bad[0] = '!'; rejected("bad magic", bad, "bad magic or version");
        rejected("v2 absent trailer", base(2), "truncated");
        for (size_t size = trailer; size < v2.size(); ++size) {
            Bytes b(v2.begin(), v2.begin() + size);
            rejected("v2 truncated trailer byte " + std::to_string(size - trailer), b, size < trailer + 8 ? "truncated" : "invalid count");
        }
        for (uint32_t channels : {0u, 5u, std::numeric_limits<uint32_t>::max()}) {
            Bytes b = v2; putU32(b, trailer, channels); rejected("bad channel count", b, "invalid additional UV channel count");
        }
        for (uint32_t count : {0u, 2u, 4u, std::numeric_limits<uint32_t>::max()}) {
            Bytes b = v2; putU32(b, trailer + 4, count); rejected("UV/header count mismatch", b, "additional UV vertex count mismatch");
        }
        // A mismatched header is also rejected, rather than using trailer count
        // as an independent geometry identity or allocation size.
        bad = v2; putU32(bad, VertexCountOffset, 10000001); rejected("oversized base vertex count", bad, "invalid frame dimensions or count");
        for (uint32_t component = 0; component < 16; ++component) {
            for (uint32_t nonFinite : {0x7f800000u, 0xff800000u, 0x7fc00001u}) {
                Bytes b = v2; putU32(b, trailer + 8 + component * 4, nonFinite);
                rejected("non-finite raw UV component " + std::to_string(component), b, "non-finite float");
            }
        }
        bad = v2; bad.push_back(0); rejected("v2 trailing byte", bad, "trailing bytes");
        bad = v1; bad.push_back(0); rejected("v1 trailing byte", bad, "trailing bytes");
        bad = v2; putU32(bad, VersionOffset, 1); rejected("v1 cannot accept a v2 trailer", bad, "trailing bytes");
        try {
            // The constructor must reject before dereferencing this short span;
            // do not allocate a 1 GiB fixture just to test the bound.
            packet::Read(v2.data(), size_t(packet::detail::MaxFile) + 1);
            check(false, "over 1 GiB accepted");
        } catch (const std::runtime_error& e) {
            check(std::string(e.what()) == "frame packet: invalid or oversized byte span", "over 1 GiB rejected before reading");
        }
    } catch (const std::exception& e) {
        ++failures; std::cerr << "UNEXPECTED " << e.what() << '\n';
    }
    std::cout << "CPU frame UV parser checks=" << checks << " failures=" << failures << '\n';
    return failures ? 1 : 0;
}
