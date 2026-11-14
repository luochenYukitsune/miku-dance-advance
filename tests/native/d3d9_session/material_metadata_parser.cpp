// CPU-only FRM4/VIS5 admission. Reuse independent packet-writing helpers;
// never load a native Session, host, GPU, HWND or user asset.
#define main existingAccessoryFixtureMain
#include "frame_accessory_parser.cpp"
#undef main
#include "../../../native/Mmd.Modern.D3D9.Compatibility/visual_packet.hpp"
namespace {
struct MetadataFixture { Bytes frame;size_t hidden=0,material=0; };
MetadataFixture metadata(){
 MetadataFixture f;auto& b=f.frame;
 for(char c:std::string("MDA9FRM1"))b.push_back(c);
 for(uint32_t n:{4,8,8,2,3,3,3,1})u32(b,n);
 f64(b,7.5);f32(b,.25f);mat(b);mat(b);
 for(float v:{0.f,-1.f,0.f,0.f,0.f,0.f,1.f})f32(b,v);
 u32(b,packet::HasModelMaterialMetadata);u32(b,17);
 f.hidden=b.size();
 // Hidden authored model: two real subsets, no vertices, bones or triangles.
 u64(b,Model);for(uint32_t n:{1,0,0,0,0,2,0})u32(b,n);
 str(b,"hidden.pmx");str(b,"hidden");u32(b,0);u32(b,0);
 u64(b,Model+1);for(uint32_t n:{1,2,0,3,2,1,1})u32(b,n);
 str(b,"visible.pmx");str(b,"visible");u32(b,0);u32(b,0);
 for(int i=0;i<3;++i)for(float v:{float(i),0.f,0.f,0.f,0.f,1.f,0.f,0.f})f32(b,v);
 for(uint32_t i=0;i<3;++i)u32(b,i);
 f.material=b.size();
 for(uint32_t i=0;i<3;++i){
  u64(b,i<2?Model:Model+1);u32(b,i<2?i:0);u32(b,0);u32(b,i<2?0:3);u32(b,1);
  for(float v:{.7f,.2f,.4f,.6f,.1f,.2f,.3f,.2f,.3f,.4f,9.f})f32(b,v);
  u32(b,i==0?0:uint32_t(-1));u32(b,uint32_t(-1));u32(b,uint32_t(-1));u32(b,i==0?3:0);
 }
 u32(b,1);u32(b,1);u32(b,4);for(uint8_t value:{21,43,65,87})b.push_back(value);
 return f;
}
Bytes metadataVisual(const MetadataFixture& f){
 Bytes b;for(char c:std::string("MDA9VIS1"))b.push_back(c);
 u32(b,5);u32(b,3);u32(b,3);auto digest=visual::hash(f.frame.data(),f.frame.size());b.insert(b.end(),digest.begin(),digest.end());
 for(float v:{0.f,0.f,5.f})f32(b,v);mat(b);
 for(float v:{.4f,.5f,.6f,10.f,10.f,10.f,.7f,.8f,.9f})f32(b,v);
 for(uint32_t i=0;i<3;++i){
  u64(b,i<2?Model:Model+1);u32(b,i<2?i:0);u32(b,i==0?2:0);
  for(int j=0;j<12;++j)f32(b,float(j)*.01f);f32(b,0);
  for(int j=0;j<16;++j)f32(b,float(j)*.02f);
  for(float v:{.3f,.4f,.5f,.6f})f32(b,v);
 }
 for(int i=0;i<18;++i)f32(b,float(i)+.5f);return b;
}
packet::Frame readMetadata(const MetadataFixture& f){return packet::Read(f.frame.data(),f.frame.size());}
visual::Inputs readMetadataVisual(const MetadataFixture& f,const Bytes& b){return visual::Read(b.data(),b.size(),f.frame.data(),f.frame.size(),readMetadata(f));}
template<class F>void rejectMetadata(F action,const char* name){try{action();check(false,name);}catch(const std::runtime_error&){check(true,name);}}
}
int main(){try{
 const auto f=metadata();const auto p=readMetadata(f);const auto b=metadataVisual(f);const auto v=readMetadataVisual(f,b);
 check(p.packetVersion==4&&p.flags==4&&p.rawPreAccessorySplit==17,"FRM4 independent metadata flag and original raw split");
 check(p.objects[0].id==Model&&!p.objects[0].visible&&p.objects[0].materialCount==2&&p.objects[0].vertexCount==0&&p.objects[0].bones.empty(),"hidden high64 identity retains real two-subset table without dummy geometry");
 check(p.objects[0].signedOrder==0&&p.objects[1].signedOrder==2,"explicit original model order0 sentinel retained");
 check(p.vertices.size()==3&&p.indices.size()==3&&p.materials[2].objectId==Model+1&&p.objects[1].materialStart==2,"visible geometry and material cursors survive preceding hidden metadata");
 check(p.materials[0].diffuseTexture==0&&p.textures[0].rgba[2]==65&&p.materials[0].sphereMode==3&&p.additionalUvs.empty(),"hidden source texture and sphere metadata need no fake UV geometry");
 check(v.materials.size()==3&&v.materials[0].subTextureAdditive&&v.materials[1].localIndex==1&&v.materials[2].objectId==Model+1&&v.materials[0].diffuse[3]==.6f,"VIS5 exact hidden and visible subset identity, tint and blend metadata");
 auto bad=f;put(bad.frame,8,3);put(bad.frame,208,0);rejectMetadata([&]{readMetadata(bad);},"old FRM3 still rejects hidden nonzero model material table");
 bad=f;put(bad.frame,208,0);rejectMetadata([&]{readMetadata(bad);},"FRM4 requires metadata flag");
 bad=f;put(bad.frame,208,12);rejectMetadata([&]{readMetadata(bad);},"FRM4 rejects unknown feature flags");
 bad=f;put(bad.frame,bad.hidden+20,1);rejectMetadata([&]{readMetadata(bad);},"hidden model cannot carry drawing vertices");
 bad=f;put(bad.frame,bad.material+16,3);rejectMetadata([&]{readMetadata(bad);},"hidden material cannot consume visible drawing indices");
 bad=f;put(bad.frame,bad.material+84+8,0);rejectMetadata([&]{readMetadata(bad);},"hidden local subset sequence is exact");
 for(uint32_t version:{2,3,4}){auto wrong=b;put(wrong,8,version);rejectMetadata([&]{readMetadataVisual(f,wrong);},"FRM4 requires VIS5 exact version pair");}
 auto wrong=b;put(wrong,164,17);rejectMetadata([&]{readMetadataVisual(f,wrong);},"VIS5 high64 object identity is exact");
 wrong=b;put(wrong,164+148+8,0);rejectMetadata([&]{readMetadataVisual(f,wrong);},"VIS5 subset identity is exact");
 wrong=b;wrong[20]^=1;rejectMetadata([&]{readMetadataVisual(f,wrong);},"VIS5 SHA binds exact promoted frame");
 wrong=b;wrong.pop_back();rejectMetadata([&]{readMetadataVisual(f,wrong);},"VIS5 truncated normal stream rejected");
 wrong=b;wrong.push_back(0);rejectMetadata([&]{readMetadataVisual(f,wrong);},"VIS5 trailing bytes rejected");
 }catch(const std::exception& e){++failures;std::cerr<<"UNEXPECTED "<<e.what()<<'\n';}
 std::cout<<"CPU material metadata checks="<<checks<<" failures="<<failures<<'\n';return failures?1:0;
}
