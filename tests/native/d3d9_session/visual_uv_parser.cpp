// Original CPU-only fixtures against the real product readers. No D3D headers,
// compatibility DLL loading, window creation, product process or user assets.
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include "visual_packet.hpp"
#include <iostream>

namespace {
using Bytes = std::vector<uint8_t>;
constexpr uint64_t ObjectId = 0x4000000000000203;
constexpr size_t VisualVersion = 8, VisualMaterials = 12, VisualVertices = 16;
constexpr size_t VisualSha = 20, VisualEye = 52, VisualMatrix = 64;
constexpr size_t Material = 128, MaterialLocal = Material + 8, MaterialFlags = Material + 12;
constexpr size_t MaterialValues = Material + 16, Cutoff = MaterialValues + 48;
constexpr size_t Normals = Material + 132;
constexpr float Identity[16] = {1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1};
int checks = 0, failures = 0;

void check(bool value, const std::string& name) {
    ++checks;
    if (!value) { ++failures; std::cerr << "FAIL " << name << '\n'; }
}
void u32(Bytes& b, uint32_t value) {
    for (int i = 0; i < 4; ++i) b.push_back(uint8_t(value >> (8 * i)));
}
void u64(Bytes& b, uint64_t value) { u32(b, uint32_t(value)); u32(b, uint32_t(value >> 32)); }
void f32(Bytes& b, float value) { uint32_t bits; std::memcpy(&bits, &value, 4); u32(b, bits); }
void f64(Bytes& b, double value) { uint64_t bits; std::memcpy(&bits, &value, 8); u64(b, bits); }
void text(Bytes& b, const std::string& value) {
    u32(b, uint32_t(value.size())); b.insert(b.end(), value.begin(), value.end());
}
void identity(Bytes& b) { for (float value : Identity) f32(b, value); }
void putU32(Bytes& b, size_t offset, uint32_t value) {
    for (int i = 0; i < 4; ++i) b.at(offset + i) = uint8_t(value >> (8 * i));
}
uint32_t bits(float value) { uint32_t out; std::memcpy(&out, &value, 4); return out; }

float uvValue(uint32_t vertex, uint32_t channel, uint32_t component) {
    if (vertex == 0 && channel == 3 && component == 2) return -0.f;
    if (vertex == 2 && channel == 2 && component == 1) return std::numeric_limits<float>::denorm_min();
    const float value = float(vertex * 100 + channel * 10 + component + 1) * .125f;
    return (vertex + component) % 2 ? -value : value;
}

// One original triangle and one 1x1 sphere texture. Version 2 appends every raw
// float4 channel, although only UV1 is declared active. The frames are parsed by
// packet::Read before being handed to visual::Read; no fabricated Frame object.
Bytes makeFrame(uint32_t version, int32_t sphereMode, bool diffuseTexture = true) {
    Bytes b{'M', 'D', 'A', '9', 'F', 'R', 'M', '1'};
    u32(b, version); u32(b, 64); u32(b, 32);
    for (uint32_t count : {1u, 3u, 3u, 1u, 1u}) u32(b, count);
    f64(b, 2.25); f32(b, .075f); identity(b); identity(b);
    for (float value : {0.f, -1.f, -.5f, .1f, .2f, .3f, 1.f}) f32(b, value);
    u64(b, ObjectId); u32(b, 0); u32(b, 1); u32(b, 1);
    text(b, "original-visual-uv.pmx"); text(b, "CPU triangle");
    u32(b, 0); u32(b, 0); // no bones or morphs
    for (uint32_t vertex = 0; vertex < 3; ++vertex)
        for (float value : {float(vertex) - .5f, .25f, -.75f, 0.f, 0.f, -2.f, -.125f, 1.25f}) f32(b, value);
    u32(b, 0); u32(b, 1); u32(b, 2);
    u64(b, ObjectId); u32(b, 0); u32(b, 0); u32(b, 3); u32(b, 3);
    for (float value : {.625f, .375f, .125f, 1.f, .5f, .25f, .125f, .0625f, .03125f, .015625f, 12.f}) f32(b, value);
    u32(b, diffuseTexture ? 0 : uint32_t(-1)); u32(b, uint32_t(-1));
    u32(b, 0); u32(b, uint32_t(sphereMode));
    u32(b, 1); u32(b, 1); u32(b, 4); b.insert(b.end(), {13, 79, 173, 241});
    if (version == 2) {
        u32(b, 1); u32(b, 3);
        for (uint32_t vertex = 0; vertex < 3; ++vertex)
            for (uint32_t channel = 0; channel < 4; ++channel)
                for (uint32_t component = 0; component < 4; ++component) f32(b, uvValue(vertex, channel, component));
    }
    return b;
}

float materialValue(uint32_t field, uint32_t component) {
    if (field == 3 && component == 2) return -0.f;
    if (field == 6 && component == 3) return std::numeric_limits<float>::denorm_min();
    const float value = float((field + 1) * 8 + component) * .25f;
    return (field + component) % 2 ? -value : value;
}
float matrixValue(uint32_t index) { return float(int(index) - 8) * .125f; }
float normalValue(uint32_t vertex, uint32_t component, bool sphere) {
    if (vertex == 0 && component == 1) return sphere ? -0.f : 0.f;
    const float value = float((vertex + 1) * 4 + component) * .25f;
    return sphere ? -value * 2.f : value;
}
Bytes makeVisual(const Bytes& frame, uint32_t version, uint32_t flags) {
    Bytes b{'M', 'D', 'A', '9', 'V', 'I', 'S', '1'};
    u32(b, version); u32(b, 1); u32(b, 3);
    const auto digest = visual::hash(frame.data(), frame.size());
    b.insert(b.end(), digest.begin(), digest.end());
    for (float value : {-0.f, 1.5f, -7.25f}) f32(b, value);
    for (uint32_t i = 0; i < 16; ++i) f32(b, matrixValue(i));
    u64(b, ObjectId); u32(b, 0); u32(b, flags);
    for (uint32_t field = 0; field < 3; ++field)
        for (uint32_t component = 0; component < 4; ++component) f32(b, materialValue(field, component));
    f32(b, .375f);
    for (uint32_t field = 3; field < 7; ++field)
        for (uint32_t component = 0; component < 4; ++component) f32(b, materialValue(field, component));
    for (uint32_t vertex = 0; vertex < 3; ++vertex)
        for (bool sphere : {false, true})
            for (uint32_t component = 0; component < 3; ++component) f32(b, normalValue(vertex, component, sphere));
    return b;
}
visual::Inputs read(const Bytes& sidecar, const Bytes& frame) {
    const auto parsed = packet::Read(frame.data(), frame.size());
    return visual::Read(sidecar.data(), sidecar.size(), frame.data(), frame.size(), parsed);
}
void rejected(const std::string& name, const Bytes& sidecar, const Bytes& frame, const char* reason) {
    try { read(sidecar, frame); check(false, name + " accepted"); }
    catch (const std::runtime_error& e) { check(std::string(e.what()) == reason, name + " reason: " + e.what()); }
    catch (...) { check(false, name + " wrong exception"); }
}
void save(const char* name, const Bytes& b) {
    std::ofstream stream(name, std::ios::binary | std::ios::trunc);
    stream.write(reinterpret_cast<const char*>(b.data()), std::streamsize(b.size()));
    stream.close(); check(bool(stream), std::string("write original fixture ") + name);
}
void values(const visual::Inputs& in, uint32_t flags, const std::string& name) {
    check(bits(in.eye[0]) == bits(-0.f) && in.eye[1] == 1.5f && in.eye[2] == -7.25f, name + " raw eye");
    bool exact = true;
    for (uint32_t i = 0; i < 16; ++i) exact = exact && bits(in.sphereRotation[i]) == bits(matrixValue(i));
    check(exact, name + " all sphere matrix values");
    check(in.materials.size() == 1, name + " material count");
    if (in.materials.size() == 1) {
        const auto& material = in.materials[0];
        check(material.objectId == ObjectId && material.localIndex == 0, name + " 64-bit material identity");
        check(material.opaque == (flags & 1) && material.subTextureAdditive == ((flags & 2) != 0), name + " independent opaque/Additive flags");
        const visual::Vector* vectors[] = {&material.eg, &material.specular, &material.toon,
            &material.texMul, &material.texAdd, &material.sphereMul, &material.sphereAdd};
        for (uint32_t field = 0; field < 7; ++field) {
            exact = true;
            for (uint32_t component = 0; component < 4; ++component)
                exact = exact && bits((*vectors[field])[component]) == bits(materialValue(field, component));
            check(exact, name + " material vector " + std::to_string(field) + " raw xyzw");
        }
        check(material.cutoff == .375f, name + " alpha cutoff");
    }
    check(in.worldNormals.size() == 3 && in.sphereNormals.size() == 3, name + " normal counts");
    exact = in.worldNormals.size() == 3 && in.sphereNormals.size() == 3;
    if (exact) for (uint32_t vertex = 0; vertex < 3; ++vertex)
        for (uint32_t component = 0; component < 3; ++component)
            exact = exact && bits(in.worldNormals[vertex][component]) == bits(normalValue(vertex, component, false))
                          && bits(in.sphereNormals[vertex][component]) == bits(normalValue(vertex, component, true));
    check(exact, name + " all raw world/sphere normals (not normalized)");
}
}

