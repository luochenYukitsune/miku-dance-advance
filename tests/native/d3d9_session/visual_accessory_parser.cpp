// Reuse the independent tiny FRM3 fixture; no native Session/device is loaded.
#define main frameAccessoryCpuMain
#include "frame_accessory_parser.cpp"
#undef main
#include "../../../native/Mmd.Modern.D3D9.Compatibility/visual_packet.hpp"
namespace {
Bytes vis(const Fixture& f,uint32_t version=4){
 Bytes b;for(char c:std::string("MDA9VIS1"))b.push_back(c);u32(b,version);u32(b,1);u32(b,3);
 const auto digest=visual::hash(f.b.data(),f.b.size());b.insert(b.end(),digest.begin(),digest.end());
 for(float v:{0.f,0.f,5.f})f32(b,v);mat(b);
 if(version==4)for(float v:{.4f,.5f,.6f,10.f,10.f,10.f,.7f,.8f,.9f})f32(b,v);
 u64(b,Accessory);u32(b,0);u32(b,1);
 for(int i=0;i<12;++i)f32(b,float(i)*.1f);f32(b,0);for(int i=0;i<16;++i)f32(b,float(i)*.01f);
 if(version==4)for(float v:{.11f,.22f,.33f,.44f})f32(b,v);
 for(int i=0;i<18;++i)f32(b,float(i)+.5f);return b;
}
visual::Inputs readVis(const Bytes& b,const Fixture& f){const auto parsed=packet::Read(f.b.data(),f.b.size());return visual::Read(b.data(),b.size(),f.b.data(),f.b.size(),parsed);}
void rejectVis(Bytes b,const Fixture& f,const char* name){try{readVis(b,f);check(false,name);}catch(const std::runtime_error&){check(true,name);}}
}
int main(){try{
 const auto f=make();const auto b=vis(f);const auto v=readVis(b,f);
 check(v.materials[0].diffuse[0]==.11f&&v.materials[0].diffuse[3]==.44f,"VIS4 explicit resolved DifColor");
 check(v.accessoryLightAmbient[0]==.4f&&v.accessoryLightDiffuse[2]==10.f&&v.accessoryLightSpecular[1]==.8f,"VIS4 exact accessory light coefficients");
 check(v.worldNormals[0][0]==.5f&&v.sphereNormals[0][0]==3.5f,"VIS4 normal streams retain meanings");
 rejectVis(vis(f,2),f,"FRM3 rejects VIS2");rejectVis(vis(f,3),f,"FRM3 rejects VIS3");
 auto bad=b;put(bad,176,3);rejectVis(bad,f,"accessory rejects model-only UV1 additive flag");
 bad=b;bad[20]^=1;rejectVis(bad,f,"VIS4 exact FRM3 SHA");
 for(size_t n=0;n<b.size();++n){bad=b;bad.resize(n);rejectVis(bad,f,"VIS4 every truncation");}
 bad=b;bad.push_back(0);rejectVis(bad,f,"VIS4 trailing bytes");
}catch(const std::exception& e){++failures;std::cerr<<"UNEXPECTED "<<e.what()<<'\n';}
 std::cout<<"CPU accessory visual checks="<<checks<<" failures="<<failures<<'\n';return failures?1:0;}
