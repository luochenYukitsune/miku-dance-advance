#pragma once
#include <array>
// Project-owned parser extracted from visual225/frame_packet.hpp. This version
// parses borrowed frozen byte spans; no file paths or user assets are loaded.
#include <cmath>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <limits>
#include <stdexcept>
#include <string>
#include <unordered_set>
#include <unordered_map>
#include <vector>
#include <utility>

namespace packet {
using Matrix = std::array<float, 16>;
struct Vertex { float position[3], normal[3], uv[2]; };
// FRM1 v5 carries Core-expanded same-sample original edges. The native
// renderer never reimplements the load-time selection or expansion math.
struct EdgeVertex { float position[3]; uint32_t color; };
static_assert(sizeof(EdgeVertex)==16,"original XYZ+DIFFUSE edge layout");
// FRM1 v2 carries all four raw xyzw vectors, including channels above the
// declared active count. Keep these values intact for effect vertex inputs.
struct AdditionalUvVertex { float uv[4][4]; };
static_assert(sizeof(AdditionalUvVertex) == 64, "additional UV vertex must be four float4 vectors");
struct Bone { std::string name; Matrix world; };
struct Morph { std::string name; float weight; };
enum : uint32_t { Model=1, Accessory=2, HasAdditionalUv=1, HasAccessories=2, HasModelMaterialMetadata=4, HasClassicPasses=8, HasMedia=16 };
enum : uint32_t { SelfShadow=1, GenerateShadow=2, PartialFar=4, GroundShadow=8, TranslucentGround=16, GroundStencil=32, EffectFrame=64 };
enum : uint32_t { CastShadow=1, ReceiveShadow=2, CastGround=4, DrawOutline=8, ShadowDoubleSided=16 };
struct ClassicState {
    uint32_t blockVersion=1,flags=0,width=0,height=0,filter=1,address=2,comparison=2;
    Matrix lightViewProjection{}; float groundDirection[3]{};
    // Exact source D3DMATERIAL9 slices, rather than a guessed inverse of a
    // resolved ground color. MME reads this material and the current light.
    float groundMaterial[17]{},groundLightAmbient[3]{},accessoryGroundGray=0;
    float modelLightAmbient[3]{},modelLightDiffuse[3]{},modelLightSpecular[3]{};
};
struct Object {
    uint64_t id; uint32_t materialStart, materialCount, visible; std::string sourcePath, name; std::vector<Bone> bones; std::vector<Morph> morphs;
    uint32_t kind=Model; int32_t signedOrder=0; uint32_t vertexStart=0,vertexCount=0;
    Matrix world{}; float translation[3]{},rotation[3]{},rawScale=1,opacity=1;
    uint32_t shadowEnabled=0,additiveBlend=0,attached=0; uint64_t parentModelId=0;int32_t parentBoneIndex=-1;
    uint32_t outlineEligible=0;
};
struct Material { uint64_t objectId; uint32_t localIndex, startIndex, indexCount, flags; float diffuse[4], specular[3], ambient[3], shininess; int32_t diffuseTexture, toonTexture, sphereTexture, sphereMode; float emissive[3]{}; uint32_t accessoryTextureType=0,classicFlags=0; float edgeColor[4]{}; };
struct Texture { uint32_t width, height; std::vector<uint8_t> rgba; };
// Decoded, same-sample inputs only. Native never opens media files or advances
// a decoder/animation clock. Source dimensions also determine original layout.
struct MediaLayer { uint32_t kind,width,height;bool drawBackground=true;float left,top,right,bottom;std::vector<uint8_t> rgba; };
struct Frame { uint32_t packetVersion, width, height; double sourceFrame; float frameSeconds; Matrix view, projection; float lightDirection[3], clearColor[4]; std::vector<Object> objects; std::vector<Vertex> vertices; std::vector<uint32_t> indices; std::vector<Material> materials; std::vector<Texture> textures; uint32_t additionalUvChannelCount; std::vector<AdditionalUvVertex> additionalUvs; uint32_t flags=0;int32_t rawPreAccessorySplit=0; ClassicState classic;std::vector<EdgeVertex> edges;std::vector<MediaLayer> media;uint32_t mediaBlockVersion=0,screenCaptureMode=0; };

namespace detail {
constexpr uint64_t MaxFile = uint64_t{1} << 30;
[[noreturn]] inline void fail(const char* reason) { throw std::runtime_error(std::string("frame packet: ") + reason); }
inline bool validUtf8(const uint8_t* p, size_t n) {
    size_t i = 0;
    while (i < n) {
        uint8_t c = p[i++];
        if (c <= 0x7f) continue;
        uint32_t value; size_t extra; uint32_t minimum;
        if (c >= 0xc2 && c <= 0xdf) { value = c & 0x1f; extra = 1; minimum = 0x80; }
        else if (c >= 0xe0 && c <= 0xef) { value = c & 0x0f; extra = 2; minimum = 0x800; }
        else if (c >= 0xf0 && c <= 0xf4) { value = c & 7; extra = 3; minimum = 0x10000; }
        else return false;
        if (extra > n - i) return false;
        for (size_t j = 0; j < extra; ++j) { uint8_t b = p[i++]; if ((b & 0xc0) != 0x80) return false; value = (value << 6) | (b & 0x3f); }
        if (value < minimum || value > 0x10ffff || (value >= 0xd800 && value <= 0xdfff)) return false;
    }
    return true;
}
class Reader {
    const uint8_t* data_; size_t size_, pos_ = 0;
public:
    Reader(const uint8_t* data, size_t size) : data_(data), size_(size) { if (!data || size > MaxFile) fail("invalid or oversized byte span"); }
    explicit Reader(const std::vector<uint8_t>& data) : Reader(data.data(), data.size()) {}
    size_t remaining() const { return size_ - pos_; }
    const uint8_t* take(size_t n) { if (n > remaining()) fail("truncated"); const uint8_t* p = data_ + pos_; pos_ += n; return p; }
    void requireCount(uint32_t n, uint64_t bytesEach, uint32_t cap) const { if (n > cap || uint64_t(n) * bytesEach > remaining()) fail("invalid count"); }
    uint32_t u32() { const uint8_t* p = take(4); return uint32_t(p[0]) | (uint32_t(p[1]) << 8) | (uint32_t(p[2]) << 16) | (uint32_t(p[3]) << 24); }
    uint64_t u64() { uint64_t lo = u32(), hi = u32(); return lo | (hi << 32); }
    int32_t i32() { uint32_t bits = u32(); int32_t out; std::memcpy(&out, &bits, sizeof out); return out; }
    float f32() { static_assert(sizeof(float) == 4 && std::numeric_limits<float>::is_iec559, "IEEE float required"); uint32_t bits = u32(); float out; std::memcpy(&out, &bits, sizeof out); if (!std::isfinite(out)) fail("non-finite float"); return out; }
    double f64() { static_assert(sizeof(double) == 8 && std::numeric_limits<double>::is_iec559, "IEEE double required"); uint64_t bits = u64(); double out; std::memcpy(&out, &bits, sizeof out); if (!std::isfinite(out)) fail("non-finite double"); return out; }
    std::string string() { uint32_t n = u32(); if (n > 1048576) fail("string too long"); const uint8_t* p = take(n); if (!validUtf8(p, n) || std::memchr(p, 0, n)) fail("invalid UTF-8 or embedded NUL"); return std::string(reinterpret_cast<const char*>(p), n); }
    void matrix(Matrix& m) { for (auto& v : m) v = f32(); }
};
inline bool affineInvertible(const Matrix& m) {
    if(m[3]!=0||m[7]!=0||m[11]!=0||m[15]!=1)return false;
    const double determinant=double(m[0])*(double(m[5])*m[10]-double(m[6])*m[9])-double(m[1])*(double(m[4])*m[10]-double(m[6])*m[8])+double(m[2])*(double(m[4])*m[9]-double(m[5])*m[8]);
    return std::isfinite(determinant)&&determinant!=0;
}
}
inline Frame Read(const uint8_t* bytes, size_t length) {
    detail::Reader r(bytes, length);
    const auto magic = r.take(8);
    if (std::memcmp(magic, "MDA9FRM1", 8) != 0) detail::fail("bad magic or version");
    Frame frame{};
    frame.packetVersion = r.u32();
    if (frame.packetVersion < 1 || frame.packetVersion > 6) detail::fail("bad magic or version");
    frame.width = r.u32(); frame.height = r.u32();
    const uint32_t objects = r.u32(), vertices = r.u32(), indices = r.u32(), materials = r.u32(), textures = r.u32();
    if (!frame.width || !frame.height || objects > 1000000 || vertices > 10000000 || indices > 60000000 || materials > 1000000 || textures > 4096 || indices % 3) detail::fail("invalid frame dimensions or count");
    frame.sourceFrame = r.f64(); frame.frameSeconds = r.f32(); r.matrix(frame.view); r.matrix(frame.projection);
    for (float& v : frame.lightDirection) v = r.f32(); for (float& v : frame.clearColor) v = r.f32();
    const bool typed=frame.packetVersion>=3;
    if(typed){
        frame.flags=r.u32();frame.rawPreAccessorySplit=r.i32();
        if(frame.flags&~uint32_t(frame.packetVersion==6?31:frame.packetVersion==5?15:frame.packetVersion==4?7:3))detail::fail("unknown typed frame flags");
        if(frame.packetVersion==4&&!(frame.flags&HasModelMaterialMetadata))detail::fail("material metadata frame flag is required");
        if(frame.packetVersion==5&&(frame.flags&(HasModelMaterialMetadata|HasClassicPasses))!=(HasModelMaterialMetadata|HasClassicPasses))detail::fail("classic passes require complete material metadata");
        if(frame.packetVersion==6&&(frame.flags&(HasModelMaterialMetadata|HasClassicPasses|HasMedia))!=(HasModelMaterialMetadata|HasClassicPasses|HasMedia))detail::fail("media requires explicit original passes and material metadata");
    }
    r.requireCount(objects, 36, 1000000); frame.objects.reserve(objects);
    std::unordered_set<uint64_t> ids; uint32_t materialCursor = 0,vertexCursor=0;bool hasAccessories=false;
    std::unordered_map<uint64_t,size_t> objectById;
    for (uint32_t i = 0; i < objects; ++i) {
        Object o{}; o.id = r.u64();o.signedOrder=static_cast<int32_t>(i)+1;
        if(typed){
            o.kind=r.u32();o.signedOrder=r.i32();o.vertexStart=r.u32();o.vertexCount=r.u32();
            if((o.kind!=Model&&o.kind!=Accessory)||(o.kind==Accessory&&!o.signedOrder)||o.signedOrder==std::numeric_limits<int32_t>::min())detail::fail("invalid typed kind or signed order");
            if(o.vertexStart!=vertexCursor||o.vertexCount>vertices-vertexCursor)detail::fail("invalid typed vertex coverage");
            vertexCursor+=o.vertexCount;hasAccessories|=o.kind==Accessory;
        }
        o.materialStart = r.u32(); o.materialCount = r.u32(); o.visible = r.u32();
        if (!o.id || !ids.insert(o.id).second || o.visible > 1 || (!o.visible && o.materialCount != 0 && o.kind!=Accessory && !(frame.flags&HasModelMaterialMetadata)) || (!o.visible&&o.vertexCount) || o.materialStart != materialCursor || o.materialCount > materials - materialCursor) detail::fail("invalid object identity or material range");
        materialCursor += o.materialCount; o.sourcePath = r.string(); o.name = r.string();
        uint32_t count = r.u32(); r.requireCount(count, 68, 1000000); o.bones.reserve(count);
        for (uint32_t j = 0; j < count; ++j) { Bone b{}; b.name = r.string(); r.matrix(b.world); o.bones.push_back(std::move(b)); }
        count = r.u32(); r.requireCount(count, 8, 1000000); o.morphs.reserve(count);
        for (uint32_t j = 0; j < count; ++j) { Morph m{}; m.name = r.string(); m.weight = r.f32(); o.morphs.push_back(std::move(m)); }
        if(o.kind==Accessory){
            if(!o.bones.empty()||!o.morphs.empty())detail::fail("accessory cannot carry model bone or morph tables");
            r.matrix(o.world);for(auto& x:o.translation)x=r.f32();for(auto& x:o.rotation)x=r.f32();o.rawScale=r.f32();o.opacity=r.f32();
            o.shadowEnabled=r.u32();o.additiveBlend=r.u32();o.attached=r.u32();o.parentModelId=r.u64();o.parentBoneIndex=r.i32();
            if(!detail::affineInvertible(o.world)||!(o.rawScale>0)||!std::isfinite(o.rawScale*10)||o.opacity<0||o.opacity>1||o.shadowEnabled>1||o.additiveBlend>1||o.attached>1||(!o.attached&&(o.parentModelId||o.parentBoneIndex!=-1))||(o.attached&&(!o.parentModelId||o.parentBoneIndex<0)))detail::fail("invalid accessory world, pose or parent");
        }
        objectById.emplace(o.id,frame.objects.size());
        frame.objects.push_back(std::move(o));
    }
    if (materialCursor != materials) detail::fail("incomplete object material coverage");
    if(typed){
        if(vertexCursor!=vertices||bool(frame.flags&HasAccessories)!=hasAccessories)detail::fail("typed flags or vertex coverage mismatch");
        for(const auto& o:frame.objects)if(o.kind==Accessory&&o.attached){auto found=objectById.find(o.parentModelId);if(found==objectById.end())detail::fail("accessory parent model is missing");const auto& p=frame.objects[found->second];if(p.kind!=Model||size_t(o.parentBoneIndex)>=p.bones.size())detail::fail("accessory parent kind or bone mismatch");}
    }
    r.requireCount(vertices, 32, 10000000); frame.vertices.reserve(vertices);
    for (uint32_t i = 0; i < vertices; ++i) { Vertex v{}; for (float& x : v.position) x = r.f32(); for (float& x : v.normal) x = r.f32(); for (float& x : v.uv) x = r.f32(); frame.vertices.push_back(v); }
    r.requireCount(indices, 4, 60000000); frame.indices.reserve(indices);
    for (uint32_t i = 0; i < indices; ++i) { uint32_t index = r.u32(); if (index >= vertices) detail::fail("vertex index out of range"); frame.indices.push_back(index); }
    r.requireCount(materials, typed?80:84, 1000000); frame.materials.reserve(materials);
    uint32_t indexCursor = 0; size_t owner = 0;
    for (uint32_t i = 0; i < materials; ++i) {
        while (owner < frame.objects.size() && i >= uint64_t(frame.objects[owner].materialStart) + frame.objects[owner].materialCount) ++owner;
        if (owner == frame.objects.size()) detail::fail("missing material owner");
        const Object& o = frame.objects[owner]; Material m{};
        m.objectId = r.u64(); m.localIndex = r.u32(); m.startIndex = r.u32(); m.indexCount = r.u32(); m.flags = r.u32();
        if (m.objectId != o.id || m.localIndex != i - o.materialStart || m.startIndex != indexCursor || m.indexCount % 3 || m.indexCount > indices - indexCursor || (m.flags & ~uint32_t(3))) detail::fail("invalid material or index coverage");
        if(typed){
            if(!o.visible&&m.indexCount)detail::fail("hidden object has drawing indices");
            for(uint32_t n=0;n<m.indexCount;++n){const auto index=frame.indices[indexCursor+n];if(index<o.vertexStart||uint64_t(index)>=uint64_t(o.vertexStart)+o.vertexCount)detail::fail("index outside typed owner vertex range");}
        }
        indexCursor += m.indexCount; for (float& x : m.diffuse) x = r.f32(); for (float& x : m.specular) x = r.f32();
        auto validTexture = [textures](int32_t t) { return t == -1 || (t >= 0 && uint32_t(t) < textures); };
        if(o.kind==Accessory){
            m.shininess=r.f32();for(auto& x:m.emissive)x=r.f32();m.accessoryTextureType=r.u32();m.diffuseTexture=r.i32();m.sphereTexture=r.i32();m.toonTexture=-1;m.sphereMode=0;
            const auto t=m.accessoryTextureType;
            if(t>5||!validTexture(m.diffuseTexture)||!validTexture(m.sphereTexture)||(t<=2&&m.sphereTexture!=-1)||(t>=1&&t<=2&&m.diffuseTexture<0)||(t==3&&(m.diffuseTexture!=-1||m.sphereTexture!=-1))||(t>=4&&(m.diffuseTexture<0||m.sphereTexture<0)))detail::fail("invalid accessory texture role or reference");
        }else{
            for (float& x : m.ambient) x = r.f32(); m.shininess = r.f32();
            m.diffuseTexture = r.i32(); m.toonTexture = r.i32(); m.sphereTexture = r.i32(); m.sphereMode = r.i32();
            if (!validTexture(m.diffuseTexture) || !validTexture(m.toonTexture) || !validTexture(m.sphereTexture) || m.sphereMode < 0 || m.sphereMode > 3) detail::fail("invalid texture reference or sphere mode");
        }
        frame.materials.push_back(m);
    }
    if (indexCursor != indices) detail::fail("incomplete material index coverage");
    r.requireCount(textures, 12, 4096); frame.textures.reserve(textures);
    uint64_t textureBytes = 0;
    for (uint32_t i = 0; i < textures; ++i) {
        Texture t{}; t.width = r.u32(); t.height = r.u32(); uint32_t n = r.u32();
        if (!t.width || !t.height || t.width > 4096 || t.height > 4096 || uint64_t(t.width) * t.height * 4 != n) detail::fail("invalid texture extent");
        textureBytes += n; if (textureBytes >= detail::MaxFile) detail::fail("texture payload too large");
        const uint8_t* p = r.take(n); t.rgba.assign(p, p + n); frame.textures.push_back(std::move(t));
    }
    if (frame.packetVersion == 2 || (typed&&(frame.flags&HasAdditionalUv))) {
        frame.additionalUvChannelCount = r.u32();
        if (frame.additionalUvChannelCount < 1 || frame.additionalUvChannelCount > 4) detail::fail("invalid additional UV channel count");
        const uint32_t uvVertices = r.u32();
        if (uvVertices != vertices) detail::fail("additional UV vertex count mismatch");
        r.requireCount(uvVertices, 64, 10000000); frame.additionalUvs.reserve(uvVertices);
        for (uint32_t i = 0; i < uvVertices; ++i) {
            AdditionalUvVertex uv{};
            for (auto& channel : uv.uv) for (float& value : channel) value = r.f32();
            frame.additionalUvs.push_back(uv);
        }
    }
    if(frame.packetVersion>=5){
        if(std::memcmp(r.take(8),"MDA9CLS1",8))detail::fail("classic pass block version mismatch");
        auto& c=frame.classic;c.blockVersion=r.u32();if(c.blockVersion<1||c.blockVersion>2)detail::fail("classic pass block version mismatch");
        c.flags=r.u32();c.width=r.u32();c.height=r.u32();c.filter=r.u32();c.address=r.u32();c.comparison=r.u32();
        if(c.blockVersion<2&&!(c.flags&EffectFrame))detail::fail("fixed-function passes require explicit original model lights");
        if((c.flags&~uint32_t(127))||((c.flags&(GenerateShadow|PartialFar))&&!(c.flags&SelfShadow))||((c.flags&SelfShadow)&&!(c.flags&EffectFrame)))detail::fail("invalid original pass flags");
        if((c.flags&SelfShadow)?(!c.width||!c.height||c.width>4096||c.height>4096):(c.width||c.height))detail::fail("invalid original shadow dimensions");
        if((c.filter!=1&&c.filter!=2)||c.address!=2||(c.comparison!=1&&c.comparison!=2))detail::fail("unsupported original shadow sampler");
        r.matrix(c.lightViewProjection);for(auto& x:c.groundDirection)x=r.f32();for(auto& x:c.groundMaterial)x=r.f32();for(auto& x:c.groundLightAmbient)x=r.f32();c.accessoryGroundGray=r.f32();
        if(c.blockVersion>=2)for(auto light:{c.modelLightAmbient,c.modelLightDiffuse,c.modelLightSpecular})for(size_t i=0;i<3;++i)light[i]=r.f32();
        if((c.flags&GroundShadow)&&c.groundDirection[1]>=0)detail::fail("original ground projection requires outgoing light Y below zero");
        if(r.u32()!=objects)detail::fail("classic object count mismatch");
        for(auto& o:frame.objects){if(r.u64()!=o.id)detail::fail("classic object identity mismatch");const auto flags=r.u32();if((flags&~uint32_t(c.blockVersion>=2?3:1))||(o.kind==Accessory&&flags))detail::fail("invalid original model pass flags");o.outlineEligible=flags&1;if(o.kind==Model)o.additiveBlend=(flags>>1)&1;}
        if(r.u32()!=materials)detail::fail("classic material count mismatch");
        for(auto& m:frame.materials){if(r.u64()!=m.objectId||r.u32()!=m.localIndex)detail::fail("classic material identity mismatch");m.classicFlags=r.u32();if(m.classicFlags&~uint32_t(31))detail::fail("unknown classic material pass flags");for(auto& x:m.edgeColor)x=r.f32();}
        if(r.u32()!=vertices)detail::fail("classic edge count mismatch");r.requireCount(vertices,16,10000000);frame.edges.reserve(vertices);
        for(uint32_t i=0;i<vertices;++i){EdgeVertex e{};for(auto& x:e.position)x=r.f32();e.color=r.u32();frame.edges.push_back(e);}
    }
    if(frame.packetVersion==6){
        if(std::memcmp(r.take(8),"MDA9MED1",8))detail::fail("media block version mismatch");
        frame.mediaBlockVersion=r.u32();if(frame.mediaBlockVersion<1||frame.mediaBlockVersion>2)detail::fail("media block version mismatch");
        const auto sourceFrame=r.f64();const auto frameSeconds=r.f32();
        if(std::memcmp(&sourceFrame,&frame.sourceFrame,sizeof sourceFrame)||std::memcmp(&frameSeconds,&frame.frameSeconds,sizeof frameSeconds))detail::fail("media belongs to a different frame sample");
        if(frame.mediaBlockVersion>=2){frame.screenCaptureMode=r.u32();if(frame.screenCaptureMode>3)detail::fail("invalid original screen capture mode");}
        const auto count=r.u32();if((frame.mediaBlockVersion==1&&count<1)||count>2)detail::fail("invalid media layer count");
        frame.media.reserve(count);uint32_t previousKind=0;
        for(uint32_t i=0;i<count;++i){
            MediaLayer layer{};layer.kind=r.u32();
            if(frame.mediaBlockVersion>=2){const auto draw=r.u32();if(draw>1||(!draw&&layer.kind!=2))detail::fail("invalid original background eligibility");layer.drawBackground=draw!=0;}
            layer.width=r.u32();layer.height=r.u32();
            layer.left=r.f32();layer.top=r.f32();layer.right=r.f32();layer.bottom=r.f32();const auto n=r.u32();
            if(layer.kind<1||layer.kind>2||layer.kind<=previousKind)detail::fail("media must contain unique picture then video layers");
            if(!layer.width||!layer.height||layer.width>32768||layer.height>32768||uint64_t(layer.width)*layer.height*4!=n||n>256u*1024u*1024u)detail::fail("invalid media extent or RGBA length");
            const auto* p=r.take(n);layer.rgba.assign(p,p+n);previousKind=layer.kind;
            // The original AVI surface is X8R8G8B8, never source alpha.
            if(layer.kind==2)for(size_t a=3;a<layer.rgba.size();a+=4)if(layer.rgba[a]!=255)detail::fail("video RGBA must be opaque");
            frame.media.push_back(std::move(layer));
        }
    }
    if (r.remaining()) detail::fail("trailing bytes");
    return frame;
}
}