int main() {
    try {
        // A known SHA-256 vector prevents a self-consistent wrong hash builder
        // and product reader from passing only because they share a function.
        const uint8_t abc[] = {'a', 'b', 'c'};
        const std::array<uint8_t, 32> knownSha = {0xba,0x78,0x16,0xbf,0x8f,0x01,0xcf,0xea,
            0x41,0x41,0x40,0xde,0x5d,0xae,0x22,0x23,0xb0,0x03,0x61,0xa3,0x96,0x17,0x7a,0x9c,
            0xb4,0x10,0xff,0x61,0xf2,0x00,0x15,0xad};
        check(visual::hash(abc, sizeof abc) == knownSha, "product SHA-256 known abc vector");

        const Bytes frame1 = makeFrame(1, 0), frame2 = makeFrame(2, 0);
        const Bytes sub1 = makeFrame(1, 3), sub2 = makeFrame(2, 3);
        const Bytes noDiffuse = makeFrame(2, 3, false);
        for (const auto& frame : {frame1, frame2}) {
            const auto parsed = packet::Read(frame.data(), frame.size());
            for (uint32_t flags : {0u, 1u}) values(read(makeVisual(frame, 2, flags), frame), flags,
                std::string("FRMv") + std::to_string(parsed.packetVersion) + "/VIS2 flags=" + std::to_string(flags));
        }
        // Historical FRMv1 mode3 remains parseable here. The Session capability
        // check, outside this CPU reader test, still rejects its missing UV1.
        values(read(makeVisual(sub1, 2, 1), sub1), 1, "legacy FRMv1 mode3/VIS2");
        for (uint32_t flags = 0; flags <= 3; ++flags) {
            values(read(makeVisual(sub2, 3, flags), sub2), flags, "FRMv2 SubTexture/VIS3 flags=" + std::to_string(flags));
            values(read(makeVisual(noDiffuse, 3, flags), noDiffuse), flags, "no diffuse texture/VIS3 flags=" + std::to_string(flags));
        }
        const auto parsedUv = packet::Read(sub2.data(), sub2.size());
        bool exact = parsedUv.packetVersion == 2 && parsedUv.additionalUvChannelCount == 1 && parsedUv.additionalUvs.size() == 3;
        if (exact) for (uint32_t vertex = 0; vertex < 3; ++vertex)
            for (uint32_t channel = 0; channel < 4; ++channel)
                for (uint32_t component = 0; component < 4; ++component)
                    exact = exact && bits(parsedUv.additionalUvs[vertex].uv[channel][component]) == bits(uvValue(vertex, channel, component));
        check(exact, "real product FRMv2 retains every raw UV xyzw including inactive channels");
        check(packet::Read(noDiffuse.data(), noDiffuse.size()).materials[0].diffuseTexture == -1,
              "Additive flag fixture really has no diffuse texture");
        // A VIS3 packet with no SubTexture remains legal when its new flag is 0.
        values(read(makeVisual(frame2, 3, 1), frame2), 1, "FRMv2 ordinary sphere/VIS3");
        save("original-visual-frame-v1.packet", frame1);
        save("original-visual-frame-v2-uv.packet", sub2);
        save("original-visual-frame-v2-no-diffuse.packet", noDiffuse);
        save("original-visual-v2.packet", makeVisual(frame1, 2, 1));
        save("original-visual-v3-multiply.packet", makeVisual(sub2, 3, 1));
        save("original-visual-v3-additive-no-diffuse.packet", makeVisual(noDiffuse, 3, 3));

        const Bytes good = makeVisual(sub2, 3, 3);
        check(good.size() == Normals + 3 * 24, "fixture has exact 128 header + 132 material + 24/vertex layout");
        for (uint32_t version : {0u, 1u, 4u, std::numeric_limits<uint32_t>::max()}) {
            Bytes bad = good; putU32(bad, VisualVersion, version);
            rejected("unknown VIS version " + std::to_string(version), bad, sub2, "visual sidecar header mismatch");
        }
        rejected("FRMv1 rejects VIS3", makeVisual(frame1, 3, 1), frame1, "visual sidecar header mismatch");
        rejected("legacy FRMv1 mode3 rejects VIS3", makeVisual(sub1, 3, 1), sub1, "visual sidecar header mismatch");
        rejected("FRMv2 SubTexture rejects VIS2", makeVisual(sub2, 2, 1), sub2,
                 "UV1 SubTexture requires explicit VIS1 v3 resolved material state");
        rejected("no diffuse SubTexture still requires VIS3", makeVisual(noDiffuse, 2, 1), noDiffuse,
                 "UV1 SubTexture requires explicit VIS1 v3 resolved material state");
        for (uint32_t flag : {2u, 3u, 4u, 0x80000000u, std::numeric_limits<uint32_t>::max()}) {
            Bytes bad = makeVisual(frame1, 2, 1); putU32(bad, MaterialFlags, flag);
            rejected("VIS2 unknown flag " + std::to_string(flag), bad, frame1, "visual material ownership or flags mismatch");
        }
        for (uint32_t flag : {4u, 5u, 7u, 0x80000000u, std::numeric_limits<uint32_t>::max()}) {
            Bytes bad = good; putU32(bad, MaterialFlags, flag);
            rejected("VIS3 unknown flag " + std::to_string(flag), bad, sub2, "visual material ownership or flags mismatch");
        }
        for (int32_t mode : {0, 1, 2}) {
            const Bytes frame = makeFrame(2, mode);
            rejected("VIS3 Additive flag requires mode3 (mode=" + std::to_string(mode) + ")",
                     makeVisual(frame, 3, 2), frame, "visual material ownership or flags mismatch");
        }
        for (size_t offset : {Material, Material + 4, MaterialLocal}) {
            Bytes bad = good; bad.at(offset) ^= 1;
            rejected("material owner/local mismatch at " + std::to_string(offset), bad, sub2,
                     "visual material ownership or flags mismatch");
        }
        for (size_t offset : {VisualMaterials, VisualVertices}) {
            for (uint32_t count : {0u, 2u, 4u, std::numeric_limits<uint32_t>::max()}) {
                Bytes bad = good; putU32(bad, offset, count);
                rejected("material/vertex count mismatch", bad, sub2, "visual sidecar header mismatch");
            }
        }
        Bytes bad = good; bad[0] = '!'; rejected("wrong VIS magic", bad, sub2, "visual sidecar magic mismatch");
        for (size_t offset = VisualSha; offset < VisualSha + 32; ++offset) {
            bad = good; bad[offset] ^= 1;
            rejected("SHA digest byte " + std::to_string(offset - VisualSha), bad, sub2,
                     "visual sidecar belongs to a different frame packet");
        }
        // Both alternate frames still parse successfully and have equal counts.
        // Bind to exact raw frame bytes, including UV data and texture identity.
        Bytes changedFrame = sub2; changedFrame.back() ^= 1;
        rejected("different raw UV bytes", good, changedFrame, "visual sidecar belongs to a different frame packet");
        rejected("different diffuse texture identity", good, noDiffuse, "visual sidecar belongs to a different frame packet");
        for (size_t length = 0; length < good.size(); ++length) {
            const Bytes shortPacket(good.begin(), good.begin() + length);
            rejected("truncated visual byte " + std::to_string(length), shortPacket, sub2,
                     length < 128 ? "visual sidecar size invalid" : "frame packet: truncated");
        }
        bad = good; bad.push_back(0); rejected("visual trailing byte", bad, sub2, "visual sidecar trailing bytes");
        for (size_t offset = VisualEye; offset < good.size(); offset += 4) {
            // Identity/flag fields are integers, not visual floating payload.
            if (offset >= Material && offset < MaterialValues) continue;
            for (uint32_t nonFinite : {0x7f800000u, 0xff800000u, 0x7fc00001u}) {
                bad = good; putU32(bad, offset, nonFinite);
                rejected("non-finite visual float at " + std::to_string(offset), bad, sub2, "frame packet: non-finite float");
            }
        }
        for (float value : {-.125f, 1.125f}) {
            bad = good; putU32(bad, Cutoff, bits(value));
            rejected("out-of-range cutoff", bad, sub2, "visual alpha cutoff invalid");
        }
        for (float value : {0.f, 1.f}) {
            bad = good; putU32(bad, Cutoff, bits(value));
            check(read(bad, sub2).materials[0].cutoff == value, "cutoff endpoint accepted");
        }
        try {
            visual::Read(good.data(), size_t(16 * 1024 * 1024) + 1,
                         sub2.data(), sub2.size(), parsedUv);
            check(false, "oversized visual accepted");
        } catch (const std::runtime_error& e) {
            check(std::string(e.what()) == "visual sidecar size invalid", "oversized visual rejected before reading");
        }
    } catch (const std::exception& e) {
        ++failures; std::cerr << "UNEXPECTED " << e.what() << '\n';
    }
    std::cout << "CPU visual UV parser checks=" << checks << " failures=" << failures << '\n';
    return failures ? 1 : 0;
}
