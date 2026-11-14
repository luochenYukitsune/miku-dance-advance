#pragma once
// Project-owned VIS1 v2/v3/v4/v5/v6/v7 contract. SHA binds the exact borrowed FRM1
// bytes parsed in this call; there is no second file read or file-system lookup.
#include <windows.h>
#include <wincrypt.h>
#include <algorithm>
#include "frame_packet.hpp"
namespace visual {
using Vector=std::array<float,4>;
struct Material { uint64_t objectId;uint32_t localIndex,opaque;bool subTextureAdditive=false;Vector eg,specular,toon;float cutoff;Vector texMul,texAdd,sphereMul,sphereAdd,diffuse; };
struct Inputs { std::array<float,3> eye;packet::Matrix sphereRotation;std::array<float,3> accessoryLightAmbient{},accessoryLightDiffuse{},accessoryLightSpecular{};std::vector<Material> materials;std::vector<std::array<float,3>> worldNormals,sphereNormals; };
inline std::array<uint8_t,32> hash(const uint8_t* data,size_t length) {
 HCRYPTPROV provider=0;HCRYPTHASH digest=0;
 struct Cleanup { HCRYPTPROV& p;HCRYPTHASH& h;~Cleanup(){if(h)CryptDestroyHash(h);if(p)CryptReleaseContext(p,0);} } cleanup{provider,digest};
 if(!CryptAcquireContextW(&provider,nullptr,nullptr,PROV_RSA_AES,CRYPT_VERIFYCONTEXT)||!CryptCreateHash(provider,CALG_SHA_256,0,0,&digest))throw std::runtime_error("visual packet SHA initialization failed");
 while(length){DWORD n=static_cast<DWORD>(std::min(length,size_t(1024*1024)));if(!CryptHashData(digest,data,n,0))throw std::runtime_error("visual packet SHA failed");data+=n;length-=n;}
 std::array<uint8_t,32> out{};DWORD n=32;if(!CryptGetHashParam(digest,HP_HASHVAL,out.data(),&n,0)||n!=32)throw std::runtime_error("visual packet SHA result failed");return out;
}
inline Inputs Read(const uint8_t* bytes,size_t length,const uint8_t* frameBytes,size_t frameLength,const packet::Frame& frame) {
 if(length<128||length>16*1024*1024)throw std::runtime_error("visual sidecar size invalid");
 packet::detail::Reader r(bytes,length);
 if(std::memcmp(r.take(8),"MDA9VIS1",8))throw std::runtime_error("visual sidecar magic mismatch");
 const auto version=r.u32();
 if((version<2||version>7)||(version==3&&frame.packetVersion!=2)||((version==4)!=(frame.packetVersion==3))||((version==5)!=(frame.packetVersion==4))||((version==6)!=(frame.packetVersion==5))||((version==7)!=(frame.packetVersion==6))||r.u32()!=frame.materials.size()||r.u32()!=frame.vertices.size())throw std::runtime_error("visual sidecar header mismatch");
 auto actual=hash(frameBytes,frameLength);if(std::memcmp(r.take(32),actual.data(),32))throw std::runtime_error("visual sidecar belongs to a different frame packet");
 Inputs out{};for(auto& x:out.eye)x=r.f32();r.matrix(out.sphereRotation);out.materials.reserve(frame.materials.size());
 if(version>=4)for(auto light:{&out.accessoryLightAmbient,&out.accessoryLightDiffuse,&out.accessoryLightSpecular})for(auto& x:*light)x=r.f32();
 size_t owner=0;
 for(auto& source:frame.materials){Material m{};m.objectId=r.u64();m.localIndex=r.u32();const auto flags=r.u32();m.opaque=flags&1;m.subTextureAdditive=(flags&2)!=0;
  while(owner<frame.objects.size()&&frame.objects[owner].id!=source.objectId)++owner;
  if(owner==frame.objects.size())throw std::runtime_error("visual material has no typed owner");
  const bool accessory=frame.objects[owner].kind==packet::Accessory;
  if(m.objectId!=source.objectId||m.localIndex!=source.localIndex||(flags&~uint32_t((version>=3&&!accessory)?3:1))||(m.subTextureAdditive&&source.sphereMode!=3))throw std::runtime_error("visual material ownership or flags mismatch");
  if(source.sphereMode==3&&frame.packetVersion==2&&version!=3)throw std::runtime_error("UV1 SubTexture requires explicit VIS1 v3 resolved material state");
  for(auto v:{&m.eg,&m.specular,&m.toon})for(auto& x:*v)x=r.f32();m.cutoff=r.f32();if(m.cutoff<0||m.cutoff>1)throw std::runtime_error("visual alpha cutoff invalid");
  for(auto v:{&m.texMul,&m.texAdd,&m.sphereMul,&m.sphereAdd})for(auto& x:*v)x=r.f32();
  if(version>=4)for(auto& x:m.diffuse)x=r.f32();else std::copy(source.diffuse,source.diffuse+4,m.diffuse.begin());out.materials.push_back(m);
 }
 out.worldNormals.resize(frame.vertices.size());out.sphereNormals.resize(frame.vertices.size());
 for(size_t i=0;i<frame.vertices.size();++i){for(auto& x:out.worldNormals[i])x=r.f32();for(auto& x:out.sphereNormals[i])x=r.f32();}
 if(r.remaining())throw std::runtime_error("visual sidecar trailing bytes");return out;
}
}
