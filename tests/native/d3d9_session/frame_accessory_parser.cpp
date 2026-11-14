// Project-owned CPU admission tests. No GPU, host runtime or user asset reads.
#include "../../../native/Mmd.Modern.D3D9.Compatibility/frame_packet.hpp"
#include <iostream>
#include <functional>
namespace {
using Bytes=std::vector<uint8_t>;
constexpr uint64_t Model=0xf123456700000001ULL,Accessory=0x8123456700000002ULL;
void u32(Bytes& b,uint32_t x){for(int i=0;i<4;++i)b.push_back(static_cast<uint8_t>(x>>(8*i)));}
void u64(Bytes& b,uint64_t x){u32(b,uint32_t(x));u32(b,uint32_t(x>>32));}
void f32(Bytes& b,float x){uint32_t n;std::memcpy(&n,&x,4);u32(b,n);}
void f64(Bytes& b,double x){uint64_t n;std::memcpy(&n,&x,8);u64(b,n);}
void str(Bytes& b,const char* x){const size_t n=std::strlen(x);u32(b,uint32_t(n));b.insert(b.end(),x,x+n);}
void mat(Bytes& b){for(int i=0;i<16;++i)f32(b,(i%5)==0?1.f:0.f);}
void put(Bytes& b,size_t at,uint32_t x){for(int i=0;i<4;++i)b.at(at+i)=uint8_t(x>>(8*i));}
struct Fixture{Bytes b;size_t accessory=0,tail=0,material=0,index=0;};
Fixture make(bool hidden=false,bool uv=false,const std::vector<int32_t>& modelOrders={2}){
 Fixture f;auto& b=f.b;for(char c:std::string("MDA9FRM1"))b.push_back(c);
 u32(b,3);u32(b,8);u32(b,8);u32(b,uint32_t(modelOrders.size()+1));u32(b,hidden?0:3);u32(b,hidden?0:3);u32(b,1);u32(b,0);
 f64(b,7.5);f32(b,.25f);mat(b);mat(b);f32(b,0);f32(b,-1);f32(b,0);for(int i=0;i<4;++i)f32(b,1);
 u32(b,2|(uv?1:0));u32(b,17); // raw split is deliberately not the negative-row count
 for(size_t i=0;i<modelOrders.size();++i){
  u64(b,Model+i);u32(b,1);u32(b,uint32_t(modelOrders[i]));u32(b,0);u32(b,0);u32(b,0);u32(b,0);u32(b,0);
  str(b,"carrier.pmx");str(b,"carrier");u32(b,1);str(b,"parent");mat(b);u32(b,0);
 }
 f.accessory=b.size();u64(b,Accessory);u32(b,2);u32(b,uint32_t(-1));u32(b,0);u32(b,hidden?0:3);u32(b,0);u32(b,1);u32(b,hidden?0:1);
 str(b,"triangle.x");str(b,"accessory");u32(b,0);u32(b,0);f.tail=b.size();mat(b);
 for(float v:{.2f,.3f,.4f,7.f,-8.f,9.f,.23f,.6f})f32(b,v);
 u32(b,0);u32(b,1);u32(b,1);u64(b,Model);u32(b,0);
 if(!hidden)for(int i=0;i<3;++i)for(float v:{float(i),0.f,0.f,0.f,0.f,1.f,0.f,0.f})f32(b,v);
 f.index=b.size();if(!hidden)for(uint32_t i=0;i<3;++i)u32(b,i);
 f.material=b.size();u64(b,Accessory);u32(b,0);u32(b,0);u32(b,hidden?0:3);u32(b,3);
 for(float v:{.8f,.6f,.4f,.4f,.3f,.2f,.1f,12.f,.05f,.06f,.07f})f32(b,v);
 u32(b,0);u32(b,uint32_t(-1));u32(b,uint32_t(-1));
 if(uv){u32(b,1);u32(b,hidden?0:3);if(!hidden)for(int i=0;i<48;++i)f32(b,float(i));}
 return f;
}
int checks=0,failures=0;
void check(bool yes,const char* name){++checks;if(!yes){++failures;std::cerr<<"FAIL "<<name<<'\n';}}
void reject(Fixture f,const char* name,const std::function<void(Fixture&)>& change){change(f);try{packet::Read(f.b.data(),f.b.size());check(false,name);}catch(const std::runtime_error&){check(true,name);}}
}
int main(){
 try{
  auto f=make();const auto p=packet::Read(f.b.data(),f.b.size());
  check(p.packetVersion==3&&p.flags==2&&p.rawPreAccessorySplit==17,"explicit v3 flags/raw split");
  const auto& a=p.objects[1];check(a.kind==2&&a.id==Accessory&&a.signedOrder==-1&&a.vertexCount==3,"true typed high64 accessory identity");
  check(a.translation[0]==.2f&&a.rotation[0]==7.f&&a.rawScale==.23f&&a.opacity==.6f,"raw pose not decomposed or normalized");
  check(a.attached==1&&a.parentModelId==Model&&a.parentBoneIndex==0,"hidden parent exact high64 identity");
  check(p.materials[0].diffuse[3]==.4f&&p.materials[0].emissive[2]==.07f,"source alpha and emissive retained");
  auto h=make(true);const auto ph=packet::Read(h.b.data(),h.b.size());check(ph.objects[1].materialCount==1&&ph.vertices.empty(),"hidden accessory retains authored materials");
  auto shadow=f;put(shadow.b,shadow.tail+96,1);check(packet::Read(shadow.b.data(),shadow.b.size()).objects[1].shadowEnabled==1,"shadow eligibility is retained without requesting a shadow pass");
  auto uv=make(false,true);check(packet::Read(uv.b.data(),uv.b.size()).additionalUvs.size()==3,"v3 optional complete UV trailer");
  auto zeroModel=f;put(zeroModel.b,228,0);check(packet::Read(zeroModel.b.data(),zeroModel.b.size()).objects[0].signedOrder==0,"model explicit zero order is retained");
  auto negativeModel=make(false,false,{-3});check(packet::Read(negativeModel.b.data(),negativeModel.b.size()).objects[0].signedOrder==-3,"model negative order is retained");
  auto absCollision=f;put(absCollision.b,absCollision.accessory+12,uint32_t(-2));check(packet::Read(absCollision.b.data(),absCollision.b.size()).objects[1].signedOrder==-2,"absolute order collision remains legal");
  auto signedTie=f;put(signedTie.b,signedTie.accessory+12,2);check(packet::Read(signedTie.b.data(),signedTie.b.size()).objects[1].signedOrder==2,"cross-kind signed order tie remains legal");
  auto modelTies=make(false,false,{2,2});const auto tied=packet::Read(modelTies.b.data(),modelTies.b.size());check(tied.objects[0].id==Model&&tied.objects[1].id==Model+1&&tied.objects[0].signedOrder==tied.objects[1].signedOrder,"same-kind order ties retain distinct source rows");
  auto postBeforeModel=f;put(postBeforeModel.b,postBeforeModel.accessory+12,1);check(packet::Read(postBeforeModel.b.data(),postBeforeModel.b.size()).objects[1].signedOrder==1,"positive accessory order need not exceed model order");
  reject(f,"unknown flags",[](Fixture& x){put(x.b,208,6);});
  reject(f,"accessory flag absent",[](Fixture& x){put(x.b,208,0);});
  reject(f,"unknown kind",[](Fixture& x){put(x.b,x.accessory+8,3);});
  reject(f,"zero signed order",[](Fixture& x){put(x.b,x.accessory+12,0);});
  reject(f,"INT_MIN signed order",[](Fixture& x){put(x.b,x.accessory+12,0x80000000);});
  reject(f,"INT_MIN model order",[](Fixture& x){put(x.b,228,0x80000000);});
  reject(f,"range does not partition",[](Fixture& x){put(x.b,x.accessory+16,1);});
  reject(f,"non-affine world",[](Fixture& x){put(x.b,x.tail+12,0x3f800000);});
  reject(f,"singular world",[](Fixture& x){put(x.b,x.tail,0);});
  reject(f,"nonpositive raw scale",[](Fixture& x){put(x.b,x.tail+88,0);});
  reject(f,"opacity outside 0..1",[](Fixture& x){put(x.b,x.tail+92,0x40000000);});
  reject(f,"invalid boolean",[](Fixture& x){put(x.b,x.tail+100,2);});
  reject(f,"missing parent",[](Fixture& x){put(x.b,x.tail+108,5);});
  reject(f,"parent bone out of range",[](Fixture& x){put(x.b,x.tail+116,1);});
  reject(f,"accessory cannot impersonate model bones",[](Fixture& x){put(x.b,x.accessory+8,1);});
  reject(f,"accessory texture enum invalid",[](Fixture& x){put(x.b,x.material+68,6);});
  reject(f,"index outside owner range",[](Fixture& x){put(x.b,x.index,3);});
  for(size_t n=0;n<f.b.size();++n){Fixture cut=f;cut.b.resize(n);try{packet::Read(cut.b.data(),cut.b.size());check(false,"truncation");}catch(const std::runtime_error&){check(true,"truncation");}}
  reject(f,"trailing byte",[](Fixture& x){x.b.push_back(0);});
 }catch(const std::exception& e){++failures;std::cerr<<"UNEXPECTED "<<e.what()<<'\n';}
 std::cout<<"CPU accessory frame checks="<<checks<<" failures="<<failures<<'\n';return failures?1:0;
}
