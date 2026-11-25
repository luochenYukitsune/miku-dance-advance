// Project-owned Windows x64 ordinary-surface capture/preview session. The surface math,
// material loop and Exp* adapter derive from the verified visual225 probe; this
// is a persistent owner-thread API, with no probe main, file output or UI hooks.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define MDA_D3D9_SESSION_BUILD
#include "mda_d3d9_session.h"
#include <windows.h>
#include <d3d9.h>
#include "d3dx9.h"
#include "MMDExport.h"
#include "mme_host_api.h"
#include "mda_mme_bridge_v1.h"
#include "mmhack_api.h"
#include "mmeffect/emm_manager.h"
#include "mmeffect/effect_engine.h"
#include "mmeffect/material_bind.h"
#include "mmeffect/model_data.h"
#include "mmeffect/mme_context.h"
#include "mmeffect/mme_globals.h"
#include "mmeffect/pass_planner.h"
#include "mmeffect/mda_object_fx_policy.hpp"
#include "frame_packet.hpp"
#include "visual_packet.hpp"
#include "object_fx.hpp"
#include "effect_resource_map.hpp"
#include "emm_configuration.hpp"
#include "surface_source.hpp"
#include "owner_window.hpp"
#include <algorithm>
#include <atomic>
#include <climits>
#include <cfloat>
#include <cmath>
#include <cstddef>
#include <cstdio>
#include <exception>
#include <memory>
#include <sstream>
#include <stdexcept>
#include <type_traits>

extern "C" HRESULT WINAPI D3DXCreateEffect(IDirect3DDevice9*,const void*,UINT,const D3DXMACRO*,ID3DXInclude*,DWORD,ID3DXEffectPool*,ID3DXEffect**,ID3DXBuffer**);
extern "C" HRESULT WINAPI D3DXLoadSurfaceFromMemory(IDirect3DSurface9*,const PALETTEENTRY*,const RECT*,const void*,D3DFORMAT,UINT,const PALETTEENTRY*,const RECT*,DWORD,D3DCOLOR);
extern "C" HRESULT WINAPI D3DXFilterTexture(IDirect3DBaseTexture9*,const PALETTEENTRY*,UINT,DWORD);
static_assert(sizeof(void*)==8,"Windows x64 is required");
static_assert(sizeof(packet::Vertex)==32,"D3D9 vertex layout mismatch");
static_assert(sizeof(packet::AdditionalUvVertex)==64,"D3D9 additional UV layout mismatch");
static_assert(sizeof(Mda9ErrorV1)==40 && sizeof(Mda9AbiV1)==40,"ABI v1 layout");
static_assert(sizeof(Mda9CreateV1)==24 && sizeof(Mda9ResizeV1)==16,"ABI v1 layout");
static_assert(sizeof(Mda9CaptureV1)==88 && sizeof(Mda9CaptureResultV1)==80,"ABI v1 layout");
static_assert(sizeof(Mda9CreatePreviewV1)==48 && sizeof(Mda9PresentV1)==72 && sizeof(Mda9PresentResultV1)==80,"Present ABI v1 layout");
static_assert(sizeof(Mda9EditorOverlayPrimitiveV1)==56 && sizeof(Mda9PresentV2)==88,"Editor overlay ABI layout");
static_assert(offsetof(Mda9PresentV2,primitives)==sizeof(Mda9PresentV1),"PresentV2 preserves the V1 prefix");
static_assert(sizeof(Mda9ObjectFxAssignmentV1)==48 && sizeof(Mda9ObjectFxConfigurationV1)==32,"Object FX ABI v1 layout");
static_assert(sizeof(Mda9EffectResourceBindingV1)==48 && sizeof(Mda9ObjectFxConfigurationV2)==48,"Package resources ABI layout");
static_assert(sizeof(Mda9Utf8SpanV1)==16 && sizeof(Mda9EmmBindingV1)==40 && sizeof(Mda9EmmRootSeedV1)==40,"EMM identity ABI layout");
static_assert(sizeof(Mda9EmmEntryV1)==48 && sizeof(Mda9EmmSectionV1)==120 && sizeof(Mda9EmmConfigurationV1)==80,"EMM section ABI layout");

namespace {
struct Failure:std::runtime_error { Mda9Status status;Failure(Mda9Status s,const std::string& m):std::runtime_error(m),status(s){} };
[[noreturn]] void fail(Mda9Status s,const char* m){throw Failure(s,m);}
void check(HRESULT hr,const char* what){if(FAILED(hr)){std::ostringstream s;s<<what<<" HRESULT=0x"<<std::hex<<static_cast<unsigned long>(hr);throw Failure(MDA9_DEVICE_FAILURE,s.str());}}
template<class T> struct Com {
 T* p=nullptr;~Com(){reset();}Com()=default;Com(const Com&)=delete;Com& operator=(const Com&)=delete;
 void reset(){if(p){p->Release();p=nullptr;}} T** put(){reset();return &p;}T* operator->()const{return p;}operator T*()const{return p;}
};
D3DMATRIX identity(){D3DMATRIX m{};m._11=m._22=m._33=m._44=1;return m;}
D3DMATRIX matrix(const packet::Matrix& a){D3DMATRIX m;std::memcpy(&m,a.data(),sizeof m);return m;}
D3DMATRIX multiply(const D3DMATRIX& a,const D3DMATRIX& b){D3DMATRIX r{};for(int i=0;i<4;++i)for(int j=0;j<4;++j)for(int k=0;k<4;++k)r.m[i][j]+=a.m[i][k]*b.m[k][j];return r;}
// Pinned MIT model_renderers.cpp BuildToonTransform. Generated fixed-function
// normals are in view space, so construct the original basis from that same
// space's incoming light before applying its -pi/2, half-scale and translation.
D3DMATRIX originalToonTransform(const float incoming[3],const D3DMATRIX& viewRotation){
 D3DXVECTOR3 direction{incoming[0]*viewRotation._11+incoming[1]*viewRotation._21+incoming[2]*viewRotation._31,incoming[0]*viewRotation._12+incoming[1]*viewRotation._22+incoming[2]*viewRotation._32,incoming[0]*viewRotation._13+incoming[1]*viewRotation._23+incoming[2]*viewRotation._33};D3DXVec3Normalize(&direction,&direction);
 D3DXVECTOR3 vertical{0,direction.x==0&&direction.z==0?0.f:1.f,direction.x==0&&direction.z==0?-1.f:0.f};
 auto cross=[](const D3DXVECTOR3& a,const D3DXVECTOR3& b){return D3DXVECTOR3{float(double(a.y)*b.z-double(a.z)*b.y),float(double(a.z)*b.x-double(a.x)*b.z),float(double(a.x)*b.y-double(a.y)*b.x)};};
 auto horizontal=cross(vertical,direction);D3DXVec3Normalize(&horizontal,&horizontal);vertical=cross(direction,horizontal);D3DXVec3Normalize(&vertical,&vertical);
 D3DXMATRIX basis{},rotation{},scaling{},translation{},temporary{},projected{},result{};
 basis.m[0][0]=horizontal.x;basis.m[1][0]=horizontal.y;basis.m[2][0]=horizontal.z;basis.m[0][1]=vertical.x;basis.m[1][1]=vertical.y;basis.m[2][1]=vertical.z;basis.m[0][2]=direction.x;basis.m[1][2]=direction.y;basis.m[2][2]=direction.z;basis.m[3][3]=1;
 D3DXMatrixRotationX(&rotation,-(3.141592f*.5f));D3DXMatrixScaling(&scaling,.5f,-.5f,1);D3DXMatrixTranslation(&translation,.5f,.5f,0);D3DXMatrixMultiply(&temporary,&basis,&rotation);D3DXMatrixMultiply(&projected,&temporary,&scaling);D3DXMatrixMultiply(&result,&projected,&translation);
 D3DMATRIX out{};std::memcpy(&out,&result,sizeof out);return out;
}
bool originalDrawBefore(const packet::Object& a,const packet::Object& b){
 const auto group=[](const packet::Object& o){return o.kind==packet::Model?1:o.signedOrder<0?0:2;};
 const int x=group(a),y=group(b);if(x!=y)return x<y;
 return x==0?-a.signedOrder < -b.signedOrder:a.signedOrder<b.signedOrder;
}
D3DCOLOR color(const float* c){auto b=[](float v){return static_cast<int>(std::max(0.f,std::min(1.f,v))*255.f+.5f);};return D3DCOLOR_ARGB(b(c[3]),b(c[0]),b(c[1]),b(c[2]));}
std::wstring wide(const std::string& s){if(s.empty())return {};int n=MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,s.data(),static_cast<int>(s.size()),nullptr,0);if(!n)fail(MDA9_PACKET_INVALID,"invalid UTF-8 metadata");std::wstring w(n,L'\0');if(!MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,s.data(),static_cast<int>(s.size()),w.data(),n))fail(MDA9_PACKET_INVALID,"UTF-8 metadata conversion failed");return w;}
std::string ansi(const std::wstring& w){if(w.empty())return {};int n=WideCharToMultiByte(CP_ACP,0,w.data(),static_cast<int>(w.size()),nullptr,0,nullptr,nullptr);if(!n)fail(MDA9_PACKET_INVALID,"ANSI metadata conversion failed");std::string s(n,'\0');if(!WideCharToMultiByte(CP_ACP,0,w.data(),static_cast<int>(w.size()),s.data(),n,nullptr,nullptr))fail(MDA9_PACKET_INVALID,"ANSI metadata conversion failed");return s;}
std::string exactAnsi(const std::wstring& w){
 if(w.empty())return {};
 const UINT page=GetACP();BOOL used=FALSE;const DWORD flags=page==CP_UTF8?WC_ERR_INVALID_CHARS:WC_NO_BEST_FIT_CHARS;
 const int n=WideCharToMultiByte(page,flags,w.data(),static_cast<int>(w.size()),nullptr,0,nullptr,page==CP_UTF8?nullptr:&used);
 if(!n||used)fail(MDA9_UNSUPPORTED,"object FX path/metadata cannot be represented losslessly in the host ANSI code page");
 std::string value(n,'\0');used=FALSE;
 if(!WideCharToMultiByte(page,flags,w.data(),static_cast<int>(w.size()),value.data(),n,nullptr,page==CP_UTF8?nullptr:&used)||used)
  fail(MDA9_UNSUPPORTED,"object FX ANSI path conversion failed");
 const int count=MultiByteToWideChar(page,0,value.data(),n,nullptr,0);std::wstring roundtrip(count,L'\0');
 if(!count||!MultiByteToWideChar(page,0,value.data(),n,roundtrip.data(),count)||roundtrip!=w)
  fail(MDA9_UNSUPPORTED,"object FX ANSI path conversion changed metadata");
 return value;
}
std::string diagnosticUtf8(const std::string& value){
 if(value.empty())return {};int n=MultiByteToWideChar(CP_ACP,0,value.data(),static_cast<int>(value.size()),nullptr,0);if(!n)return "MME compiler diagnostic encoding failed";
 std::wstring w(n,L'\0');MultiByteToWideChar(CP_ACP,0,value.data(),static_cast<int>(value.size()),w.data(),n);
 n=WideCharToMultiByte(CP_UTF8,0,w.data(),static_cast<int>(w.size()),nullptr,0,nullptr,nullptr);std::string out(n,'\0');if(n)WideCharToMultiByte(CP_UTF8,0,w.data(),static_cast<int>(w.size()),out.data(),n,nullptr,nullptr);return out;
}
struct FxLease {
 HANDLE handle=INVALID_HANDLE_VALUE;
 explicit FxLease(HANDLE value):handle(value){}
 ~FxLease(){if(handle!=INVALID_HANDLE_VALUE)CloseHandle(handle);}
 FxLease(FxLease&& other)noexcept:handle(other.handle){other.handle=INVALID_HANDLE_VALUE;}
 FxLease(const FxLease&)=delete;FxLease& operator=(const FxLease&)=delete;
};
struct ObjectFxState {
 const bool sceneEffects;
 explicit ObjectFxState(bool scene=false):sceneEffects(scene){}
 std::vector<objectfx::Row> rows;std::vector<FxLease> leases;
 std::unique_ptr<resources::Map> resources;
 std::unique_ptr<emmcfg::State> emm;
 std::set<std::string> emmFiles;
 std::map<std::string,std::shared_ptr<mme::LoadedEffect>> compiled;
 unsigned int previousAutoReload=mme::g_autoReload;
 Mda9Status failureStatus=MDA9_OK;std::string error;
};
struct Metadata {
 std::vector<std::wstring> paths;std::vector<std::string> ansiPaths;
 std::vector<std::vector<std::string>> bones,morphs;
 std::vector<size_t> models,accessories;
 std::unordered_map<uint64_t,size_t> byId;
 Metadata()=default;
 Metadata(const packet::Frame& f,uint64_t session,bool configured=false){
  for(auto& o:f.objects){
   // '?' cannot name a DOS drive; no directory or file is created. This is an
   // explicitly reported surface-only host namespace, not real MME metadata.
   const size_t index=paths.size();(o.kind==packet::Model?models:accessories).push_back(index);byId.emplace(o.id,index);
   paths.push_back(configured?wide(o.sourcePath):L"?:\\MDA9_ISOLATED_SURFACE\\"+std::to_wstring(session)+L"\\"+std::to_wstring(o.id)+(o.kind==packet::Model?L".pmx":L".x"));ansiPaths.push_back(configured?exactAnsi(paths.back()):ansi(paths.back()));
   bones.emplace_back();for(auto& b:o.bones)bones.back().push_back(ansi(wide(b.name)));
   morphs.emplace_back();for(auto& m:o.morphs)morphs.back().push_back(ansi(wide(m.name)));
  }
 }
};
struct FrozenDraw { packet::Frame frame;visual::Inputs appearance;Metadata metadata;std::vector<D3DMATERIAL9> materials;std::vector<Mda9EditorOverlayPrimitiveV1> editorOverlay; };
struct BackgroundVertex {float x,y,z,rhw;D3DCOLOR color;float u,v;};
static_assert(sizeof(BackgroundVertex)==28,"original XYZRHW+DIFFUSE+TEX1 background layout");

// D3DSBT_ALL omits render targets and depth surfaces. Keep the CURRENT depth
// attached throughout this operation: the session depth was created with
// Discard=TRUE, so detaching/rebinding it would discard the visibility sample.
struct OverlayDeviceState {
 IDirect3DDevice9* device;
 Com<IDirect3DStateBlock9> state;
 std::array<Com<IDirect3DSurface9>,4> targets;
 Com<IDirect3DSurface9> borrowedDepth;
 D3DVIEWPORT9 viewport{};
 UINT targetCount=0,maxPrimitiveCount=0;
 explicit OverlayDeviceState(IDirect3DDevice9* d):device(d){
  D3DCAPS9 caps{};check(d->GetDeviceCaps(&caps),"editor overlay device capabilities");
  targetCount=std::min<UINT>(caps.NumSimultaneousRTs,static_cast<UINT>(targets.size()));
  maxPrimitiveCount=caps.MaxPrimitiveCount;
  if(!targetCount)fail(MDA9_DEVICE_FAILURE,"editor overlay requires a color target");
  check(d->GetViewport(&viewport),"save editor overlay viewport");
  for(UINT i=0;i<targetCount;++i){const HRESULT hr=d->GetRenderTarget(i,targets[i].put());if(hr!=D3DERR_NOTFOUND||i==0)check(hr,"save editor overlay MRT");}
  const HRESULT hr=d->GetDepthStencilSurface(borrowedDepth.put());if(hr!=D3DERR_NOTFOUND)check(hr,"borrow current editor overlay depth");
  check(d->CreateStateBlock(D3DSBT_ALL,state.put()),"save editor overlay device state");
  check(state->Capture(),"capture editor overlay device state");
 }
 void bindColor(IDirect3DSurface9* surface,const D3DVIEWPORT9& vp){
  for(UINT i=1;i<targetCount;++i)check(device->SetRenderTarget(i,nullptr),"detach editor overlay MRT color");
  check(device->SetRenderTarget(0,surface),"bind editor overlay color target");
  check(device->SetViewport(&vp),"bind editor overlay viewport");
 }
 void restore(){
  // Attempt every restoration even after one call fails, and report the real
  // first failing HRESULT instead of disguising a partially restored device.
  HRESULT first=S_OK;auto keep=[&](HRESULT hr){if(FAILED(hr)&&SUCCEEDED(first))first=hr;};
  for(UINT i=1;i<targetCount;++i)keep(device->SetRenderTarget(i,nullptr));
  keep(device->SetRenderTarget(0,targets[0]));
  for(UINT i=1;i<targetCount;++i)keep(device->SetRenderTarget(i,targets[i]));
  keep(state->Apply());keep(device->SetViewport(&viewport));
  check(first,"restore editor overlay state/targets/viewport");
 }
};
template<class Fn> void withOverlayState(OverlayDeviceState& saved,Fn&& draw){
 try{draw();}catch(...){const auto original=std::current_exception();saved.restore();std::rethrow_exception(original);}
 saved.restore();
}
void editorOverlayState(IDirect3DDevice9* d,bool textured,bool premultiplied){
 check(d->SetVertexShader(nullptr),"editor overlay fixed vertex shader");check(d->SetPixelShader(nullptr),"editor overlay fixed pixel shader");
 check(d->SetFVF(D3DFVF_XYZRHW|D3DFVF_DIFFUSE|(textured?D3DFVF_TEX1:0)),"editor overlay FVF");
 check(d->SetStreamSourceFreq(0,1),"editor overlay noninstanced stream");
 check(d->SetRenderState(D3DRS_FILLMODE,D3DFILL_SOLID),"editor overlay solid fill");check(d->SetRenderState(D3DRS_CULLMODE,D3DCULL_NONE),"editor overlay no culling");
 for(auto state:{D3DRS_LIGHTING,D3DRS_SPECULARENABLE,D3DRS_FOGENABLE,D3DRS_RANGEFOGENABLE,D3DRS_STENCILENABLE,D3DRS_SCISSORTESTENABLE,D3DRS_ALPHATESTENABLE,D3DRS_SRGBWRITEENABLE,D3DRS_ZWRITEENABLE,D3DRS_CLIPPLANEENABLE})check(d->SetRenderState(state,FALSE),"editor overlay disabled inherited state");
 check(d->SetRenderState(D3DRS_CLIPPING,TRUE),"editor overlay clipping");check(d->SetRenderState(D3DRS_COLORVERTEX,TRUE),"editor overlay vertex color");
 check(d->SetRenderState(D3DRS_ZENABLE,FALSE),"editor overlay initial depth policy");check(d->SetRenderState(D3DRS_ZFUNC,D3DCMP_LESSEQUAL),"editor overlay depth comparison");
 check(d->SetRenderState(D3DRS_DEPTHBIAS,0),"editor overlay zero depth bias");check(d->SetRenderState(D3DRS_SLOPESCALEDEPTHBIAS,0),"editor overlay zero slope bias");
 check(d->SetRenderState(D3DRS_COLORWRITEENABLE,15),"editor overlay RGBA write");
 check(d->SetRenderState(D3DRS_ALPHABLENDENABLE,TRUE),"editor overlay blending");check(d->SetRenderState(D3DRS_BLENDOP,D3DBLENDOP_ADD),"editor overlay blend operation");
 check(d->SetRenderState(D3DRS_SRCBLEND,premultiplied?D3DBLEND_ONE:D3DBLEND_SRCALPHA),"editor overlay source blend");check(d->SetRenderState(D3DRS_DESTBLEND,D3DBLEND_INVSRCALPHA),"editor overlay destination blend");
 check(d->SetRenderState(D3DRS_SEPARATEALPHABLENDENABLE,TRUE),"editor overlay separate alpha");check(d->SetRenderState(D3DRS_SRCBLENDALPHA,D3DBLEND_ONE),"editor overlay source alpha");check(d->SetRenderState(D3DRS_DESTBLENDALPHA,D3DBLEND_INVSRCALPHA),"editor overlay destination alpha");check(d->SetRenderState(D3DRS_BLENDOPALPHA,D3DBLENDOP_ADD),"editor overlay alpha blend operation");
 for(DWORD stage=0;stage<16;++stage)check(d->SetTexture(stage,nullptr),"detach inherited editor overlay sampler");
 for(DWORD stage=0;stage<8;++stage){
  check(d->SetTextureStageState(stage,D3DTSS_COLOROP,D3DTOP_DISABLE),"disable inherited editor overlay color stage");check(d->SetTextureStageState(stage,D3DTSS_ALPHAOP,D3DTOP_DISABLE),"disable inherited editor overlay alpha stage");
  check(d->SetTextureStageState(stage,D3DTSS_TEXTURETRANSFORMFLAGS,D3DTTFF_DISABLE),"editor overlay no texture transform");check(d->SetTextureStageState(stage,D3DTSS_TEXCOORDINDEX,0),"editor overlay UV0");
  check(d->SetTextureStageState(stage,D3DTSS_RESULTARG,D3DTA_CURRENT),"editor overlay current stage result");
 }
 check(d->SetTextureStageState(0,D3DTSS_COLOROP,textured?D3DTOP_MODULATE:D3DTOP_SELECTARG2),"editor overlay color stage");check(d->SetTextureStageState(0,D3DTSS_COLORARG1,D3DTA_TEXTURE),"editor overlay texture color");check(d->SetTextureStageState(0,D3DTSS_COLORARG2,D3DTA_DIFFUSE),"editor overlay diffuse color");
 check(d->SetTextureStageState(0,D3DTSS_ALPHAOP,textured?D3DTOP_MODULATE:D3DTOP_SELECTARG2),"editor overlay alpha stage");check(d->SetTextureStageState(0,D3DTSS_ALPHAARG1,D3DTA_TEXTURE),"editor overlay texture alpha");check(d->SetTextureStageState(0,D3DTSS_ALPHAARG2,D3DTA_DIFFUSE),"editor overlay diffuse alpha");
 check(d->SetSamplerState(0,D3DSAMP_SRGBTEXTURE,FALSE),"editor overlay linear texture values");
 check(d->SetSamplerState(0,D3DSAMP_ADDRESSU,D3DTADDRESS_CLAMP),"editor overlay clamp U");check(d->SetSamplerState(0,D3DSAMP_ADDRESSV,D3DTADDRESS_CLAMP),"editor overlay clamp V");
 check(d->SetSamplerState(0,D3DSAMP_MINFILTER,D3DTEXF_POINT),"editor overlay point minification");check(d->SetSamplerState(0,D3DSAMP_MAGFILTER,D3DTEXF_POINT),"editor overlay point magnification");check(d->SetSamplerState(0,D3DSAMP_MIPFILTER,D3DTEXF_NONE),"editor overlay no mipmap");
}
struct OverlayPoint {double x,y,z;};
struct OverlayVertex {float x,y,z,rhw;D3DCOLOR color;};
static_assert(sizeof(OverlayVertex)==20,"editor stroke vertex layout");
// Tessellate in double and clip before conversion to float. Caller coordinates
// and sizes may be any finite floats, including far outside the viewport.
void overlayTriangle(std::vector<OverlayVertex>& vertices,OverlayPoint a,OverlayPoint b,OverlayPoint c,D3DCOLOR rgba,const D3DVIEWPORT9& vp){
 std::vector<OverlayPoint> polygon{a,b,c},next;next.reserve(8);
 for(int edge=0;edge<4&&!polygon.empty();++edge){
  next.clear();const bool vertical=edge<2;const double boundary=edge==0?vp.X:edge==1?double(vp.X)+vp.Width:edge==2?vp.Y:double(vp.Y)+vp.Height;
  const auto coordinate=[&](const OverlayPoint& p){return vertical?p.x:p.y;};
  const auto inside=[&](const OverlayPoint& p){return (edge==0||edge==2)?coordinate(p)>=boundary:coordinate(p)<=boundary;};
  auto previous=polygon.back();bool previousInside=inside(previous);
  for(const auto& current:polygon){const bool currentInside=inside(current);if(currentInside!=previousInside){const double t=(boundary-coordinate(previous))/(coordinate(current)-coordinate(previous));OverlayPoint p{previous.x+(current.x-previous.x)*t,previous.y+(current.y-previous.y)*t,previous.z+(current.z-previous.z)*t};if(vertical)p.x=boundary;else p.y=boundary;next.push_back(p);}if(currentInside)next.push_back(current);previous=current;previousInside=currentInside;}
  polygon.swap(next);
 }
 const auto emit=[&](const OverlayPoint& p){vertices.push_back({static_cast<float>(p.x-.5),static_cast<float>(p.y-.5),static_cast<float>(p.z),1,rgba});};
 for(size_t i=1;i+1<polygon.size();++i){emit(polygon[0]);emit(polygon[i]);emit(polygon[i+1]);}
}
void overlayQuad(std::vector<OverlayVertex>& v,OverlayPoint a,OverlayPoint b,OverlayPoint c,OverlayPoint d,D3DCOLOR rgba,const D3DVIEWPORT9& vp){overlayTriangle(v,a,b,c,rgba,vp);overlayTriangle(v,a,c,d,rgba,vp);}
void overlaySegment(std::vector<OverlayVertex>& v,OverlayPoint a,OverlayPoint b,double width,D3DCOLOR rgba,const D3DVIEWPORT9& vp){
 const double dx=b.x-a.x,dy=b.y-a.y,length=std::hypot(dx,dy),half=width*.5;
 if(length<=.000001){overlayQuad(v,{a.x-half,a.y-half,a.z},{a.x+half,a.y-half,a.z},{a.x+half,a.y+half,a.z},{a.x-half,a.y+half,a.z},rgba,vp);return;}
 const double x=-dy/length*half,y=dx/length*half;
 overlayQuad(v,{a.x-x,a.y-y,a.z},{b.x-x,b.y-y,b.z},{b.x+x,b.y+y,b.z},{a.x+x,a.y+y,a.z},rgba,vp);
}
std::vector<OverlayVertex> overlayVertices(const Mda9EditorOverlayPrimitiveV1& row,const D3DVIEWPORT9& vp){
 std::vector<OverlayVertex> v;v.reserve(512);const auto rgba=color(row.color);
 const OverlayPoint a{double(vp.X)+double(row.start[0])*vp.Width,double(vp.Y)+double(row.start[1])*vp.Height,(row.flags&MDA9_OVERLAY_DEPTH)?row.depth[0]:0};
 const OverlayPoint b{double(vp.X)+double(row.end[0])*vp.Width,double(vp.Y)+double(row.end[1])*vp.Height,(row.flags&MDA9_OVERLAY_DEPTH)?row.depth[1]:0};
 const double radius=row.radius,inner=std::max(0.,radius-double(row.width));
 if(row.shape==MDA9_OVERLAY_SEGMENT)overlaySegment(v,a,b,row.width,rgba,vp);
 else if(row.shape==MDA9_OVERLAY_WEDGE){
  const double dx=b.x-a.x,dy=b.y-a.y,length=std::hypot(dx,dy),x=length>.001?-dy/length*radius:0,y=length>.001?dx/length*radius:radius;
  overlaySegment(v,{a.x-x,a.y-y,0},b,row.width,rgba,vp);overlaySegment(v,{a.x+x,a.y+y,0},b,row.width,rgba,vp);
 }else if(row.shape==MDA9_OVERLAY_SQUARE){
  const std::array<OverlayPoint,4> outside{{{a.x-radius,a.y-radius,0},{a.x+radius,a.y-radius,0},{a.x+radius,a.y+radius,0},{a.x-radius,a.y+radius,0}}};
  const std::array<OverlayPoint,4> inside{{{a.x-inner,a.y-inner,0},{a.x+inner,a.y-inner,0},{a.x+inner,a.y+inner,0},{a.x-inner,a.y+inner,0}}};
  for(size_t i=0;i<4;++i){const auto j=(i+1)%4;overlayQuad(v,outside[i],outside[j],inside[j],inside[i],rgba,vp);}
 }else{
  constexpr int segments=64;constexpr double tau=6.2831853071795864769;
  for(int i=0;i<segments;++i){const double x=std::cos(tau*i/segments),y=std::sin(tau*i/segments),nx=std::cos(tau*(i+1)/segments),ny=std::sin(tau*(i+1)/segments);
   overlayQuad(v,{a.x+x*radius,a.y+y*radius,0},{a.x+nx*radius,a.y+ny*radius,0},{a.x+nx*inner,a.y+ny*inner,0},{a.x+x*inner,a.y+y*inner,0},rgba,vp);
  }
 }
 return v;
}
struct EditorOverlayTarget {
 Com<IDirect3DTexture9> texture;Com<IDirect3DSurface9> surface;
 uint32_t prepare(IDirect3DDevice9* d,uint32_t width,uint32_t height,const std::vector<Mda9EditorOverlayPrimitiveV1>& rows){
  if(rows.empty())return 0;
  OverlayDeviceState saved(d);D3DSURFACE_DESC current{},z{};check(saved.targets[0]->GetDesc(&current),"editor overlay current target description");
  if(current.Width!=width||current.Height!=height||current.MultiSampleType!=D3DMULTISAMPLE_NONE||current.MultiSampleQuality||current.Pool!=D3DPOOL_DEFAULT)
   fail(MDA9_UNSUPPORTED,"editor overlay requires a matching non-MSAA current color target");
  if(saved.borrowedDepth.p){
   check(saved.borrowedDepth->GetDesc(&z),"editor overlay borrowed depth description");if(z.Width<width||z.Height<height||z.MultiSampleType!=D3DMULTISAMPLE_NONE||z.MultiSampleQuality)fail(MDA9_UNSUPPORTED,"editor overlay current depth is incompatible with the private color target");
   Com<IDirect3D9> api;D3DDEVICE_CREATION_PARAMETERS creation{};D3DDISPLAYMODE display{};
   check(d->GetDirect3D(api.put()),"editor overlay Direct3D interface");check(d->GetCreationParameters(&creation),"editor overlay device creation parameters");check(api->GetAdapterDisplayMode(creation.AdapterOrdinal,&display),"editor overlay adapter format");
   check(api->CheckDepthStencilMatch(creation.AdapterOrdinal,creation.DeviceType,display.Format,D3DFMT_A8R8G8B8,z.Format),"editor overlay A8R8G8B8/current-depth compatibility");
  }
  else if(std::any_of(rows.begin(),rows.end(),[](const auto& row){return (row.flags&MDA9_OVERLAY_DEPTH)!=0;}))fail(MDA9_UNSUPPORTED,"depth-tested editor overlay requires the current bound scene depth");
  if(!saved.viewport.Width||!saved.viewport.Height||uint64_t(saved.viewport.X)+saved.viewport.Width>width||uint64_t(saved.viewport.Y)+saved.viewport.Height>height)fail(MDA9_UNSUPPORTED,"editor overlay current viewport is outside the target");
  const auto maxVertices=static_cast<size_t>(std::min<uint64_t>(393216,uint64_t(saved.maxPrimitiveCount)*3));if(maxVertices<3)fail(MDA9_UNSUPPORTED,"editor overlay device has no triangle capacity");
  check(d->CreateTexture(width,height,1,D3DUSAGE_RENDERTARGET,D3DFMT_A8R8G8B8,D3DPOOL_DEFAULT,texture.put(),nullptr),"create private editor overlay texture");check(texture->GetSurfaceLevel(0,surface.put()),"get private editor overlay surface");
  uint32_t draws=0;withOverlayState(saved,[&]{
   const D3DVIEWPORT9 full{0,0,width,height,0,1};saved.bindColor(surface,full);editorOverlayState(d,false,false);
   // TARGET only: neither the original depth nor stencil may be modified.
   check(d->Clear(0,nullptr,D3DCLEAR_TARGET,0,1,0),"clear private editor overlay color");
   check(d->SetViewport(&saved.viewport),"restore borrowed editor overlay draw viewport");
   // Preserve row order while batching contiguous depth policies. A character
   // skeleton and the default world guides normally need only two draws.
   std::vector<OverlayVertex> batch;batch.reserve(4096);bool depthEnabled=false;
   const auto flush=[&]{if(batch.empty())return;check(d->SetRenderState(D3DRS_ZENABLE,depthEnabled?TRUE:FALSE),"editor overlay batch depth policy");check(d->DrawPrimitiveUP(D3DPT_TRIANGLELIST,static_cast<UINT>(batch.size()/3),batch.data(),sizeof(OverlayVertex)),"raw editor overlay stroke batch");++draws;batch.clear();};
   for(const auto& row:rows){
    auto vertices=overlayVertices(row,saved.viewport);if(vertices.empty())continue;const bool nextDepth=(row.flags&MDA9_OVERLAY_DEPTH)!=0;
    if(depthEnabled!=nextDepth)flush();depthEnabled=nextDepth;
    for(size_t offset=0;offset<vertices.size();){const auto count=std::min(vertices.size()-offset,maxVertices-batch.size());batch.insert(batch.end(),vertices.begin()+offset,vertices.begin()+offset+count);offset+=count;if(batch.size()==maxVertices)flush();}
   }
   flush();
  });return draws;
 }
 uint32_t composite(IDirect3DDevice9* d,IDirect3DSurface9* target,uint32_t width,uint32_t height){
  if(!texture.p)return 0;
  OverlayDeviceState saved(d);
  withOverlayState(saved,[&]{
   const D3DVIEWPORT9 vp{0,0,width,height,0,1};saved.bindColor(target,vp);editorOverlayState(d,true,true);check(d->SetTexture(0,texture),"sample private editor overlay texture");
   check(d->BeginScene(),"raw editor overlay composite BeginScene");
   try{
    const float l=-.5f,t=-.5f,r=float(width)-.5f,b=float(height)-.5f;
    const std::array<BackgroundVertex,6> vertices{{{l,t,0,1,0xffffffff,0,0},{r,t,0,1,0xffffffff,1,0},{r,b,0,1,0xffffffff,1,1},{l,t,0,1,0xffffffff,0,0},{r,b,0,1,0xffffffff,1,1},{l,b,0,1,0xffffffff,0,1}}};
    check(d->DrawPrimitiveUP(D3DPT_TRIANGLELIST,2,vertices.data(),sizeof(BackgroundVertex)),"raw editor overlay composite draw");
   }catch(...){const auto original=std::current_exception();const HRESULT end=d->EndScene();check(end,"raw editor overlay failed composite EndScene");std::rethrow_exception(original);}
   check(d->EndScene(),"raw editor overlay composite EndScene");
  });return 1;
 }
};
struct FrameGpu {
 Com<IDirect3DVertexBuffer9> vertices,normals,additionalUvs,edges,fixedVertices,mediaVertices;Com<IDirect3DIndexBuffer9> indices;Com<IDirect3DVertexDeclaration9> declaration;
 DWORD fixedFvf=0;UINT fixedStride=0;
 std::vector<IDirect3DTexture9*> textures,mediaTextures;
 ~FrameGpu(){for(auto t:textures)if(t)t->Release();for(auto t:mediaTextures)if(t)t->Release();}
 void uploadMedia(IDirect3DDevice9* d,const std::vector<packet::MediaLayer>& layers){
  if(layers.empty())return;
  std::vector<BackgroundVertex> quads;quads.reserve(layers.size()*6);mediaTextures.reserve(layers.size());
  for(const auto& layer:layers){
   // The pinned MIT media_load/bg_overlay path resizes the actual decoded
   // image to 1024 squared; neither viewport size nor a new shader is used.
   std::vector<uint8_t> bgra(layer.rgba.size());for(size_t i=0;i<bgra.size();i+=4){bgra[i]=layer.rgba[i+2];bgra[i+1]=layer.rgba[i+1];bgra[i+2]=layer.rgba[i];bgra[i+3]=layer.rgba[i+3];}
   Com<IDirect3DTexture9> texture;
   if(layer.kind==1){
    const RECT source{0,0,static_cast<LONG>(layer.width),static_cast<LONG>(layer.height)};
    auto load=[&](UINT extent,UINT levels){
     HRESULT hr=d->CreateTexture(extent,extent,levels,0,D3DFMT_A8R8G8B8,D3DPOOL_MANAGED,texture.put(),nullptr);if(FAILED(hr))return hr;
     Com<IDirect3DSurface9> surface;hr=texture->GetSurfaceLevel(0,surface.put());if(FAILED(hr))return hr;
     // D3DX_DEFAULT = TRIANGLE|DITHER for source filtering; BOX for mips.
     hr=D3DXLoadSurfaceFromMemory(surface,nullptr,nullptr,bgra.data(),D3DFMT_A8R8G8B8,layer.width*4,nullptr,&source,0x80004,0);
     if(SUCCEEDED(hr)&&levels==0)hr=D3DXFilterTexture(texture,nullptr,0,3);return hr;
    };
    HRESULT hr=load(1024,0);if(FAILED(hr))hr=load(512,1);check(hr,"original background picture upload/filter");
   }else{
    // Original AVI: source-sized X8 plain surface -> 1024 X8 RT; LINEAR,
    // retry NONE. Keep this operation outside BeginScene, as in the source.
    Com<IDirect3DSurface9> source,target;check(d->CreateOffscreenPlainSurface(layer.width,layer.height,D3DFMT_X8R8G8B8,D3DPOOL_DEFAULT,source.put(),nullptr),"original AVI source surface");
    D3DLOCKED_RECT locked{};check(source->LockRect(&locked,nullptr,0),"original AVI source lock");for(uint32_t y=0;y<layer.height;++y)std::memcpy(static_cast<uint8_t*>(locked.pBits)+size_t(y)*locked.Pitch,bgra.data()+size_t(y)*layer.width*4,size_t(layer.width)*4);check(source->UnlockRect(),"original AVI source unlock");
    check(d->CreateTexture(1024,1024,1,D3DUSAGE_RENDERTARGET,D3DFMT_X8R8G8B8,D3DPOOL_DEFAULT,texture.put(),nullptr),"original AVI background texture");check(texture->GetSurfaceLevel(0,target.put()),"original AVI target surface");
    HRESULT hr=d->StretchRect(source,nullptr,target,nullptr,D3DTEXF_LINEAR);if(FAILED(hr))hr=d->StretchRect(source,nullptr,target,nullptr,D3DTEXF_NONE);check(hr,"original AVI StretchRect");
   }
   mediaTextures.push_back(texture.p);texture.p=nullptr;
   // Preserve the original six-vertex LIST order, white diffuse and UVs.
   const auto l=layer.left,t=layer.top,r=layer.right,b=layer.bottom;
   for(const auto& vertex:std::array<BackgroundVertex,6>{{{r,t,0,1,0xffffffff,1,0},{r,b,0,1,0xffffffff,1,1},{l,t,0,1,0xffffffff,0,0},{l,t,0,1,0xffffffff,0,0},{r,b,0,1,0xffffffff,1,1},{l,b,0,1,0xffffffff,0,1}}})quads.push_back(vertex);
  }
  check(d->CreateVertexBuffer(static_cast<UINT>(quads.size()*sizeof(BackgroundVertex)),D3DUSAGE_WRITEONLY,D3DFVF_XYZRHW|D3DFVF_DIFFUSE|D3DFVF_TEX1,D3DPOOL_MANAGED,mediaVertices.put(),nullptr),"original background vertex buffer");
  void* bytes=nullptr;check(mediaVertices->Lock(0,0,&bytes,0),"original background vertex lock");std::memcpy(bytes,quads.data(),quads.size()*sizeof(BackgroundVertex));check(mediaVertices->Unlock(),"original background vertex unlock");
 }
 void upload(IDirect3DDevice9* d,const packet::Frame& f,const visual::Inputs& v){
  check(d->CreateVertexBuffer(static_cast<UINT>(std::max(size_t(1),f.vertices.size())*sizeof(packet::Vertex)),D3DUSAGE_WRITEONLY,D3DFVF_XYZ|D3DFVF_NORMAL|D3DFVF_TEX1,D3DPOOL_MANAGED,vertices.put(),nullptr),"CreateVertexBuffer");
  void* p=nullptr;check(vertices->Lock(0,0,&p,0),"vertex lock");if(!f.vertices.empty())std::memcpy(p,f.vertices.data(),f.vertices.size()*sizeof(packet::Vertex));check(vertices->Unlock(),"vertex unlock");
  check(d->CreateIndexBuffer(static_cast<UINT>(std::max(size_t(1),f.indices.size())*4),D3DUSAGE_WRITEONLY,D3DFMT_INDEX32,D3DPOOL_MANAGED,indices.put(),nullptr),"CreateIndexBuffer");check(indices->Lock(0,0,&p,0),"index lock");if(!f.indices.empty())std::memcpy(p,f.indices.data(),f.indices.size()*4);check(indices->Unlock(),"index unlock");
  check(d->CreateVertexBuffer(static_cast<UINT>(std::max(size_t(1),v.sphereNormals.size())*12),D3DUSAGE_WRITEONLY,0,D3DPOOL_MANAGED,normals.put(),nullptr),"Create sphere-normal buffer");check(normals->Lock(0,0,&p,0),"normal lock");if(!v.sphereNormals.empty())std::memcpy(p,v.sphereNormals.data(),v.sphereNormals.size()*12);check(normals->Unlock(),"normal unlock");
  std::vector<D3DVERTEXELEMENT9> elements={{0,0,D3DDECLTYPE_FLOAT3,D3DDECLMETHOD_DEFAULT,D3DDECLUSAGE_POSITION,0},{0,12,D3DDECLTYPE_FLOAT3,D3DDECLMETHOD_DEFAULT,D3DDECLUSAGE_NORMAL,0},{0,24,D3DDECLTYPE_FLOAT2,D3DDECLMETHOD_DEFAULT,D3DDECLUSAGE_TEXCOORD,0},{1,0,D3DDECLTYPE_FLOAT3,D3DDECLMETHOD_DEFAULT,D3DDECLUSAGE_TEXCOORD,5}};
  if(f.additionalUvChannelCount){
   check(d->CreateVertexBuffer(static_cast<UINT>(std::max(size_t(1),f.additionalUvs.size())*sizeof(packet::AdditionalUvVertex)),D3DUSAGE_WRITEONLY,0,D3DPOOL_MANAGED,additionalUvs.put(),nullptr),"Create additional UV buffer");
   check(additionalUvs->Lock(0,0,&p,0),"additional UV lock");if(!f.additionalUvs.empty())std::memcpy(p,f.additionalUvs.data(),f.additionalUvs.size()*sizeof(packet::AdditionalUvVertex));check(additionalUvs->Unlock(),"additional UV unlock");
   for(WORD channel=0;channel<4;++channel)elements.push_back({2,static_cast<WORD>(channel*16),D3DDECLTYPE_FLOAT4,D3DDECLMETHOD_DEFAULT,D3DDECLUSAGE_TEXCOORD,static_cast<BYTE>(channel+1)});
  }
  const D3DVERTEXELEMENT9 end=D3DDECL_END();elements.push_back(end);check(d->CreateVertexDeclaration(elements.data(),declaration.put()),"CreateVertexDeclaration");
  if((f.flags&packet::HasClassicPasses)&&!(f.classic.flags&packet::EffectFrame)){
   // FFP consumes the original contiguous vertex format, including raw UV1.
   // The shader path's separate additional-UV stream is not a fixed FVF input.
   fixedStride=sizeof(packet::Vertex)+16*f.additionalUvChannelCount;
   fixedFvf=D3DFVF_XYZ|D3DFVF_NORMAL|((1+f.additionalUvChannelCount)<<D3DFVF_TEXCOUNT_SHIFT);
   for(DWORD channel=1;channel<=f.additionalUvChannelCount;++channel)fixedFvf|=D3DFVF_TEXCOORDSIZE4(channel);
   const size_t bytes=std::max(size_t(1),f.vertices.size())*fixedStride;if(bytes>UINT_MAX)fail(MDA9_UNSUPPORTED,"original fixed vertex buffer exceeds D3D9 limits");
   check(d->CreateVertexBuffer(static_cast<UINT>(bytes),D3DUSAGE_WRITEONLY,fixedFvf,D3DPOOL_MANAGED,fixedVertices.put(),nullptr),"Create original fixed vertex buffer");check(fixedVertices->Lock(0,0,&p,0),"original fixed vertex lock");
   for(size_t i=0;i<f.vertices.size();++i){auto* dst=static_cast<uint8_t*>(p)+i*fixedStride;std::memcpy(dst,&f.vertices[i],sizeof(packet::Vertex));if(f.additionalUvChannelCount)std::memcpy(dst+sizeof(packet::Vertex),&f.additionalUvs[i],16*f.additionalUvChannelCount);}
   check(fixedVertices->Unlock(),"original fixed vertex unlock");
  }
  if(!f.edges.empty()){
   check(d->CreateVertexBuffer(static_cast<UINT>(f.edges.size()*sizeof(packet::EdgeVertex)),D3DUSAGE_WRITEONLY,D3DFVF_XYZ|D3DFVF_DIFFUSE,D3DPOOL_MANAGED,edges.put(),nullptr),"Create original edge buffer");
   check(edges->Lock(0,0,&p,0),"edge lock");std::memcpy(p,f.edges.data(),f.edges.size()*sizeof(packet::EdgeVertex));check(edges->Unlock(),"edge unlock");
  }
  textures.reserve(f.textures.size());for(auto& t:f.textures){IDirect3DTexture9* tex=nullptr;check(d->CreateTexture(t.width,t.height,1,0,D3DFMT_A8R8G8B8,D3DPOOL_MANAGED,&tex,nullptr),"CreateTexture");textures.push_back(tex);D3DLOCKED_RECT r{};check(tex->LockRect(0,&r,nullptr,0),"texture lock");for(uint32_t y=0;y<t.height;++y){auto dst=static_cast<uint8_t*>(r.pBits)+size_t(y)*r.Pitch;auto src=t.rgba.data()+size_t(y)*t.width*4;for(uint32_t x=0;x<t.width;++x){dst[4*x]=src[4*x+2];dst[4*x+1]=src[4*x+1];dst[4*x+2]=src[4*x];dst[4*x+3]=src[4*x+3];}}check(tex->UnlockRect(0),"texture unlock");}
  uploadMedia(d,f.media);
 }
};
struct Diagnostics { uint32_t dispatches=0,passDraws=0,passFailures=0,fallbacks=0,noUi=0;char message[768]{}; };
struct Session {
 uint64_t id;DWORD owner=GetCurrentThreadId();HWND window=nullptr;
 enum class Mode { Capture, Preview };
 const Mode mode;const HWND borrowedTarget;const DWORD targetThread,targetProcess;
 Com<IDirect3D9> d3d;Com<IDirect3DDevice9> device;Com<ID3DXEffect> effect;
 Com<IDirect3DSurface9> target,depth,readback,shadowTarget,shadowDepth;Com<IDirect3DTexture9> shadowTexture,screenCaptureTexture;
 uint32_t shadowWidth=0,shadowHeight=0;
 enum class Phase { Ready, NeedsPreparation, ResetPrepared, TargetsPending };
 D3DPRESENT_PARAMETERS parameters{};bool runtime=false,bound=false,started=false,softwareVp=false,poisoned=false;
 Phase phase=Phase::Ready;
 uint32_t width=0,height=0;uint64_t captures=0,presents=0,generation=0;
 packet::Frame scene{};Metadata metadata;std::vector<D3DMATERIAL9> materials;std::unique_ptr<FrameGpu> gpu;Diagnostics diagnostic;
 std::unique_ptr<ObjectFxState> objectFx;
 int currentObject=0,currentMaterial=0,currentTechnic=1,repeatCount=0;
 explicit Session(uint64_t value,Mode m=Mode::Capture,HWND target=nullptr,DWORD thread=0,DWORD process=0):id(value),mode(m),borrowedTarget(target),targetThread(thread),targetProcess(process){}
 ~Session(){cleanup();}
 void cleanup()noexcept;
 void initialize(const Mda9CreateV1&);
 void makeTargets(uint32_t,uint32_t);
 void prepareShadowTarget(const packet::Frame&);
 void releaseShadowTarget()noexcept;
 void captureAccessoryScreen();
 void resize(uint32_t,uint32_t);
 uint32_t render(FrozenDraw&,uint32_t);
 uint32_t draw(const visual::Inputs&,uint32_t,const std::vector<Mda9EditorOverlayPrimitiveV1>&);
 void present();
 void bridgeCheck();
 void checkSceneFxState();
 bool sceneEffects()const{return objectFx&&objectFx->sceneEffects;}
 bool emmConfigured()const{return objectFx&&objectFx->emm;}
 uint32_t configuredScope()const{return emmConfigured()?MDA9_CONFIGURED_EMM:objectFx?(sceneEffects()?MDA9_CONFIGURED_SCENE_FX:MDA9_CONFIGURED_OBJECT_FX):MDA9_CAPTURE_ISOLATED_SURFACE;}
 uint32_t resultScope()const{return emmConfigured()?MDA9_RESULT_CONFIGURED_EMM:objectFx?(sceneEffects()?MDA9_RESULT_CONFIGURED_SCENE_FX:MDA9_RESULT_CONFIGURED_OBJECT_FX):MDA9_RESULT_ISOLATED_HOST_METADATA;}
 uint64_t effectFeatures()const{return objectFx?uint64_t(MDA9_FEATURE_OBJECT_FX)|(sceneEffects()?uint64_t(MDA9_FEATURE_SCENE_EFFECTS):0)|(emmConfigured()?uint64_t(MDA9_FEATURE_EMM_CONFIGURATION):0):0;}
 void applyObjectFx();
 void applyEmm();
 void checkObjectFxBinding(uint64_t,int32_t);
 bool valid(int i)const{return i>=0&&size_t(i)<scene.objects.size();}
 int typedIndex(uint32_t kind,int i)const{const auto& list=kind==packet::Model?metadata.models:metadata.accessories;return i>=0&&size_t(i)<list.size()?static_cast<int>(list[i]):-1;}
 const packet::Object* object(uint32_t kind,int i)const{const int n=typedIndex(kind,i);return valid(n)?&scene.objects[n]:nullptr;}
 D3DMATERIAL9 material(int i,int j)const{if(!valid(i)||j<0||uint32_t(j)>=scene.objects[i].materialCount)return {};return materials[scene.objects[i].materialStart+j];}
};
struct RecoverOnFailure {Session& s;int before=std::uncaught_exceptions();~RecoverOnFailure(){if(std::uncaught_exceptions()>before&&s.phase==Session::Phase::Ready)s.phase=Session::Phase::NeedsPreparation;}};
DWORD validateTarget(HWND target,DWORD expectedThread=0,DWORD expectedProcess=0){
 DWORD process=0;const DWORD thread=target?GetWindowThreadProcessId(target,&process):0;
 RECT client{};const HWND parent=target?GetParent(target):nullptr;
 if(!target||!IsWindow(target)||!thread||process!=GetCurrentProcessId()||
    (expectedThread&&thread!=expectedThread)||(expectedProcess&&process!=expectedProcess)||
    !(GetWindowLongPtrW(target,GWL_STYLE)&WS_CHILD)||!parent||!IsWindow(parent)||
    !GetClientRect(target,&client)||client.right<=client.left||client.bottom<=client.top)
  fail(MDA9_INVALID_ARGUMENT,"preview target must be a live same-process child HWND with a positive client extent");
 return thread;
}
std::unique_ptr<Session> active;Session* callbacks=nullptr;uint64_t nextId=1;
std::atomic_flag apiBusy=ATOMIC_FLAG_INIT;
struct Guard {bool held=!apiBusy.test_and_set(std::memory_order_acquire);~Guard(){if(held)apiBusy.clear(std::memory_order_release);}};
const wchar_t* typedPath(uint32_t kind,int i){if(!callbacks)return nullptr;const int n=callbacks->typedIndex(kind,i);return callbacks->valid(n)?callbacks->metadata.paths[n].c_str():nullptr;}
const wchar_t* __cdecl modelPath(int i){return typedPath(packet::Model,i);}
const wchar_t* __cdecl accessoryPath(int i){return typedPath(packet::Accessory,i);}
int32_t __cdecl geometry(void* context,uint32_t kind,uint64_t id,MdaMmeGeometryV1* out)noexcept{
 auto* s=static_cast<Session*>(context);if(!s||(kind!=MDA_MME_MODEL&&kind!=MDA_MME_ACCESSORY)||!out||out->size<sizeof(*out)||out->version!=MDA_MME_BRIDGE_VERSION||!s->gpu||!s->gpu->vertices.p)return -1;
 const auto found=s->metadata.byId.find(id);if(found==s->metadata.byId.end()||s->scene.objects[found->second].kind!=kind)return -1;
 out->identity=s->generation;out->borrowed_iunknown=s->gpu->vertices.p;return 0;
}
int32_t __cdecl parent(void* context,uint64_t id,MdaMmeParentV1* out)noexcept{
 auto* s=static_cast<Session*>(context);if(!s||!out||out->size<sizeof(*out)||out->version!=MDA_MME_BRIDGE_VERSION)return -1;
 const auto found=s->metadata.byId.find(id);if(found==s->metadata.byId.end())return -1;const auto& o=s->scene.objects[found->second];if(o.kind!=packet::Accessory)return -1;
 if(o.attached){const auto p=s->metadata.byId.find(o.parentModelId);if(p==s->metadata.byId.end())return -1;const auto& model=s->scene.objects[p->second];if(model.kind!=packet::Model||o.parentBoneIndex<0||size_t(o.parentBoneIndex)>=model.bones.size())return -1;}
 out->attached=o.attached;out->model_id=o.parentModelId;out->bone_index=o.parentBoneIndex;return 0;
}
int32_t __cdecl applyObjectFx(void* context) noexcept {
 auto* s=static_cast<Session*>(context);if(!s||!s->objectFx)return MDA9_INTERNAL_ERROR;
 auto& fx=*s->objectFx;fx.failureStatus=MDA9_OK;fx.error.clear();
 try{s->applyObjectFx();return MDA9_OK;}
 catch(const Failure& failure){fx.failureStatus=failure.status;try{fx.error=failure.what();}catch(...){}return failure.status;}
 catch(const std::bad_alloc&){fx.failureStatus=MDA9_OUT_OF_MEMORY;return MDA9_OUT_OF_MEMORY;}
 catch(const std::exception& failure){fx.failureStatus=MDA9_INTERNAL_ERROR;try{fx.error=failure.what();}catch(...){}return MDA9_INTERNAL_ERROR;}
 catch(...){fx.failureStatus=MDA9_INTERNAL_ERROR;return MDA9_INTERNAL_ERROR;}
}
int32_t __cdecl resolveEffectResource(void* context,const char* base,const char* request,char* output,uint32_t capacity,uint32_t* required)noexcept {
 if(required)*required=0;
 auto* state=static_cast<ObjectFxState*>(context);
 if(!state||!state->resources||!base||!request||!required||(!output&&capacity))return MDA_MME_INVALID;
 try {
  auto fromAnsi=[](const char* value){
   const size_t length=strnlen(value,32768);if(length==0||length>=32768)throw std::invalid_argument("invalid resource path span");
   const int size=MultiByteToWideChar(GetACP(),MB_ERR_INVALID_CHARS,value,static_cast<int>(length),nullptr,0);
   if(!size)throw std::invalid_argument("invalid resource ANSI bytes");std::wstring path(size,L'\0');
   if(!MultiByteToWideChar(GetACP(),MB_ERR_INVALID_CHARS,value,static_cast<int>(length),path.data(),size)||exactAnsi(path)!=std::string(value,length))throw std::invalid_argument("resource ANSI path roundtrip changed");return path;
  };
  const auto path=exactAnsi(state->resources->Resolve(fromAnsi(base),fromAnsi(request)));
  if(path.size()>=MAX_PATH)return MDA_MME_INVALID;
  *required=static_cast<uint32_t>(path.size()+1);
  if(!output)return MDA_MME_OK;
  if(capacity<*required)return MDA_MME_INVALID;
  std::memcpy(output,path.c_str(),*required);return MDA_MME_OK;
 }catch(...){return MDA_MME_MISSING;}
}
const char* techniques[]={"ColorRenderTec","ZValuePlotTec","BufferShadowTec","BShadowTextureTec","BShadowSphiaTec","BShadowSphiaTextureTec","BShadowTexCd2Tec","BShadowTextureTexCd2Tec","DiffuseBufferShadowTec","DiffuseBSTextureTec","DiffuseBSSphiaTec","DiffuseBSSphiaTexTec"};

void Session::cleanup()noexcept {
 if(started&&device.p&&phase!=Phase::ResetPrepared)MmeHostEndScene(device);
 started=false;
 if(objectFx)objectFx->compiled.clear();
 // The pinned DestroyDevice cleanup releases host COM owners/caches without
 // calling device state methods, including after a failed Reset. Do not run
 // EndScene/Lost/Reset callbacks in ResetPrepared. Host cleanup can still read
 // the borrowed standard effect and geometry before their final Release.
 if(runtime&&device.p){MmeHostDestroyDevice(device);bound=false;}
 if(runtime){MmeHostShutdownRuntime();runtime=false;}
 if(objectFx){mme::g_autoReload=objectFx->previousAutoReload;objectFx.reset();mme::MdaObjectFxAllowedPaths.clear();}
 if(callbacks==this)callbacks=nullptr;
 gpu.reset();effect.reset();releaseShadowTarget();screenCaptureTexture.reset();target.reset();depth.reset();readback.reset();device.reset();d3d.reset();
 if(window){DestroyWindow(window);window=nullptr;}
}
void Session::initialize(const Mda9CreateV1& c){
 window=mda9::detail::createOwnerWindow();if(!window)fail(MDA9_DEVICE_FAILURE,"create owner window failed");
 d3d.p=Direct3DCreate9(D3D_SDK_VERSION);if(!d3d.p)fail(MDA9_DEVICE_FAILURE,"Direct3DCreate9 failed");if(c.adapterOrdinal>=d3d->GetAdapterCount())fail(MDA9_INVALID_ARGUMENT,"adapter ordinal is out of range");
 parameters.Windowed=TRUE;parameters.SwapEffect=D3DSWAPEFFECT_DISCARD;parameters.hDeviceWindow=window;parameters.BackBufferWidth=c.width;parameters.BackBufferHeight=c.height;parameters.BackBufferFormat=D3DFMT_UNKNOWN;parameters.PresentationInterval=D3DPRESENT_INTERVAL_IMMEDIATE;
 HRESULT hr=d3d->CreateDevice(c.adapterOrdinal,D3DDEVTYPE_HAL,window,D3DCREATE_HARDWARE_VERTEXPROCESSING,&parameters,device.put());if(FAILED(hr)){softwareVp=true;check(d3d->CreateDevice(c.adapterOrdinal,D3DDEVTYPE_HAL,window,D3DCREATE_SOFTWARE_VERTEXPROCESSING,&parameters,device.put()),"CreateDevice HAL");}
 Com<ID3DXBuffer> errors;hr=D3DXCreateEffect(device,kSurfaceSource,static_cast<UINT>(sizeof(kSurfaceSource)-1),nullptr,nullptr,0,nullptr,effect.put(),errors.put());if(FAILED(hr))fail(MDA9_DEVICE_FAILURE,"D3DXCreateEffect ordinary surface compilation failed");for(auto name:techniques)if(!effect->GetTechniqueByName(name))fail(MDA9_DEVICE_FAILURE,"ordinary surface technique missing");
 callbacks=this;MmeHostInitializeRuntime(GetModuleHandleW(nullptr));runtime=true;MmeHostSetMainWindow(window);MmeHostSetDrawnWindow(window);MmeHostSetPathProviders(modelPath,accessoryPath);MmeHostSetStandardEffect(effect.p);mme::MmeEmmSetDefaultEffect("");
 MdaMmeCallbacksV1 cb{sizeof cb,MDA_MME_BRIDGE_VERSION,this,geometry,parent};if(MdaMmeBindV1(&cb)!=MDA_MME_OK)fail(MDA9_INTERNAL_ERROR,"opaque64 host binding failed");bound=true;
 makeTargets(c.width,c.height);width=c.width;height=c.height;
}
void Session::makeTargets(uint32_t w,uint32_t h){
 D3DFORMAT format=D3DFMT_A8R8G8B8;
 if(mode==Mode::Preview){
  Com<IDirect3DSurface9> back;D3DSURFACE_DESC desc{};check(device->GetBackBuffer(0,0,D3DBACKBUFFER_TYPE_MONO,back.put()),"GetBackBuffer format");check(back->GetDesc(&desc),"backbuffer description");
  if(desc.Format!=D3DFMT_X8R8G8B8&&desc.Format!=D3DFMT_A8R8G8B8)fail(MDA9_UNSUPPORTED,"preview backbuffer color format is unsupported");
  if(desc.Width!=w||desc.Height!=h||desc.Pool!=D3DPOOL_DEFAULT||desc.MultiSampleType!=D3DMULTISAMPLE_NONE)fail(MDA9_DEVICE_FAILURE,"preview backbuffer extent or storage differs from the requested target");
  format=desc.Format;
 }
 check(device->CreateRenderTarget(w,h,format,D3DMULTISAMPLE_NONE,0,FALSE,target.put(),nullptr),"CreateRenderTarget");check(device->CreateDepthStencilSurface(w,h,D3DFMT_D24S8,D3DMULTISAMPLE_NONE,0,TRUE,depth.put(),nullptr),"CreateDepthStencilSurface");
 if(mode==Mode::Capture)check(device->CreateOffscreenPlainSurface(w,h,D3DFMT_A8R8G8B8,D3DPOOL_SYSTEMMEM,readback.put(),nullptr),"CreateOffscreenPlainSurface");
}
void Session::resize(uint32_t w,uint32_t h){
 if(phase==Phase::Ready&&w==width&&h==height&&target.p&&depth.p&&(mode==Mode::Preview||readback.p)&&device->TestCooperativeLevel()==D3D_OK)return;
 // A successful Reset followed by partial target allocation can retry those
 // allocations directly. A changed size first prepares a fresh real Reset.
 if(phase==Phase::TargetsPending&&(w!=width||h!=height||device->TestCooperativeLevel()!=D3D_OK))phase=Phase::NeedsPreparation;
 if(phase==Phase::Ready||phase==Phase::NeedsPreparation){
  phase=Phase::NeedsPreparation;
  MmeHostOnLostDevice(device);check(effect->OnLostDevice(),"standard effect OnLostDevice");
  // Detach all caller-held default-pool targets before the first Reset attempt.
  // The host separately detaches its geometry AddRef on Lost.
  check(device->SetDepthStencilSurface(nullptr),"detach depth");
  {Com<IDirect3DSurface9> back;check(device->GetBackBuffer(0,0,D3DBACKBUFFER_TYPE_MONO,back.put()),"GetBackBuffer before Reset");check(device->SetRenderTarget(0,back),"detach capture target");}
  for(unsigned i=0;i<16;++i)device->SetTexture(i,nullptr);
  device->SetStreamSource(0,nullptr,0,0);device->SetStreamSource(1,nullptr,0,0);device->SetStreamSource(2,nullptr,0,0);device->SetIndices(nullptr);device->SetVertexDeclaration(nullptr);
  gpu.reset();releaseShadowTarget();screenCaptureTexture.reset();target.reset();depth.reset();readback.reset();
  phase=Phase::ResetPrepared;
 }
 if(phase==Phase::ResetPrepared){
  // After a failed Reset, D3D9 permits only TestCooperativeLevel, Reset and
  // Release. This branch deliberately performs no Lost/effect/state/target calls.
  device->TestCooperativeLevel();
  parameters.BackBufferWidth=w;parameters.BackBufferHeight=h;
  check(device->Reset(&parameters),"Reset");
  width=w;height=h;
  // Reset succeeded: device calls are legal again. If effect recovery fails,
  // the next retry must run a fresh Lost preparation before another Reset.
  phase=Phase::NeedsPreparation;
  check(effect->OnResetDevice(),"standard effect OnResetDevice");MmeHostOnResetDevice(device);
  phase=Phase::TargetsPending;
 }
 if(phase==Phase::TargetsPending){
  target.reset();depth.reset();readback.reset();
  makeTargets(width,height);phase=Phase::Ready;
 }
}
void Session::releaseShadowTarget()noexcept {
 shadowTarget.reset();shadowDepth.reset();shadowTexture.reset();shadowWidth=shadowHeight=0;
}
void Session::prepareShadowTarget(const packet::Frame& frame){
 if(!(frame.flags&packet::HasClassicPasses)||!(frame.classic.flags&packet::SelfShadow)){releaseShadowTarget();return;}
 const auto& c=frame.classic;if(shadowTexture.p&&shadowTarget.p&&shadowDepth.p&&shadowWidth==c.width&&shadowHeight==c.height)return;
 // All owners are DEFAULT-pool and join the existing Lost/Reset lifecycle.
 // Allocate atomically; failure cannot leave a partly usable shadow map.
 Com<IDirect3DTexture9> texture;Com<IDirect3DSurface9> surface,z;
 check(device->CreateTexture(c.width,c.height,1,D3DUSAGE_RENDERTARGET,D3DFMT_R32F,D3DPOOL_DEFAULT,texture.put(),nullptr),"Create original R32F shadow map");
 check(texture->GetSurfaceLevel(0,surface.put()),"original shadow surface");
 check(device->CreateDepthStencilSurface(c.width,c.height,D3DFMT_D24X8,D3DMULTISAMPLE_NONE,0,FALSE,z.put(),nullptr),"Create original D24X8 shadow depth");
 releaseShadowTarget();shadowTexture.p=texture.p;texture.p=nullptr;shadowTarget.p=surface.p;surface.p=nullptr;shadowDepth.p=z.p;z.p=nullptr;shadowWidth=c.width;shadowHeight=c.height;
}
void Session::captureAccessoryScreen(){
 // MIT pin bfb6297 frame_scene.cpp:246..303: modes0/3 leave the existing
 // capture alone. Materials have already consumed the preceding frame.
 if(scene.mediaBlockVersion<2||(scene.screenCaptureMode!=1&&scene.screenCaptureMode!=2))return;
 if(!screenCaptureTexture.p){
  HRESULT hr=device->CreateTexture(1024,1024,1,D3DUSAGE_RENDERTARGET,D3DFMT_X8R8G8B8,D3DPOOL_DEFAULT,screenCaptureTexture.put(),nullptr);
  if(hr==D3DERR_OUTOFVIDEOMEMORY||hr==E_OUTOFMEMORY)hr=device->CreateTexture(512,512,1,D3DUSAGE_RENDERTARGET,D3DFMT_X8R8G8B8,D3DPOOL_DEFAULT,screenCaptureTexture.put(),nullptr);
  check(hr,"original screen capture texture");
 }
 RECT source{0,0,static_cast<LONG>(width),static_cast<LONG>(height)};
 if(scene.screenCaptureMode==2){
  const double w=source.right-source.left,h=source.bottom-source.top,targetWidth=h*4.0/3.0;
  if(targetWidth<w){const double center=double(source.left+source.right)*.5;source.left=static_cast<LONG>(center-targetWidth*.5);source.right=static_cast<LONG>(center+targetWidth*.5);}
  else {const double targetHeight=w*3.0/4.0,center=double(source.top+source.bottom)*.5;source.top=static_cast<LONG>(center-targetHeight*.5);source.bottom=static_cast<LONG>(center+targetHeight*.5);}
 }
 Com<IDirect3DSurface9> surface;check(screenCaptureTexture->GetSurfaceLevel(0,surface.put()),"original screen capture surface");
 // No readback or CPU resize. Detach any material sampler before writing
 // the same session-owned texture; the scene is complete at this point.
 for(DWORD stage=0;stage<16;++stage){Com<IDirect3DBaseTexture9> bound;check(device->GetTexture(stage,bound.put()),"screen capture sampler");if(bound.p==screenCaptureTexture.p)check(device->SetTexture(stage,nullptr),"detach screen capture sampler");}
 HRESULT hr=device->StretchRect(target,&source,surface,nullptr,D3DTEXF_LINEAR);
 if(FAILED(hr))hr=device->StretchRect(target,&source,surface,nullptr,D3DTEXF_NONE);
 check(hr,"original screen capture StretchRect");
}
uint32_t Session::render(FrozenDraw& frozen,uint32_t order){
 resize(frozen.frame.width,frozen.frame.height);prepareShadowTarget(frozen.frame);auto next=std::make_unique<FrameGpu>();next->upload(device,frozen.frame,frozen.appearance);
 auto previous=std::move(gpu);gpu=std::move(next);scene=std::move(frozen.frame);metadata=std::move(frozen.metadata);materials=std::move(frozen.materials);++generation;diagnostic={};
 try{return draw(frozen.appearance,order,frozen.editorOverlay);}catch(...){
  // A general SAS walk may have suspended/redirected state when it fails. Use
  // the same frame-tail cleanup, then require Destroy rather than publishing
  // partial output or retrying a one-way run-failed latch on another frame.
  if(sceneEffects())poisoned=true;
  if(started){started=false;try{MmeHostEndScene(device);}catch(...){poisoned=true;device->EndScene();}}
  throw;
 }
}
void Session::present(){
 Com<IDirect3DSurface9> back;D3DSURFACE_DESC src{},dst{};
 check(device->GetBackBuffer(0,0,D3DBACKBUFFER_TYPE_MONO,back.put()),"GetBackBuffer for Present");check(target->GetDesc(&src),"preview target description");check(back->GetDesc(&dst),"Present backbuffer description");
 if(src.Width!=width||src.Height!=height||dst.Width!=width||dst.Height!=height||src.Format!=dst.Format||
    src.Pool!=D3DPOOL_DEFAULT||dst.Pool!=D3DPOOL_DEFAULT||src.MultiSampleType!=D3DMULTISAMPLE_NONE||dst.MultiSampleType!=D3DMULTISAMPLE_NONE)
  fail(MDA9_DEVICE_FAILURE,"Present requires matching non-MSAA DEFAULT-pool target and backbuffer");
 check(device->StretchRect(target,nullptr,back,nullptr,D3DTEXF_NONE),"preview StretchRect");
 validateTarget(borrowedTarget,targetThread,targetProcess);
 const HRESULT hr=device->Present(nullptr,nullptr,borrowedTarget,nullptr);check(hr,"Present");
 if(hr!=D3D_OK){std::ostringstream message;message<<"Present did not return D3D_OK; HRESULT=0x"<<std::hex<<static_cast<unsigned long>(hr);throw Failure(MDA9_DEVICE_FAILURE,message.str());}
}
void Session::checkObjectFxBinding(uint64_t id,int32_t subset){
 const auto* requested=objectfx::Resolve(objectFx->rows,id,subset);if(!requested)return;
 auto* model=mme::MmeFindOrCreateModelEntry(id);
 mme::MaterialBinding* binding=nullptr;
 if(sceneEffects()){
  // This verifies the immutable root assignment, not the currently active
  // offscreen DefaultEffect. The ordinary lookup intentionally redirects
  // turn0 inside an offscreen turn, where hide/none/replacement is legitimate.
  auto* manager=mme::g_context?mme::g_context->ownerManager:nullptr;
  if(manager&&model){
   auto found=manager->bindings.find(mme::EffectOwnerManager::BindingKey(0,model,subset));
   if(found==manager->bindings.end()&&subset>=0)
    found=manager->bindings.find(mme::EffectOwnerManager::BindingKey(0,model,-1));
   if(found!=manager->bindings.end())binding=found->second;
  }
 }else if(model)binding=mme::MmeFindMaterialBinding(0,model,subset,true);
 if(!binding||!binding->effect||!binding->sas||!binding->owner||!binding->instance||binding->effectPath!=requested->path)
  fail(MDA9_DEVICE_FAILURE,"configured object FX did not produce the requested compiled binding");
 const auto rejection=sceneEffects()?mme::MdaSceneFxAdmission(binding->sas):mme::MdaObjectFxAdmission(binding->sas);
 if(!rejection.empty())throw Failure(sceneEffects()?MDA9_DEVICE_FAILURE:MDA9_UNSUPPORTED,rejection+"\n"+diagnosticUtf8(binding->owner->errorText)+diagnosticUtf8(binding->instance->errorText));
}
void Session::applyObjectFx(){
 auto& fx=*objectFx;
 // Initialize may refresh runtime settings. The immutable session never polls
 // live shader files or executes EMM sidecar discovery at its frame tail.
 mme::g_autoReload=0;
 if(fx.emm){applyEmm();return;}
 for(const auto& row:fx.rows)if(row.assignment==objectfx::File){
  // Compile even if this object is hidden (FRM1 intentionally has 0 materials
  // there). The actual visible binding later validates its real subset count.
  auto loaded=mme::MmeEngineLoadEffectFile(device,row.path,0);
  if(!loaded||!loaded->effect||!loaded->sas||!loaded->errorText.empty())
   throw Failure(loaded&&loaded->objectFxRejected?MDA9_UNSUPPORTED:MDA9_DEVICE_FAILURE,"object FX compilation/parse failed:\n"+(loaded?diagnosticUtf8(loaded->errorText):std::string("effect loader returned no result")));
  const auto rejection=fx.sceneEffects?mme::MdaSceneFxAdmission(loaded->sas):mme::MdaObjectFxAdmission(loaded->sas);
  if(!rejection.empty())throw Failure(fx.sceneEffects?MDA9_DEVICE_FAILURE:MDA9_UNSUPPORTED,rejection);
  fx.compiled[row.path]=std::move(loaded);
 }
 // A new VB/generation may have replaced every host object. Reapply the full
 // frozen map each frame, in whole-then-subset order independent of input order.
 for(const auto& object:scene.objects){
  if(!object.visible)continue;
  if(!mme::MmeAssignEffect(object.id,-1,""))fail(MDA9_DEVICE_FAILURE,"configured object FX could not reset a visible registered object");
 }
 mme::MmeEnginePruneExpiredInstances();
 for(int whole=1;whole>=0;--whole)for(const auto& row:fx.rows){
  if((row.subset== -1)!=bool(whole)||row.assignment==objectfx::Inherit)continue;
  const auto object=std::find_if(scene.objects.begin(),scene.objects.end(),[&](const auto& candidate){return candidate.id==row.object;});
  if(object==scene.objects.end()||!object->visible)continue;
  const std::string path=row.assignment==objectfx::File?row.path:std::string();
  if(row.assignment==objectfx::File){
   auto loaded=mme::MmeEngineLoadEffectFile(device,path,static_cast<int>(object->materialCount));
   if(!loaded||!loaded->effect||!loaded->sas||!loaded->errorText.empty())
    throw Failure(loaded&&loaded->objectFxRejected?MDA9_UNSUPPORTED:MDA9_DEVICE_FAILURE,"object FX material validation failed:\n"+(loaded?diagnosticUtf8(loaded->errorText):std::string("effect loader returned no result")));
  }
  if(!mme::MmeAssignEffect(row.object,row.subset,path))fail(MDA9_DEVICE_FAILURE,"configured object/subset FX assignment failed");
  if(row.assignment==objectfx::File)checkObjectFxBinding(row.object,row.subset);
 }
 for(const auto& object:scene.objects)if(object.visible)
  for(uint32_t subset=0;subset<object.materialCount;++subset)checkObjectFxBinding(object.id,static_cast<int32_t>(subset));
}
void Session::applyEmm(){
 auto& fx=*objectFx;
 // Compile all explicit/default/implicit FILE values, including overridden and
 // hidden roots, before the host's destructive mapped graph apply. Resources
 // still remain leased through host destruction if any later apply fails.
 for(const auto& path:fx.emmFiles){
  auto loaded=mme::MmeEngineLoadEffectFile(device,path,0);
  if(!loaded||!loaded->effect||!loaded->sas||!loaded->errorText.empty())
   throw Failure(MDA9_DEVICE_FAILURE,"EMM effect compilation/parse failed:\n"+(loaded?diagnosticUtf8(loaded->errorText):std::string("effect loader returned no result")));
  const auto rejection=mme::MdaSceneFxAdmission(loaded->sas);
  if(!rejection.empty())throw Failure(MDA9_DEVICE_FAILURE,rejection);
  fx.compiled[path]=std::move(loaded);
 }
 const auto applied=mme::MmeEmmApplyMapped(fx.emm->sections,fx.emm->objects,fx.emm->rootSeeds);
 if(!applied.succeeded()){
  std::ostringstream message;message<<"EMM mapped application failed";
  for(const auto& row:applied.diagnostics){message<<"\n["<<diagnosticUtf8(row.sectionName)<<"] key="<<row.key<<" object="<<row.objectId<<" subset="<<row.subsetIndex<<" phase="<<static_cast<int>(row.phase)<<" code="<<static_cast<int>(row.code)<<": "<<diagnosticUtf8(row.detail);if(message.tellp()>32768){message<<"\nFurther EMM diagnostics omitted";break;}}
  throw Failure(MDA9_DEVICE_FAILURE,message.str());
 }
 checkSceneFxState();
}
void Session::bridgeCheck(){MdaMmeErrorV1 e{sizeof e,MDA_MME_BRIDGE_VERSION,0,0,0,0,0};if(MdaMmeGetErrorV1(&e)!=MDA_MME_OK||e.status)fail(MDA9_DEVICE_FAILURE,"host geometry/effect callback failed");if(diagnostic.noUi)fail(MDA9_DEVICE_FAILURE,diagnostic.message[0]?diagnostic.message:"MME emitted a no-UI diagnostic");}
void Session::checkSceneFxState(){
 if(!sceneEffects())return;
 if(diagnostic.passFailures)fail(MDA9_DEVICE_FAILURE,"configured scene FX effect-pass draw failed");
 const auto* context=mme::g_context;
 if(!context||!context->ownerManager)fail(MDA9_DEVICE_FAILURE,"configured scene FX lost its host context");
 // DefaultEffect dependencies have independent assignment instances. Check the
 // existing manager's live bindings as well as explicit root FILE rows; their
 // SAS run-failed latches are authoritative even when a carrier emits no draw.
 for(const auto& entry:context->ownerManager->bindings){
  const auto* binding=entry.second;if(!binding)continue; // a legitimate hide row
  if(binding->effectPath.empty())continue; // borrowed ordinary standard effect
  if(!binding->effect||!binding->owner||!binding->instance||
     !binding->owner->errorText.empty()||!binding->instance->errorText.empty())
   fail(MDA9_DEVICE_FAILURE,"configured scene FX contains a failed compiled assignment");
  const auto rejection=mme::MdaSceneFxAdmission(binding->sas);
  if(!rejection.empty())throw Failure(MDA9_DEVICE_FAILURE,rejection+"\n"+diagnosticUtf8(binding->owner->errorText)+diagnosticUtf8(binding->instance->errorText));
 }
}
uint32_t Session::draw(const visual::Inputs& v,uint32_t order,const std::vector<Mda9EditorOverlayPrimitiveV1>& editorOverlay){
 auto* d=device.p;check(d->SetRenderTarget(0,target),"SetRenderTarget");check(d->SetDepthStencilSurface(depth),"SetDepthStencilSurface");D3DVIEWPORT9 vp{0,0,width,height,0,1};check(d->SetViewport(&vp),"SetViewport");
 const bool classic=(scene.flags&packet::HasClassicPasses)!=0;
 const auto& original=scene.classic;
 const bool effectFrame=!classic||(original.flags&packet::EffectFrame);
 const bool selfShadow=classic&&(original.flags&packet::SelfShadow);
 auto world=identity(),view=matrix(scene.view),projection=matrix(scene.projection),wvp=multiply(view,projection),rotation=matrix(v.sphereRotation);
 const auto lightViewProjection=classic?matrix(original.lightViewProjection):world;
 D3DLIGHT9 light{};light.Type=D3DLIGHT_DIRECTIONAL;light.Diffuse.r=light.Diffuse.g=light.Diffuse.b=1;light.Direction={-scene.lightDirection[0],-scene.lightDirection[1],-scene.lightDirection[2]};
 auto em=[&](const char* name,const D3DMATRIX& m){check(effect->SetMatrix(name,reinterpret_cast<const D3DXMATRIX*>(&m)),name);};
 auto ev=[&](const char* name,const visual::Vector& a){D3DXVECTOR4 value{a[0],a[1],a[2],a[3]};check(effect->SetVector(name,&value),name);};
 const float len=std::sqrt(scene.lightDirection[0]*scene.lightDirection[0]+scene.lightDirection[1]*scene.lightDirection[1]+scene.lightDirection[2]*scene.lightDirection[2]);
 auto bindSurfaceState=[&](bool sampleShadow=true){
  // Scene/external passes may use a fullscreen declaration, streams or device
  // state. Rebind ordinary geometry state without changing planner-owned
  // render targets or viewport; the existing host restores those itself.
  check(d->SetStreamSource(0,gpu->vertices,0,sizeof(packet::Vertex)),"SetStreamSource");check(d->SetStreamSource(1,gpu->normals,0,12),"sphere normal stream");check(d->SetStreamSource(2,gpu->additionalUvs,0,gpu->additionalUvs.p?sizeof(packet::AdditionalUvVertex):0),"additional UV stream");check(d->SetIndices(gpu->indices),"SetIndices");check(d->SetVertexDeclaration(gpu->declaration),"SetVertexDeclaration");
  check(d->SetTransform(D3DTS_WORLD,&world),"world");check(d->SetTransform(D3DTS_VIEW,&view),"view");check(d->SetTransform(D3DTS_PROJECTION,&projection),"projection");
  check(d->SetRenderState(D3DRS_ZENABLE,TRUE),"Z enable");check(d->SetRenderState(D3DRS_LIGHTING,TRUE),"lighting");check(d->SetRenderState(D3DRS_NORMALIZENORMALS,TRUE),"normalize");check(d->SetRenderState(D3DRS_AMBIENT,D3DCOLOR_XRGB(90,90,90)),"ambient");
  // MME object passes inherit original GE/ref=1 alpha testing. In particular
  // SvSSAO's NormalMap outputs alpha zero for transparent texels without
  // enabling the state itself; disabling it makes normals/depth disagree.
  check(d->SetRenderState(D3DRS_ALPHATESTENABLE,classic?TRUE:FALSE),"model shader alpha policy");
  if(classic){check(d->SetRenderState(D3DRS_ALPHAREF,1),"original model alpha ref");check(d->SetRenderState(D3DRS_ALPHAFUNC,D3DCMP_GREATEREQUAL),"original model alpha compare");}
  check(d->SetLight(0,&light),"light");check(d->LightEnable(0,TRUE),"light enable");
  check(d->SetRenderState(D3DRS_ALPHABLENDENABLE,TRUE),"blend");check(d->SetRenderState(D3DRS_SRCBLEND,D3DBLEND_SRCALPHA),"source blend");check(d->SetRenderState(D3DRS_DESTBLEND,D3DBLEND_INVSRCALPHA),"dest blend");check(d->SetRenderState(D3DRS_SEPARATEALPHABLENDENABLE,TRUE),"separate alpha");check(d->SetRenderState(D3DRS_SRCBLENDALPHA,D3DBLEND_ONE),"source alpha");check(d->SetRenderState(D3DRS_DESTBLENDALPHA,D3DBLEND_INVSRCALPHA),"dest alpha");
  check(d->SetRenderState(D3DRS_BLENDOPALPHA,classic?D3DBLENDOP_MAX:D3DBLENDOP_ADD),"original alpha blend operation");
  for(DWORD stage=0;stage<3;++stage){for(auto s:{D3DSAMP_MINFILTER,D3DSAMP_MAGFILTER,D3DSAMP_MIPFILTER})check(d->SetSamplerState(stage,s,D3DTEXF_LINEAR),"sampler");check(d->SetTextureStageState(stage,D3DTSS_TEXCOORDINDEX,0),"UV0");}
  for(DWORD stage=0;stage<3;++stage)check(d->SetTextureStageState(stage,D3DTSS_TEXTURETRANSFORMFLAGS,D3DTTFF_DISABLE),"reset texture transform");
  check(d->SetTextureStageState(0,D3DTSS_COLOROP,D3DTOP_SELECTARG2),"stage0 color");check(d->SetTextureStageState(0,D3DTSS_COLORARG2,D3DTA_DIFFUSE),"stage0 diffuse");check(d->SetTextureStageState(0,D3DTSS_ALPHAOP,D3DTOP_SELECTARG2),"stage0 alpha");check(d->SetTextureStageState(0,D3DTSS_ALPHAARG2,D3DTA_DIFFUSE),"stage0 alpha diffuse");check(d->SetTextureStageState(2,D3DTSS_COLOROP,D3DTOP_DISABLE),"stage2 end");
  em("matWorld",world);em("matWorldViewProj",wvp);em("matRotate",rotation);em("matNormalWorld",world);em("matLightViewProj",lightViewProjection);
  ev("ShadowOptions",{0,classic&&(original.flags&packet::PartialFar)?1.f:0.f,0,0});
  if(classic){
   check(d->SetRenderState(D3DRS_ZFUNC,D3DCMP_LESSEQUAL),"original surface depth comparison");
   check(d->SetRenderState(D3DRS_STENCILENABLE,(original.flags&packet::GroundStencil)?TRUE:FALSE),"original stencil enable");
   check(d->SetRenderState(D3DRS_STENCILFUNC,D3DCMP_ALWAYS),"original surface stencil");check(d->SetRenderState(D3DRS_STENCILREF,1),"original surface stencil ref");check(d->SetRenderState(D3DRS_STENCILPASS,D3DSTENCILOP_REPLACE),"original stencil replace");
   check(d->SetTexture(0,sampleShadow?shadowTexture.p:nullptr),"original shadow map binding");
   for(auto state:{D3DSAMP_MINFILTER,D3DSAMP_MAGFILTER})check(d->SetSamplerState(0,state,original.filter==1?D3DTEXF_POINT:D3DTEXF_LINEAR),"original shadow filter");
   check(d->SetSamplerState(0,D3DSAMP_MIPFILTER,D3DTEXF_NONE),"original shadow mip");check(d->SetSamplerState(0,D3DSAMP_ADDRESSU,D3DTADDRESS_CLAMP),"original shadow clamp U");check(d->SetSamplerState(0,D3DSAMP_ADDRESSV,D3DTADDRESS_CLAMP),"original shadow clamp V");
  }
  ev("Place",{v.eye[0],v.eye[1],v.eye[2],1});
  ev("LightDir",{scene.lightDirection[0]/len,scene.lightDirection[1]/len,scene.lightDirection[2]/len,0});check(effect->SetFloat("parthf",classic?((original.flags&packet::PartialFar)?1.f:0.f):1.f),"parthf");
 };
 auto screenTexture=[&]()->IDirect3DTexture9*{
  if(scene.screenCaptureMode==3){for(size_t i=0;i<scene.media.size();++i)if(scene.media[i].kind==2)return gpu->mediaTextures[i];return nullptr;}
  return screenCaptureTexture.p;
 };
 auto accessoryPrimary=[&](const packet::Material& m)->IDirect3DTexture9*{return m.accessoryTextureType==3?screenTexture():m.diffuseTexture>=0?gpu->textures[m.diffuseTexture]:nullptr;};
 auto bindAccessoryState=[&](const packet::Object& o,const packet::Material& m){
  // Bounded shadow-OFF fixed-function rules from the pinned MIT accessory.cpp
  // (material conversion and ConfigureFixedTexture), using this frozen sample.
  // No world-baked accessory VB or model toon shader participates in this path.
  check(d->SetVertexShader(nullptr),"accessory fixed vertex shader");check(d->SetPixelShader(nullptr),"accessory fixed pixel shader");
  check(d->SetStreamSource(1,nullptr,0,0),"accessory detach sphere normals");check(d->SetStreamSource(2,nullptr,0,0),"accessory detach additional UVs");
  check(d->SetFVF(D3DFVF_XYZ|D3DFVF_NORMAL|D3DFVF_TEX1),"accessory fixed FVF");
  const auto world=matrix(o.world);check(d->SetTransform(D3DTS_WORLD,&world),"accessory exact world");
  D3DLIGHT9 accessory=light;
  std::memcpy(&accessory.Ambient,v.accessoryLightAmbient.data(),12);std::memcpy(&accessory.Diffuse,v.accessoryLightDiffuse.data(),12);std::memcpy(&accessory.Specular,v.accessoryLightSpecular.data(),12);
  accessory.Ambient.a=accessory.Diffuse.a=accessory.Specular.a=1;
  check(d->SetLight(0,&accessory),"accessory source light");check(d->SetRenderState(D3DRS_AMBIENT,0),"accessory global ambient");
  check(d->SetRenderState(D3DRS_COLORVERTEX,FALSE),"accessory material colors");
  for(auto state:{D3DRS_DIFFUSEMATERIALSOURCE,D3DRS_AMBIENTMATERIALSOURCE,D3DRS_SPECULARMATERIALSOURCE,D3DRS_EMISSIVEMATERIALSOURCE})check(d->SetRenderState(state,D3DMCS_MATERIAL),"accessory material source");
  check(d->SetRenderState(D3DRS_SPECULARENABLE,TRUE),"accessory specular");check(d->SetRenderState(D3DRS_LOCALVIEWER,TRUE),"accessory local viewer");
  check(d->SetRenderState(D3DRS_DESTBLEND,o.additiveBlend?D3DBLEND_ONE:D3DBLEND_INVSRCALPHA),"accessory authored blend");
  // Match the original fixed-function render state. Fully transparent texels
  // must fail before writing depth, otherwise an invisible accessory occludes
  // models and later accessories even though its blended color is unchanged.
  check(d->SetRenderState(D3DRS_ALPHATESTENABLE,TRUE),"accessory alpha test");
  check(d->SetRenderState(D3DRS_ALPHAREF,1),"accessory alpha reference");
  check(d->SetRenderState(D3DRS_ALPHAFUNC,D3DCMP_GREATEREQUAL),"accessory alpha comparison");
  // Start every material with a known cascade. The pinned host identifies the
  // sphere role from COUNT2 + CAMERASPACENORMAL, including single-map types1/2.
  for(DWORD stage=0;stage<3;++stage){
   check(d->SetTexture(stage,nullptr),"accessory clear texture");check(d->SetTextureStageState(stage,D3DTSS_COLOROP,D3DTOP_DISABLE),"accessory clear color stage");check(d->SetTextureStageState(stage,D3DTSS_ALPHAOP,D3DTOP_DISABLE),"accessory clear alpha stage");
   check(d->SetTextureStageState(stage,D3DTSS_COLORARG1,D3DTA_TEXTURE),"accessory color texture");check(d->SetTextureStageState(stage,D3DTSS_COLORARG2,D3DTA_CURRENT),"accessory color current");
   check(d->SetTextureStageState(stage,D3DTSS_ALPHAARG1,D3DTA_TEXTURE),"accessory alpha texture");check(d->SetTextureStageState(stage,D3DTSS_ALPHAARG2,D3DTA_CURRENT),"accessory alpha current");
  }
  const uint32_t type=m.accessoryTextureType;
  check(d->SetTextureStageState(0,D3DTSS_COLOROP,(m.diffuseTexture>=0||type==3)?D3DTOP_MODULATE:D3DTOP_SELECTARG2),"accessory base color");check(d->SetTextureStageState(0,D3DTSS_COLORARG2,D3DTA_DIFFUSE),"accessory base material");
  check(d->SetTextureStageState(0,D3DTSS_ALPHAOP,(m.diffuseTexture>=0||type==3)?D3DTOP_MODULATE:D3DTOP_SELECTARG2),"accessory base alpha");check(d->SetTextureStageState(0,D3DTSS_ALPHAARG2,D3DTA_DIFFUSE),"accessory base material alpha");
  check(d->SetTexture(0,accessoryPrimary(m)),"accessory primary texture");
  if(type==1||type==2||type==4||type==5){
   const DWORD stage=type<=2?0:1;D3DMATRIX sphere{};sphere._11=.5f;sphere._22=-.5f;sphere._41=.5f;sphere._42=.5f;sphere._44=1;
   check(d->SetTransform(static_cast<D3DTRANSFORMSTATETYPE>(D3DTS_TEXTURE0+stage),&sphere),"accessory sphere affine UV");check(d->SetTextureStageState(stage,D3DTSS_TEXTURETRANSFORMFLAGS,D3DTTFF_COUNT2),"accessory sphere COUNT2");
   check(d->SetTextureStageState(stage,D3DTSS_TEXCOORDINDEX,D3DTSS_TCI_CAMERASPACENORMAL),"accessory sphere camera normal");check(d->SetTextureStageState(stage,D3DTSS_COLOROP,(type==2||type==5)?D3DTOP_ADD:D3DTOP_MODULATE),"accessory sphere combine");
   check(d->SetTextureStageState(stage,D3DTSS_ALPHAOP,D3DTOP_MODULATE),"accessory sphere alpha");
   if(stage)check(d->SetTexture(stage,gpu->textures[m.sphereTexture]),"accessory second sphere texture");
  }
  // Keep the standard effect's matrices/colors meaningful for host consumers;
  // its Diffuse* techniques are fixed passes, with the device world above.
  em("matWorld",world);em("matWorldViewProj",multiply(world,wvp));em("matNormalWorld",world);
  if(classic)em("matLightViewProj",multiply(world,lightViewProjection));
 };
 auto bindAccessoryEffect=[&](const packet::Object& o,const packet::Material& m,const D3DMATERIAL9& material){
  const auto actual=matrix(o.world);auto lighting=actual;lighting._41=lighting._42=lighting._43=0;
  check(d->SetTransform(D3DTS_WORLD,&actual),"accessory effect world");
  em("matWorld",lighting);em("matObjectWorld",actual);em("matNormalWorld",lighting);em("matWorldViewProj",multiply(actual,wvp));em("matLightViewProj",multiply(actual,lightViewProjection));
  auto inverseScale=identity();const float scale=o.rawScale*10.f;inverseScale._11=inverseScale._22=inverseScale._33=1.f/scale;
  em("matWRotate",multiply(multiply(inverseScale,lighting),rotation));
  D3DLIGHT9 source=light;std::memcpy(&source.Ambient,v.accessoryLightAmbient.data(),12);std::memcpy(&source.Diffuse,v.accessoryLightDiffuse.data(),12);std::memcpy(&source.Specular,v.accessoryLightSpecular.data(),12);source.Ambient.a=source.Diffuse.a=source.Specular.a=1;
  check(d->SetLight(0,&source),"accessory effect light");check(d->SetRenderState(D3DRS_DESTBLEND,o.additiveBlend?D3DBLEND_ONE:D3DBLEND_INVSRCALPHA),"accessory effect blend");
  const bool sphereOnly=m.accessoryTextureType==1||m.accessoryTextureType==2;
  check(d->SetTexture(1,accessoryPrimary(m)),"accessory effect primary");check(d->SetTexture(2,m.sphereTexture>=0?gpu->textures[m.sphereTexture]:nullptr),"accessory effect sphere");
  ev("EgColor",{(material.Ambient.r*source.Ambient.r+material.Emissive.r)*original.accessoryGroundGray,(material.Ambient.g*source.Ambient.g+material.Emissive.g)*original.accessoryGroundGray,(material.Ambient.b*source.Ambient.b+material.Emissive.b)*original.accessoryGroundGray,material.Diffuse.a*o.opacity});
  ev("SpcColor",{material.Specular.r*source.Specular.r,material.Specular.g*source.Specular.g,material.Specular.b*source.Specular.b,material.Power==0?.1f:material.Power});
  ev("DifColor",{material.Diffuse.r*source.Diffuse.r,material.Diffuse.g*source.Diffuse.g,material.Diffuse.b*source.Diffuse.b,1});
  ev("AccessoryOptions",{sphereOnly?1.f:0.f,!sphereOnly&&(m.diffuseTexture>=0||m.accessoryTextureType==3)?1.f:0.f,(sphereOnly||m.sphereTexture>=0)?1.f:0.f,original.accessoryGroundGray});
 };
 auto bindFixedModel=[&](const packet::Material& m){
  // Actual original ordinary-model FFP path (MIT pin, ConfigureMaterialStages
  // and DrawModelMaterials), independently selected by the original mode gate.
  check(d->SetVertexShader(nullptr),"model fixed vertex shader");check(d->SetPixelShader(nullptr),"model fixed pixel shader");
  check(d->SetStreamSource(0,gpu->fixedVertices,0,gpu->fixedStride),"original interleaved model stream");check(d->SetStreamSource(1,nullptr,0,0),"model fixed detach normals");check(d->SetStreamSource(2,nullptr,0,0),"model fixed detach UVs");check(d->SetFVF(gpu->fixedFvf),"original model fixed FVF");
  D3DLIGHT9 source=light;std::memcpy(&source.Ambient,original.modelLightAmbient,12);std::memcpy(&source.Diffuse,original.modelLightDiffuse,12);std::memcpy(&source.Specular,original.modelLightSpecular,12);source.Ambient.a=source.Diffuse.a=source.Specular.a=1;
  check(d->SetLight(0,&source),"original model source light");check(d->SetRenderState(D3DRS_AMBIENT,0),"original model global ambient");check(d->SetRenderState(D3DRS_NORMALIZENORMALS,FALSE),"original model raw normal length");
  check(d->SetRenderState(D3DRS_COLORVERTEX,TRUE),"original model vertex colors");check(d->SetRenderState(D3DRS_DIFFUSEMATERIALSOURCE,D3DMCS_COLOR1),"original model diffuse source");check(d->SetRenderState(D3DRS_AMBIENTMATERIALSOURCE,D3DMCS_MATERIAL),"original model ambient source");check(d->SetRenderState(D3DRS_SPECULARMATERIALSOURCE,D3DMCS_COLOR2),"original model specular source");check(d->SetRenderState(D3DRS_EMISSIVEMATERIALSOURCE,D3DMCS_MATERIAL),"original model emissive source");
  check(d->SetRenderState(D3DRS_SPECULARENABLE,TRUE),"original model specular");check(d->SetRenderState(D3DRS_LOCALVIEWER,TRUE),"original model viewer");check(d->SetRenderState(D3DRS_ALPHATESTENABLE,TRUE),"original model alpha test");check(d->SetRenderState(D3DRS_ALPHAREF,1),"original model alpha ref");check(d->SetRenderState(D3DRS_ALPHAFUNC,D3DCMP_GREATEREQUAL),"original model alpha compare");
  D3DMATRIX half{};half._11=.5f;half._22=-.5f;half._41=half._42=.5f;half._44=1;
  for(DWORD stage=0;stage<3;++stage){
   check(d->SetTexture(stage,nullptr),"original model clear texture");check(d->SetTextureStageState(stage,D3DTSS_COLOROP,stage?D3DTOP_DISABLE:D3DTOP_MODULATE),"original model cascade");check(d->SetTextureStageState(stage,D3DTSS_COLORARG1,D3DTA_TEXTURE),"original model texture arg");check(d->SetTextureStageState(stage,D3DTSS_COLORARG2,stage?D3DTA_CURRENT:D3DTA_DIFFUSE),"original model color arg");
   check(d->SetTextureStageState(stage,D3DTSS_ALPHAOP,D3DTOP_MODULATE),"original model texture alpha");check(d->SetTextureStageState(stage,D3DTSS_ALPHAARG1,D3DTA_TEXTURE),"original model alpha texture");check(d->SetTextureStageState(stage,D3DTSS_ALPHAARG2,D3DTA_CURRENT),"original model alpha current");
   check(d->SetSamplerState(stage,D3DSAMP_MIPFILTER,D3DTEXF_NONE),"original model mip filter");for(auto address:{D3DSAMP_ADDRESSU,D3DSAMP_ADDRESSV})check(d->SetSamplerState(stage,address,stage?D3DTADDRESS_WRAP:D3DTADDRESS_CLAMP),"original model addressing");
  }
  const auto toon=originalToonTransform(scene.lightDirection,rotation);check(d->SetTransform(D3DTS_TEXTURE0,&toon),"original toon transform");check(d->SetTextureStageState(0,D3DTSS_TEXTURETRANSFORMFLAGS,D3DTTFF_COUNT2),"original toon COUNT2");check(d->SetTextureStageState(0,D3DTSS_TEXCOORDINDEX,D3DTSS_TCI_CAMERASPACENORMAL),"original toon normal coordinates");check(d->SetTexture(0,m.toonTexture>=0?gpu->textures[m.toonTexture]:nullptr),"original toon image");
  for(auto state:{D3DSAMP_MINFILTER,D3DSAMP_MAGFILTER})check(d->SetSamplerState(0,state,D3DTEXF_LINEAR),"original toon filtering");
  if(m.diffuseTexture>=0){check(d->SetTexture(1,gpu->textures[m.diffuseTexture]),"original model diffuse");check(d->SetTextureStageState(1,D3DTSS_COLOROP,D3DTOP_MODULATE),"original diffuse modulation");check(d->SetTextureStageState(1,D3DTSS_TEXCOORDINDEX,0),"original diffuse UV0");check(d->SetTextureStageState(1,D3DTSS_TEXTURETRANSFORMFLAGS,D3DTTFF_DISABLE),"original diffuse no transform");}
  if(m.sphereMode&&m.sphereTexture>=0){
   const DWORD stage=m.diffuseTexture>=0?2:1;const auto transform=m.sphereMode==3?identity():half;
   check(d->SetTransform(static_cast<D3DTRANSFORMSTATETYPE>(D3DTS_TEXTURE0+stage),&transform),"original sphere transform");check(d->SetTextureStageState(stage,D3DTSS_TEXTURETRANSFORMFLAGS,D3DTTFF_COUNT2),"original sphere COUNT2");check(d->SetTextureStageState(stage,D3DTSS_TEXCOORDINDEX,m.sphereMode==3?1:D3DTSS_TCI_CAMERASPACENORMAL),"original sphere coordinates");check(d->SetTextureStageState(stage,D3DTSS_COLOROP,m.sphereMode==2?D3DTOP_ADD:D3DTOP_MODULATE),"original sphere combine");check(d->SetTexture(stage,gpu->textures[m.sphereTexture]),"original sphere image");
  }
 };
 bindSurfaceState();
 check(MmeHostClear(d,0,nullptr,D3DCLEAR_TARGET|D3DCLEAR_ZBUFFER|(classic?D3DCLEAR_STENCIL:0),color(scene.clearColor),1,0),"host Clear");
 HRESULT beginScene;
 // Clear the prior callback failure even when BeginScene fails before invoking
 // this frame's callback. Status must not depend on allocating diagnostic text.
 if(objectFx){objectFx->failureStatus=MDA9_OK;objectFx->error.clear();}
 if(sceneEffects())repeatCount=1; // OnBeginScene may replace this with N+1.
 try {
  // Validation builds exercise exceptions before/after a successful host
  // Begin. The shipping build never defines this switch or adds an ABI hook.
#if defined(MDA9_VALIDATION_BEGIN_THROW) && MDA9_VALIDATION_BEGIN_THROW == 1
  throw std::bad_alloc();
#endif
  beginScene=MmeHostBeginScene(d,1);
#if defined(MDA9_VALIDATION_BEGIN_THROW) && MDA9_VALIDATION_BEGIN_THROW == 2
  if(SUCCEEDED(beginScene))throw std::bad_alloc();
#endif
 } catch(...) {
  // Host bookkeeping can allocate after the real BeginScene. Its partial
  // initialization cannot be rolled back by this adapter. End the raw scene
  // if one began (otherwise INVALIDCALL is harmless), then require Destroy.
  poisoned=true;started=false;d->EndScene();throw;
 }
 if(FAILED(beginScene)&&objectFx&&objectFx->failureStatus!=MDA9_OK)
  throw Failure(objectFx->failureStatus,objectFx->error.empty()?"object FX application failed before drawing":objectFx->error);
 check(beginScene,"host BeginScene");started=true;bridgeCheck();if(!MmeHostEffectsActive())fail(MDA9_DEVICE_FAILURE,"MME host initialization did not become active");
 check(MmeHostClear(d,0,nullptr,D3DCLEAR_TARGET|D3DCLEAR_ZBUFFER|(classic?D3DCLEAR_STENCIL:0),color(scene.clearColor),1,0),"tracked main-target clear after BeginScene");if(GetClearColor()!=color(scene.clearColor))fail(MDA9_DEVICE_FAILURE,"host clear color did not follow tracked target clear");
 std::vector<std::pair<size_t,uint32_t>> keys;for(size_t i=0;i<scene.objects.size();++i)if(scene.objects[i].visible)for(uint32_t j=0;j<scene.objects[i].materialCount;++j)keys.emplace_back(i,j);
 if(scene.packetVersion>=3)std::stable_sort(keys.begin(),keys.end(),[&](const auto& a,const auto& b){return originalDrawBefore(scene.objects[a.first],scene.objects[b.first]);});
 if(order==MDA9_ORDER_OPAQUE_FIRST)std::stable_partition(keys.begin(),keys.end(),[&](auto k){return v.materials[scene.objects[k.first].materialStart+k.second].opaque!=0;});
 const int turnCount=sceneEffects()?repeatCount:1;
 if(sceneEffects()&&(turnCount<1||turnCount>4096))fail(MDA9_UNSUPPORTED,"scene FX requested a render-turn count outside 1..4096");
 uint32_t draws=0;
 auto clearIdentity=[&]{currentObject=0;currentMaterial=-1;currentTechnic=1;};
 auto drawMedia=[&]{
  if(scene.media.empty())return;
  // MME must see the original DrawPrimitive before the first model in EACH
  // render turn: its hook queues/replays background records into offscreens.
  // A CPU composite after Capture would bypass that established host contract.
  clearIdentity();Com<IDirect3DStateBlock9> saved;check(d->CreateStateBlock(D3DSBT_ALL,saved.put()),"save pre-background state");check(saved->Capture(),"capture pre-background state");
  auto restore=[&]{check(saved->Apply(),"restore pre-background state");};
  try{
   check(d->SetVertexShader(nullptr),"original background fixed vertex shader");check(d->SetPixelShader(nullptr),"original background fixed pixel shader");
   check(d->SetFVF(D3DFVF_XYZRHW|D3DFVF_DIFFUSE|D3DFVF_TEX1),"original background FVF324");check(d->SetRenderState(D3DRS_ZENABLE,FALSE),"original background no depth");check(d->SetRenderState(D3DRS_FILLMODE,D3DFILL_SOLID),"original background solid");check(d->SetRenderState(D3DRS_CULLMODE,D3DCULL_CCW),"original background cull");
   // InitRenderStates' AF-off cascade/filter and the established Classic
   // frame-head alpha policy. Original interactive frames inherit blend TRUE
   // from frame_scene's overlay tail; this explicit policy is sample-stable.
   check(d->SetRenderState(D3DRS_ALPHABLENDENABLE,TRUE),"classic background blending");check(d->SetRenderState(D3DRS_SRCBLEND,D3DBLEND_SRCALPHA),"original background source blend");check(d->SetRenderState(D3DRS_DESTBLEND,D3DBLEND_INVSRCALPHA),"original background destination blend");check(d->SetRenderState(D3DRS_SEPARATEALPHABLENDENABLE,TRUE),"original background separate alpha");check(d->SetRenderState(D3DRS_BLENDOPALPHA,D3DBLENDOP_MAX),"original background alpha MAX");
   D3DCAPS9 caps{};check(d->GetDeviceCaps(&caps),"background alpha capabilities");const bool alphaTest=(caps.AlphaCmpCaps&D3DPCMPCAPS_GREATEREQUAL)!=0;
   check(d->SetRenderState(D3DRS_ALPHATESTENABLE,alphaTest?TRUE:FALSE),"original background alpha test");if(alphaTest){check(d->SetRenderState(D3DRS_ALPHAREF,1),"original background alpha ref");check(d->SetRenderState(D3DRS_ALPHAFUNC,D3DCMP_GREATEREQUAL),"original background alpha comparison");}
   for(DWORD stage=0;stage<3;++stage){
    check(d->SetTexture(stage,nullptr),"clear pre-background texture");check(d->SetTextureStageState(stage,D3DTSS_TEXTURETRANSFORMFLAGS,D3DTTFF_DISABLE),"background no texture transform");check(d->SetTextureStageState(stage,D3DTSS_TEXCOORDINDEX,0),"background UV0");
    check(d->SetTextureStageState(stage,D3DTSS_COLOROP,stage?D3DTOP_DISABLE:D3DTOP_MODULATE),"original background color operation");check(d->SetTextureStageState(stage,D3DTSS_ALPHAOP,stage?D3DTOP_DISABLE:D3DTOP_MODULATE),"original background alpha operation");
   }
   for(auto pair:std::array<std::pair<D3DTEXTURESTAGESTATETYPE,DWORD>,4>{{{D3DTSS_COLORARG1,D3DTA_TEXTURE},{D3DTSS_COLORARG2,D3DTA_DIFFUSE},{D3DTSS_ALPHAARG1,D3DTA_TEXTURE},{D3DTSS_ALPHAARG2,D3DTA_DIFFUSE}}})check(d->SetTextureStageState(0,pair.first,pair.second),"original background texture modulation");
   for(auto state:{D3DSAMP_MINFILTER,D3DSAMP_MAGFILTER})check(d->SetSamplerState(0,state,D3DTEXF_LINEAR),"original background linear filter");check(d->SetSamplerState(0,D3DSAMP_MIPFILTER,D3DTEXF_NONE),"original background no mip sampling");check(d->SetSamplerState(0,D3DSAMP_MAXANISOTROPY,1),"original background AF off");for(auto state:{D3DSAMP_ADDRESSU,D3DSAMP_ADDRESSV})check(d->SetSamplerState(0,state,D3DTADDRESS_WRAP),"original background wrap");
   for(size_t i=0;i<scene.media.size();++i){
    if(!scene.media[i].drawBackground)continue;
    check(d->SetTexture(0,gpu->mediaTextures[i]),"original picture/video background texture");check(d->SetStreamSource(0,gpu->mediaVertices,static_cast<UINT>(i*6*sizeof(BackgroundVertex)),sizeof(BackgroundVertex)),"original background stream");
    check(MmeHostDrawPrimitive(d,D3DPT_TRIANGLELIST,0,2),"original host background draw");bridgeCheck();checkSceneFxState();++draws;
   }
   check(d->SetRenderState(D3DRS_ZENABLE,TRUE),"original background restore depth");restore();
  }catch(...){try{restore();}catch(...){poisoned=true;}throw;}
 };
 auto indexedPass=[&](const std::pair<size_t,uint32_t>& k,const char* technique,bool fixed=false){
  auto& o=scene.objects[k.first];auto& m=scene.materials[o.materialStart+k.second];
  currentObject=o.signedOrder;currentMaterial=static_cast<int>(k.second);
  if(!fixed){check(effect->SetTechnique(technique),"original pass technique");
   UINT passes=0;check(effect->Begin(&passes,0),"original pass Begin");if(passes!=1){effect->End();fail(MDA9_DEVICE_FAILURE,"unexpected original pass count");}
   HRESULT begin=effect->BeginPass(0);if(FAILED(begin)){effect->End();check(begin,"original pass BeginPass");}}
  const HRESULT draw=MmeHostDrawIndexedPrimitive(d,D3DPT_TRIANGLELIST,0,0,static_cast<UINT>(scene.vertices.size()),m.startIndex,m.indexCount/3);
  const HRESULT passEnd=fixed?S_OK:effect->EndPass(),end=fixed?S_OK:effect->End();check(draw,"original host pass draw");check(passEnd,"original pass EndPass");check(end,"original pass End");bridgeCheck();
  if(objectFx){checkObjectFxBinding(o.id,static_cast<int32_t>(k.second));if(diagnostic.passFailures)fail(MDA9_DEVICE_FAILURE,"configured effect original-pass draw failed");}
  ++draws;
 };
 auto bindEdgeStream=[&]{
  check(d->SetStreamSource(0,gpu->edges,0,sizeof(packet::EdgeVertex)),"original edge stream");check(d->SetStreamSource(1,nullptr,0,0),"edge detach normals");check(d->SetStreamSource(2,nullptr,0,0),"edge detach UV");check(d->SetFVF(D3DFVF_XYZ|D3DFVF_DIFFUSE),"original edge XYZ DIFFUSE");check(d->SetIndices(gpu->indices),"original edge indices");
 };
 auto bindFixedSolid=[&](bool lighting){
  check(d->SetVertexShader(nullptr),"original solid fixed vertex shader");check(d->SetPixelShader(nullptr),"original solid fixed pixel shader");check(d->SetTexture(0,nullptr),"original solid no toon");
  check(d->SetRenderState(D3DRS_LIGHTING,lighting?TRUE:FALSE),"original solid lighting");check(d->SetRenderState(D3DRS_AMBIENT,0),"original solid global ambient");check(d->SetRenderState(D3DRS_NORMALIZENORMALS,FALSE),"original solid raw normals");check(d->SetRenderState(D3DRS_COLORVERTEX,TRUE),"original solid vertex colors");
  check(d->SetRenderState(D3DRS_DIFFUSEMATERIALSOURCE,D3DMCS_COLOR1),"original solid diffuse source");check(d->SetRenderState(D3DRS_AMBIENTMATERIALSOURCE,D3DMCS_MATERIAL),"original solid ambient source");check(d->SetRenderState(D3DRS_SPECULARMATERIALSOURCE,D3DMCS_COLOR2),"original solid specular source");check(d->SetRenderState(D3DRS_EMISSIVEMATERIALSOURCE,D3DMCS_MATERIAL),"original solid emissive source");
  check(d->SetTextureStageState(0,D3DTSS_COLOROP,D3DTOP_SELECTARG2),"original solid color pass through");check(d->SetTextureStageState(0,D3DTSS_COLORARG2,D3DTA_DIFFUSE),"original solid vertex color");check(d->SetTextureStageState(0,D3DTSS_ALPHAOP,D3DTOP_SELECTARG2),"original solid alpha pass through");check(d->SetTextureStageState(0,D3DTSS_ALPHAARG2,D3DTA_DIFFUSE),"original solid vertex alpha");check(d->SetTextureStageState(1,D3DTSS_COLOROP,D3DTOP_DISABLE),"original solid end cascade");
  check(d->SetRenderState(D3DRS_ALPHATESTENABLE,TRUE),"original solid alpha test");check(d->SetRenderState(D3DRS_ALPHAREF,1),"original solid alpha ref");check(d->SetRenderState(D3DRS_ALPHAFUNC,D3DCMP_GREATEREQUAL),"original solid alpha compare");
 };
 auto drawGround=[&]{
  clearIdentity();if(!(original.flags&packet::GroundShadow))return;
  D3DMATRIX projected{};projected._11=projected._33=projected._44=1;projected._21=-original.groundDirection[0]/original.groundDirection[1];projected._23=-original.groundDirection[2]/original.groundDirection[1];projected._42=.10000000149011612f;
  auto bindGround=[&](const D3DMATRIX& objectWorld){
   bindSurfaceState();const auto groundWorld=multiply(objectWorld,projected);
   check(d->SetTransform(D3DTS_WORLD,&groundWorld),"original projected world");em("matWorld",groundWorld);em("matWorldViewProj",multiply(groundWorld,wvp));
   check(d->SetRenderState(D3DRS_CULLMODE,D3DCULL_NONE),"original ground cull");check(d->SetRenderState(D3DRS_ZFUNC,D3DCMP_LESS),"original ground strict depth");check(d->SetRenderState(D3DRS_ZWRITEENABLE,TRUE),"original ground depth write");
   check(d->SetRenderState(D3DRS_STENCILFUNC,D3DCMP_GREATER),"original ground stencil compare");check(d->SetRenderState(D3DRS_STENCILREF,2),"original ground stencil ref");check(d->SetRenderState(D3DRS_STENCILPASS,D3DSTENCILOP_REPLACE),"original ground stencil replace");
   check(d->SetRenderState(D3DRS_ALPHABLENDENABLE,(original.flags&packet::TranslucentGround)?TRUE:FALSE),"original translucent ground toggle");check(d->SetTexture(0,nullptr),"original ground no texture");
   D3DLIGHT9 source=light;std::memcpy(&source.Ambient,original.groundLightAmbient,12);std::memcpy(&source.Specular,original.modelLightSpecular,12);source.Ambient.a=source.Specular.a=1;
   // Original effect frames also submit projected silhouettes with no VS/PS:
   // RenderShadowMap and the preceding accessory/outline tails clear both.
   // A helper shader changes MME's WORLD snapshot from the projected matrix
   // to identity (model) / placement (accessory), despite the same clip WVP.
   source.Diffuse.r=source.Diffuse.g=source.Diffuse.b=0;check(d->SetLight(0,&source),"original fixed ground light");bindFixedSolid(true);
   currentTechnic=3;
  };
  // Projected accessories use source slot order rather than mixed render order.
  // The trailing blend/material reset is performed even for an empty range.
  for(size_t i:metadata.accessories){const auto& o=scene.objects[i];if(!o.visible||!o.shadowEnabled)continue;
   for(uint32_t j=0;j<o.materialCount;++j){const auto& m=scene.materials[o.materialStart+j];if(!m.indexCount||!(m.classicFlags&packet::CastGround))continue;
    bindGround(matrix(o.world));D3DMATERIAL9 material{};material.Diffuse.a=.699999988079071f;material.Ambient={original.accessoryGroundGray,original.accessoryGroundGray,original.accessoryGroundGray,original.accessoryGroundGray};material.Specular.a=1;check(d->SetMaterial(&material),"accessory projected material");
    ev("EgColor",{material.Ambient.r*original.groundLightAmbient[0],material.Ambient.g*original.groundLightAmbient[1],material.Ambient.b*original.groundLightAmbient[2],material.Diffuse.a});ev("ShadowOptions",{0,0,0,0});
    check(d->SetStreamSource(1,nullptr,0,0),"projected accessory detach normal");check(d->SetStreamSource(2,nullptr,0,0),"projected accessory detach UV");check(d->SetFVF(D3DFVF_XYZ|D3DFVF_NORMAL|D3DFVF_TEX1),"projected accessory source FVF");
    indexedPass({i,j},"MdaSolidPlainTec",true);
   }
  }
  D3DMATERIAL9 source{};static_assert(sizeof(source)==17*sizeof(float),"ground material transport layout");std::memcpy(&source,original.groundMaterial,sizeof(source));
  bindGround(world);check(d->SetMaterial(&source),"original source ground material");
  for(auto k:keys){const auto& o=scene.objects[k.first];const auto& m=scene.materials[o.materialStart+k.second];if(o.kind!=packet::Model||!m.indexCount||!(m.classicFlags&packet::CastGround))continue;
   bindGround(world);check(d->SetMaterial(&source),"original model ground material");bindEdgeStream();
   ev("EgColor",{source.Ambient.r*original.groundLightAmbient[0],source.Ambient.g*original.groundLightAmbient[1],source.Ambient.b*original.groundLightAmbient[2],source.Diffuse.a});ev("ShadowOptions",{0,0,1,0});
   indexedPass(k,"MdaSolidTec",true);
  }
  clearIdentity();bindSurfaceState();
 };
 auto drawOutlines=[&]{
  clearIdentity();
  for(auto k:keys){const auto& o=scene.objects[k.first];const auto& m=scene.materials[o.materialStart+k.second];if(o.kind!=packet::Model||o.additiveBlend||!o.outlineEligible||!m.indexCount||!(m.classicFlags&packet::DrawOutline))continue;
   bindSurfaceState();bindEdgeStream();currentTechnic=4;
   check(d->SetRenderState(D3DRS_CULLMODE,D3DCULL_CW),"original outline inverted cull");check(d->SetRenderState(D3DRS_ZFUNC,effectFrame?D3DCMP_LESSEQUAL:D3DCMP_LESS),"original outline depth comparison");check(d->SetRenderState(D3DRS_ZWRITEENABLE,TRUE),"original outline depth write");
   if(!effectFrame)bindFixedSolid(false);
   ev("EgColor",{m.edgeColor[0],m.edgeColor[1],m.edgeColor[2],m.edgeColor[3]});indexedPass(k,"MdaSolidTec",!effectFrame);
  }
  clearIdentity();bindSurfaceState();
 };
 if(selfShadow){
  // Exactly one shadow-map clear/generation before the accepted N..0 scene
  // turns. The existing MME dispatcher sees the genuine zplot identity.
  clearIdentity();Com<IDirect3DSurface9> previousTarget,previousDepth;D3DVIEWPORT9 previousViewport{};
  check(d->GetRenderTarget(0,previousTarget.put()),"save original shadow target");check(d->GetDepthStencilSurface(previousDepth.put()),"save original shadow depth");check(d->GetViewport(&previousViewport),"save original shadow viewport");
  auto restore=[&]{check(d->SetDepthStencilSurface(nullptr),"detach original shadow depth");check(d->SetRenderTarget(0,previousTarget),"restore original main target");check(d->SetDepthStencilSurface(previousDepth),"restore original main depth");check(d->SetViewport(&previousViewport),"restore original main viewport");clearIdentity();bindSurfaceState();};
  try{
   for(unsigned i=0;i<16;++i)check(d->SetTexture(i,nullptr),"detach shadow input");
   check(d->SetDepthStencilSurface(nullptr),"detach main depth for shadow map");check(d->SetRenderTarget(0,shadowTarget),"original R32F target");check(d->SetDepthStencilSurface(shadowDepth),"original D24X8 depth");
   D3DVIEWPORT9 shadowViewport{0,0,original.width,original.height,0,1};check(d->SetViewport(&shadowViewport),"original shadow viewport");check(d->Clear(0,nullptr,D3DCLEAR_TARGET|D3DCLEAR_ZBUFFER,0xffffffff,1,0),"original white shadow clear");
   if(original.flags&packet::GenerateShadow){
    auto caster=[&](size_t i,uint32_t j){const auto& o=scene.objects[i];const auto& m=scene.materials[o.materialStart+j];if(!o.visible||!m.indexCount||!(m.classicFlags&packet::CastShadow)||m.diffuse[3]==.9800000190734863f)return;
     if(o.kind==packet::Accessory&&(!o.shadowEnabled||o.additiveBlend))return;
     bindSurfaceState(false);currentTechnic=5;
     const auto objectWorld=o.kind==packet::Accessory?matrix(o.world):world;const auto light=multiply(objectWorld,lightViewProjection);
     check(d->SetTransform(D3DTS_WORLD,&objectWorld),"original caster world");em("matWorld",objectWorld);em("matLightViewProj",light);em("matWorldViewProj",o.kind==packet::Accessory?light:wvp);
     if(o.kind==packet::Accessory){D3DMATERIAL9 blank{};blank.Diffuse.a=blank.Specular.a=1;check(d->SetMaterial(&blank),"original accessory caster material");}
     check(d->SetRenderState(D3DRS_STENCILENABLE,FALSE),"shadow no stencil");check(d->SetRenderState(D3DRS_ALPHABLENDENABLE,FALSE),"original shadow no blend");check(d->SetRenderState(D3DRS_ZWRITEENABLE,TRUE),"original shadow depth write");check(d->SetRenderState(D3DRS_ZFUNC,original.comparison==1?D3DCMP_LESS:D3DCMP_LESSEQUAL),"original shadow depth comparison");check(d->SetRenderState(D3DRS_CULLMODE,(m.classicFlags&packet::ShadowDoubleSided)?D3DCULL_NONE:D3DCULL_CCW),"original caster cull");
     indexedPass({i,j},"ZValuePlotTec");
    };
    for(size_t i:metadata.accessories)for(uint32_t j=0;j<scene.objects[i].materialCount;++j)caster(i,j);
    for(size_t i:metadata.models)for(uint32_t j=0;j<scene.objects[i].materialCount;++j)caster(i,j);
   }
  }catch(...){try{restore();}catch(...){poisoned=true;}throw;}
  restore();
 }
 auto drawSurface=[&](const std::pair<size_t,uint32_t>& k){auto& o=scene.objects[k.first];auto& m=scene.materials[o.materialStart+k.second];if(!(m.flags&2)||!m.indexCount)return;auto& a=v.materials[o.materialStart+k.second];currentObject=o.signedOrder;currentMaterial=static_cast<int>(k.second);auto mat=material(static_cast<int>(k.first),static_cast<int>(k.second));const bool accessory=o.kind==packet::Accessory;const bool accessoryEffect=accessory&&classic&&effectFrame&&o.shadowEnabled;const bool shaderSurface=accessory?accessoryEffect:effectFrame;currentTechnic=selfShadow&&(m.classicFlags&packet::ReceiveShadow)?2:1;
   // An accessory or an external pass may have replaced the declaration/FVF,
   // shaders, transforms and texture cascade. Restore every ordinary draw.
   bindSurfaceState();if(accessory&&!accessoryEffect){mat.Diffuse.a*=o.opacity;bindAccessoryState(o,m);}
   const bool fixedAdditive=!accessory&&!effectFrame&&o.additiveBlend;
   check(d->SetMaterial(&mat),"material");check(d->SetRenderState(D3DRS_CULLMODE,(!fixedAdditive&&((m.flags&1)||(!accessory&&!effectFrame&&mat.Diffuse.a<1)))?D3DCULL_NONE:D3DCULL_CCW),"cull");check(d->SetRenderState(D3DRS_ZWRITEENABLE,TRUE),"depth write");
   if(!accessory){check(d->SetTexture(1,m.diffuseTexture>=0?gpu->textures[m.diffuseTexture]:m.sphereTexture>=0?gpu->textures[m.sphereTexture]:nullptr),"base texture");check(d->SetTexture(2,m.sphereTexture>=0?gpu->textures[m.sphereTexture]:nullptr),"sphere texture");
   check(d->SetTextureStageState(1,D3DTSS_COLOROP,m.diffuseTexture>=0?D3DTOP_MODULATE:D3DTOP_SELECTARG2),"texture color op");check(d->SetTextureStageState(1,D3DTSS_COLORARG1,D3DTA_TEXTURE),"texture color arg1");check(d->SetTextureStageState(1,D3DTSS_COLORARG2,D3DTA_CURRENT),"texture color arg2");check(d->SetTextureStageState(1,D3DTSS_ALPHAOP,m.diffuseTexture>=0?D3DTOP_MODULATE:D3DTOP_SELECTARG2),"texture alpha op");check(d->SetTextureStageState(1,D3DTSS_ALPHAARG1,D3DTA_TEXTURE),"texture alpha arg1");check(d->SetTextureStageState(1,D3DTSS_ALPHAARG2,D3DTA_CURRENT),"texture alpha arg2");}
   const bool add=m.sphereMode==2||(m.sphereMode==3&&a.subTextureAdditive);
   ev("DifColor",accessory?a.diffuse:visual::Vector{mat.Diffuse.r,mat.Diffuse.g,mat.Diffuse.b,mat.Diffuse.a});check(effect->SetBool("transp",FALSE),"transparent");ev("EgColor",a.eg);ev("SpcColor",a.specular);ev("ToonColor",a.toon);ev("TexCMul",a.texMul);ev("TexCAdd",a.texAdd);ev("SphCMul",a.sphereMul);ev("SphCAdd",a.sphereAdd);ev("ShaderOptions",{a.cutoff,static_cast<float>(m.sphereMode==3&&a.subTextureAdditive?4:m.sphereMode),m.diffuseTexture>=0?1.f:0.f,0});check(effect->SetBool("spadd",accessory?(m.accessoryTextureType==2||m.accessoryTextureType==5):add),"sphere add");
   if(accessoryEffect)bindAccessoryEffect(o,m,mat);
   if(!accessory&&!effectFrame)bindFixedModel(m);
   if(!accessory&&classic)check(d->SetRenderState(D3DRS_DESTBLEND,o.additiveBlend?D3DBLEND_ONE:D3DBLEND_INVSRCALPHA),"original model authored blend");
   ev("ShadowOptions",{selfShadow&&(m.classicFlags&packet::ReceiveShadow)?1.f:0.f,classic&&(original.flags&packet::PartialFar)?1.f:0.f,0,0});
   const char* technique=accessory?(m.accessoryTextureType>=4?"DiffuseBSSphiaTexTec":(m.accessoryTextureType==1||m.accessoryTextureType==2)?"DiffuseBSSphiaTec":(m.diffuseTexture>=0||m.accessoryTextureType==3)?"DiffuseBSTextureTec":"DiffuseBufferShadowTec"):m.sphereMode==3?(m.diffuseTexture>=0?"BShadowTextureTexCd2Tec":"BShadowTexCd2Tec"):m.sphereMode>0?(m.diffuseTexture>=0?"BShadowSphiaTextureTec":"BShadowSphiaTec"):(m.diffuseTexture>=0?"BShadowTextureTec":"ColorRenderTec");check(effect->SetTechnique(technique),"technique");
   if(shaderSurface){UINT passes=0;check(effect->Begin(&passes,0),"effect Begin");if(passes!=1){effect->End();fail(MDA9_DEVICE_FAILURE,"unexpected ordinary surface pass count");}HRESULT begin=effect->BeginPass(0);if(FAILED(begin)){effect->End();check(begin,"effect BeginPass");}}
   const auto beforeDispatch=diagnostic.dispatches,beforeFallback=diagnostic.fallbacks;
   if(!accessory&&!effectFrame&&o.additiveBlend&&mat.Diffuse.a<1){check(d->SetRenderState(D3DRS_CULLMODE,D3DCULL_CW),"original additive back faces");check(MmeHostDrawIndexedPrimitive(d,D3DPT_TRIANGLELIST,0,0,static_cast<UINT>(scene.vertices.size()),m.startIndex,m.indexCount/3),"original additive back-face draw");bridgeCheck();check(d->SetRenderState(D3DRS_CULLMODE,D3DCULL_CCW),"original additive front faces");++draws;}
   HRESULT draw=MmeHostDrawIndexedPrimitive(d,D3DPT_TRIANGLELIST,0,0,static_cast<UINT>(scene.vertices.size()),m.startIndex,m.indexCount/3);HRESULT endPass=S_OK,end=S_OK;if(shaderSurface){endPass=effect->EndPass();end=effect->End();}check(draw,"host draw");check(endPass,"effect EndPass");check(end,"effect End");bridgeCheck();
   if(objectFx){
    checkObjectFxBinding(o.id,static_cast<int32_t>(k.second));
    if(diagnostic.passFailures)fail(MDA9_DEVICE_FAILURE,"configured object FX effect-pass draw failed");
    // Original MMHack treats CurrentObject==0 as the no-object sentinel even
    // when the exact model getter order is0. Keep that ordinary fallback;
    // binding/compile/callback/pass-failure checks remain authoritative above.
    if(!sceneEffects()&&o.signedOrder!=0&&objectfx::Resolve(objectFx->rows,o.id,static_cast<int32_t>(k.second))&&
       (diagnostic.dispatches==beforeDispatch||diagnostic.fallbacks!=beforeFallback))
     fail(MDA9_DEVICE_FAILURE,"configured object FX unexpectedly fell back to the ordinary surface");
   }
   const int32_t base=accessory&&(m.accessoryTextureType==1||m.accessoryTextureType==2)?-1:m.diffuseTexture;
   const int32_t sphere=accessory&&(m.accessoryTextureType==1||m.accessoryTextureType==2)?m.diffuseTexture:m.sphereTexture;
   if(accessory&&m.accessoryTextureType==3&&GetTexture()!=screenTexture())fail(MDA9_DEVICE_FAILURE,"host captured wrong screen texture");if(base>=0&&GetTexture()!=gpu->textures[base])fail(MDA9_DEVICE_FAILURE,"host captured wrong base texture");if(sphere>=0&&GetSphereMapTexture()!=gpu->textures[sphere])fail(MDA9_DEVICE_FAILURE,"host captured wrong sphere texture");++draws;
 };
 for(int remaining=turnCount;remaining>0;--remaining){
  if(sceneEffects()){
   // Match the original MMD loop: decrement first, exposing N ... 1 ... 0.
   // Bookkeeping is explicit so empty geometry still transitions exactly once;
   // storing lastRepeatCount prevents the existing draw hook from repeating it.
   if(repeatCount!=remaining)fail(MDA9_DEVICE_FAILURE,"scene FX changed its render-turn count during drawing");
   --repeatCount;auto* context=mme::g_context;
   if(!context)fail(MDA9_DEVICE_FAILURE,"scene FX host context is unavailable at turn transition");
   if(context->lastRepeatCount!=repeatCount){context->lastRepeatCount=repeatCount;mme::MmePassBookkeeping(context);}
   bridgeCheck();checkSceneFxState();
  }
  drawMedia();
  if(!classic){for(auto k:keys)drawSurface(k);}
  else {
   for(auto k:keys)if(scene.objects[k.first].kind==packet::Accessory&&scene.objects[k.first].signedOrder<0)drawSurface(k);
   drawGround();
   for(auto k:keys)if(scene.objects[k.first].kind==packet::Model)drawSurface(k);
   drawOutlines();
   for(auto k:keys)if(scene.objects[k.first].kind==packet::Accessory&&scene.objects[k.first].signedOrder>=0)drawSurface(k);
  }
  if(sceneEffects()){
   if(repeatCount!=remaining-1)fail(MDA9_DEVICE_FAILURE,"scene FX changed its render-turn count during drawing");
   bridgeCheck();checkSceneFxState();
  }
 }
 // All main render turns have ended, and CURRENT depth still contains this
 // sample. Render only private color here; restore the exact host target/MRTs,
 // viewport and device state before its existing finalization/copy sequence.
 EditorOverlayTarget overlay;draws+=overlay.prepare(d,width,height,editorOverlay);
 clearIdentity();MmeHostPreRenderTargetCopy(d,target);bridgeCheck();checkSceneFxState();captureAccessoryScreen();HRESULT end=MmeHostEndScene(d);started=false;check(end,"host EndScene");bridgeCheck();checkSceneFxState();
 // Raw device calls bypass MmeHost: no scene FX receives these editor pixels,
 // and the already sampled accessory screen texture keeps scene-only pixels.
 draws+=overlay.composite(d,target,width,height);return draws;
}
void extent(uint32_t w,uint32_t h){if(!w||!h||w>MDA9_MAX_EXTENT||h>MDA9_MAX_EXTENT)fail(MDA9_INVALID_ARGUMENT,"capture extent must be 1..4096");}
template<class T> void structure(const T* p){if(!p)fail(MDA9_INVALID_ARGUMENT,"null ABI structure");if(p->size<sizeof(T)||p->version!=MDA9_ABI_VERSION)fail(MDA9_ABI_MISMATCH,"ABI structure size or version mismatch");}
void errorValidate(Mda9ErrorV1* e){if(!e)return;structure(e);if(e->reserved||(!e->utf8&&e->capacity)||e->capacity>SIZE_MAX)fail(MDA9_INVALID_ARGUMENT,"invalid caller error buffer");}
Mda9Status report(Mda9ErrorV1* e,Mda9Status s,const char* text)noexcept {
 if(!e||e->size<sizeof(*e)||e->version!=MDA9_ABI_VERSION)return s;
 if(!text)text="";size_t n=std::strlen(text);e->requiredBytes=n+1;e->status=s;
 if(e->utf8&&e->capacity){size_t count=std::min(n,static_cast<size_t>(e->capacity-1));while(count&&count<n&&(static_cast<unsigned char>(text[count])&0xc0)==0x80)--count;std::memcpy(e->utf8,text,count);e->utf8[count]=0;}return s;
}
template<class Fn> Mda9Status invoke(Mda9ErrorV1* e,Fn&& fn)noexcept {
 try{errorValidate(e);Guard guard;if(!guard.held)return report(e,MDA9_BUSY,"a session API call is already in progress");fn();return report(e,MDA9_OK,"");}
 catch(const Failure& f){return report(e,f.status,f.what());}catch(const std::bad_alloc&){return report(e,MDA9_OUT_OF_MEMORY,"native session allocation failed");}catch(const std::exception&){return report(e,MDA9_INTERNAL_ERROR,"unexpected native session exception");}catch(...){return report(e,MDA9_INTERNAL_ERROR,"unknown native session exception");}
}
Session& session(Mda9Session id){if(!active||!id||active->id!=id)fail(MDA9_INVALID_HANDLE,"invalid or expired session handle");if(active->owner!=GetCurrentThreadId())fail(MDA9_WRONG_THREAD,"session call must run on its creating OS thread");return *active;}
std::vector<Mda9EditorOverlayPrimitiveV1> freezeEditorOverlay(const Mda9PresentV2& c){
 if(c.primitiveCount>MDA9_MAX_EDITOR_OVERLAY_PRIMITIVES||(!c.primitives&&c.primitiveCount))fail(MDA9_INVALID_ARGUMENT,"invalid editor overlay primitive span or count");
 if(c.primitiveCount&&!(c.requiredFeatures&MDA9_FEATURE_EDITOR_OVERLAY))fail(MDA9_UNSUPPORTED,"editor overlay rows require the explicit editor-overlay feature");
 std::vector<Mda9EditorOverlayPrimitiveV1> rows;
 if(c.primitiveCount)rows.assign(c.primitives,c.primitives+static_cast<size_t>(c.primitiveCount));
 // Finish the complete caller-memory copy and validation before resize,
 // geometry publication, host calls or any other live-session mutation.
 for(const auto& row:rows){
  for(float value:row.start)if(!std::isfinite(value))fail(MDA9_INVALID_ARGUMENT,"editor overlay start must be finite");
  for(float value:row.end)if(!std::isfinite(value))fail(MDA9_INVALID_ARGUMENT,"editor overlay end must be finite");
  for(float value:row.color)if(!std::isfinite(value))fail(MDA9_INVALID_ARGUMENT,"editor overlay color must be finite");
  for(float value:row.depth)if(!std::isfinite(value))fail(MDA9_INVALID_ARGUMENT,"editor overlay depth must be finite");
  if(!std::isfinite(row.width)||row.width<=0||!std::isfinite(row.radius)||row.radius<=0)fail(MDA9_INVALID_ARGUMENT,"editor overlay sizes must be positive and finite");
  if(row.shape>MDA9_OVERLAY_WEDGE||(row.flags&~uint32_t(MDA9_OVERLAY_DEPTH)))fail(MDA9_INVALID_ARGUMENT,"invalid editor overlay shape or flags");
  if((row.flags&MDA9_OVERLAY_DEPTH)&&(row.shape!=MDA9_OVERLAY_SEGMENT||row.depth[0]<0||row.depth[0]>1||row.depth[1]<0||row.depth[1]>1))fail(MDA9_INVALID_ARGUMENT,"editor overlay depth requires a segment with both Classic device depths in [0,1]");
 }
 return rows;
}
template<class T> FrozenDraw freezeDraw(const T& c,Session& session){
 if(!c.frameBytes||!c.visualBytes||c.frameLength<208||c.frameLength>packet::detail::MaxFile||c.visualLength<128||c.visualLength>16*1024*1024)fail(MDA9_INVALID_ARGUMENT,"invalid frozen packet byte span");
 FrozenDraw frozen;auto& frame=frozen.frame;auto& appearance=frozen.appearance;
 try{frame=packet::Read(c.frameBytes,static_cast<size_t>(c.frameLength));appearance=visual::Read(c.visualBytes,static_cast<size_t>(c.visualLength),c.frameBytes,static_cast<size_t>(c.frameLength),frame);}catch(const std::bad_alloc&){throw;}catch(const std::exception& ex){throw Failure(MDA9_PACKET_INVALID,ex.what());}
 if(frame.width!=c.width||frame.height!=c.height)fail(MDA9_PACKET_INVALID,"FRM1 extent differs from caller output extent");
 if(frame.clearColor[3]<1.f)fail(MDA9_UNSUPPORTED,"transparent background is unavailable with the current opaque D3D9 host path");
 if(frame.packetVersion<5&&(c.requiredFeatures&uint64_t(MDA9_FEATURE_SHADOW|MDA9_FEATURE_OUTLINE)))fail(MDA9_UNSUPPORTED,"original shadow and outline capabilities require complete FRM1 v5/v6 inputs");
 if(frame.packetVersion==2&&(c.requiredFeatures&MDA9_FEATURE_UV1)==0)fail(MDA9_UNSUPPORTED,"FRM1 v2 requires explicit additional UV capability");
 if(frame.packetVersion>=3){
  const uint64_t features=MDA9_FEATURE_SURFACE|((frame.flags&packet::HasAdditionalUv)?uint64_t(MDA9_FEATURE_UV1):0)|((frame.flags&packet::HasAccessories)?uint64_t(MDA9_FEATURE_ACCESSORY):0)|((frame.flags&packet::HasModelMaterialMetadata)?uint64_t(MDA9_FEATURE_MODEL_MATERIAL_METADATA):0)|((frame.flags&packet::HasClassicPasses)?uint64_t(MDA9_FEATURE_SHADOW|MDA9_FEATURE_OUTLINE):0)|((frame.flags&packet::HasMedia)?uint64_t(MDA9_FEATURE_MEDIA):0);
  if((c.requiredFeatures&uint64_t(MDA9_FEATURE_SURFACE|MDA9_FEATURE_UV1|MDA9_FEATURE_ACCESSORY|MDA9_FEATURE_MODEL_MATERIAL_METADATA|MDA9_FEATURE_SHADOW|MDA9_FEATURE_OUTLINE|MDA9_FEATURE_MEDIA))!=features)fail(MDA9_UNSUPPORTED,"typed FRM1 features must match its surface, metadata, classic pass and media flags");
  if(frame.packetVersion==4&&!session.emmConfigured())fail(MDA9_UNSUPPORTED,"FRM1 v4 model material metadata requires configured EMM scope4");
  if(frame.packetVersion>=5){
   if(!session.emmConfigured())fail(MDA9_UNSUPPORTED,"FRM1 v5/v6 original passes require configured EMM scope4");
   if(c.renderOrder!=MDA9_ORDER_ORIGINAL)fail(MDA9_UNSUPPORTED,"original passes require Original render order");
  }
  if((frame.flags&packet::HasAccessories)&&c.renderOrder!=MDA9_ORDER_ORIGINAL)fail(MDA9_UNSUPPORTED,"classic accessories require Original mixed draw order");
 }else if(c.requiredFeatures&(MDA9_FEATURE_ACCESSORY|MDA9_FEATURE_MODEL_MATERIAL_METADATA|MDA9_FEATURE_MEDIA))fail(MDA9_UNSUPPORTED,"accessory, model material metadata or media capability requires an explicitly typed FRM1 packet");
 if(frame.vertices.size()>UINT_MAX/sizeof(packet::Vertex)||frame.indices.size()>UINT_MAX/4||appearance.sphereNormals.size()>UINT_MAX/12||frame.additionalUvs.size()>UINT_MAX/sizeof(packet::AdditionalUvVertex))fail(MDA9_UNSUPPORTED,"geometry exceeds D3D9 buffer limits");
 for(const auto& object:frame.objects)for(uint32_t local=0;local<object.materialCount;++local){
  const auto& m=frame.materials[object.materialStart+local];
  const bool metadataOnly=(frame.flags&packet::HasModelMaterialMetadata)&&!object.visible;
  if(m.sphereMode==3&&frame.additionalUvChannelCount==0&&!metadataOnly)fail(MDA9_UNSUPPORTED,"SubTexture mode requires explicit UV1");
  // Effect-frame ToonColor is only a cached colour; it cannot reconstruct the
  // fixed renderer's original ramp or its embedded default slot-0 PNG.
  if(frame.packetVersion>=5&&!(frame.classic.flags&packet::EffectFrame)&&object.kind==packet::Model&&object.visible&&(m.flags&2)&&m.indexCount&&m.toonTexture<0)
   fail(MDA9_UNSUPPORTED,"original fixed-function model drawing requires the actual same-sample toon image, including the default toon when selected");
  if(m.accessoryTextureType==3&&(frame.mediaBlockVersion<2||!(frame.flags&packet::HasClassicPasses)))fail(MDA9_UNSUPPORTED,"classic accessory screen requires explicit original screen metadata");
 }
 const float lightLength=frame.lightDirection[0]*frame.lightDirection[0]+frame.lightDirection[1]*frame.lightDirection[1]+frame.lightDirection[2]*frame.lightDirection[2];if(!(lightLength>0)||!std::isfinite(lightLength))fail(MDA9_PACKET_INVALID,"light vector cannot be normalized");
 if(frame.packetVersion>=3){for(const auto& o:frame.objects)if(o.kind==packet::Model)for(size_t i=o.vertexStart;i<size_t(o.vertexStart)+o.vertexCount;++i)std::memcpy(frame.vertices[i].normal,appearance.worldNormals[i].data(),12);}
 else for(size_t i=0;i<frame.vertices.size();++i)std::memcpy(frame.vertices[i].normal,appearance.worldNormals[i].data(),12);
 frozen.materials.reserve(frame.materials.size());
 for(const auto& o:frame.objects)for(uint32_t j=0;j<o.materialCount;++j){
  const auto& raw=frame.materials[o.materialStart+j];D3DMATERIAL9 m{};
  std::memcpy(&m.Diffuse,raw.diffuse,16);std::memcpy(&m.Specular,raw.specular,12);m.Specular.a=1;m.Power=raw.shininess;
  if(o.kind==packet::Accessory){
   m.Ambient=m.Diffuse;std::memcpy(&m.Emissive,raw.emissive,12);m.Emissive.a=1;
   m.Diffuse.r*=.1f;m.Diffuse.g*=.1f;m.Diffuse.b*=.1f;m.Specular.r*=.1f;m.Specular.g*=.1f;m.Specular.b*=.1f;
  }else if(frame.flags&packet::HasClassicPasses){m.Ambient=m.Diffuse;std::memcpy(&m.Emissive,raw.ambient,12);m.Emissive.a=1;}
  else {std::memcpy(&m.Ambient,raw.ambient,12);m.Ambient.a=1;}
  frozen.materials.push_back(m);
 }
 if(session.objectFx){
  try{if(session.objectFx->emm)emmcfg::ValidateFrame(*session.objectFx->emm,frame);else objectfx::ValidateFrame(session.objectFx->rows,frame);}catch(const std::invalid_argument& error){throw Failure(MDA9_PACKET_INVALID,error.what());}catch(const std::runtime_error& error){throw Failure(MDA9_PACKET_INVALID,error.what());}
 }
 frozen.metadata=Metadata(frame,session.id,bool(session.objectFx));return frozen;
}
template<class Request>
void presentFrame(Session& s, const Request* request, Mda9PresentResultV1* result) {
    constexpr bool withOverlay = std::is_same_v<Request, Mda9PresentV2>;
    if (s.mode != Session::Mode::Preview)
        fail(MDA9_UNSUPPORTED, withOverlay ? "PresentV2 cannot be used with a capture session" : "PresentV1 cannot be used with a capture session");
    if (s.poisoned) fail(MDA9_DEVICE_FAILURE, "host state is poisoned; destroy this session before creating another");
    structure(request);
    structure(result);
    extent(request->width, request->height);
    if (request->reserved[0] || request->reserved[1])
        fail(MDA9_INVALID_ARGUMENT, "present reserved fields must be zero");

    const uint64_t required = MDA9_FEATURE_SURFACE | MDA9_FEATURE_PRESENT | s.effectFeatures();
    uint64_t allowed = required | MDA9_FEATURE_UV1 | MDA9_FEATURE_ACCESSORY;
    if (s.emmConfigured())
        allowed |= MDA9_FEATURE_MODEL_MATERIAL_METADATA | MDA9_FEATURE_SHADOW | MDA9_FEATURE_OUTLINE | MDA9_FEATURE_MEDIA;
    if constexpr (withOverlay) allowed |= MDA9_FEATURE_EDITOR_OVERLAY;
    if ((request->requiredFeatures & required) != required || (request->requiredFeatures & ~allowed))
        fail(MDA9_UNSUPPORTED, withOverlay
            ? "PresentV2 features must match SURFACE|PRESENT and the immutable effect scope, with optional typed frame/editor-overlay capabilities"
            : "Present features must match SURFACE|PRESENT and the immutable effect scope, with optional typed frame capabilities");
    if (request->flags != s.configuredScope())
        fail(MDA9_UNSUPPORTED, "present scope does not match the immutable session effect configuration");
    if (request->renderOrder > MDA9_ORDER_OPAQUE_FIRST)
        fail(MDA9_INVALID_ARGUMENT, "unknown render order");

    std::vector<Mda9EditorOverlayPrimitiveV1> overlay;
    if constexpr (withOverlay) overlay = freezeEditorOverlay(*request);
    auto frozen = freezeDraw(*request, s);
    if constexpr (withOverlay) frozen.editorOverlay = std::move(overlay);
    validateTarget(s.borrowedTarget, s.targetThread, s.targetProcess);
    if (s.generation == UINT64_MAX || s.presents == UINT64_MAX)
        fail(MDA9_INTERNAL_ERROR, "present generation space exhausted");
    RecoverOnFailure recovery{s};
    const uint32_t draws = s.render(frozen, request->renderOrder);
    s.present();
    ++s.presents;

    result->width = s.width;
    result->height = s.height;
    result->sourceFrame = s.scene.sourceFrame;
    result->completedPresents = s.presents;
    result->geometryGeneration = s.generation;
    result->readbackBytes = 0;
    result->drawCalls = draws;
    result->flags = s.resultScope() | MDA9_PRESENT_RESULT_API_ACCEPTED | (s.softwareVp ? MDA9_RESULT_SOFTWARE_VERTEX_PROCESSING : 0);
    result->effectDispatches = s.diagnostic.dispatches;
    result->effectPassDraws = s.diagnostic.passDraws;
    result->effectPassFailures = s.diagnostic.passFailures;
    result->rawFallbackDraws = s.diagnostic.fallbacks;
    result->noUiDiagnostics = s.diagnostic.noUi;
    result->reserved = 0;
}

void preparePackageResources(ObjectFxState& next,std::vector<resources::Row>& mappings){
 for(auto& row:mappings){row.origin=resources::Normalize(row.origin);row.local=resources::Normalize(row.local);const auto local=exactAnsi(row.local);if(local.size()>=MAX_PATH)fail(MDA9_UNSUPPORTED,"mapped resource path exceeds pinned MAX_PATH limit");}
 next.resources=std::make_unique<resources::Map>(mappings);
}
void leasePackageResources(ObjectFxState& next,const std::vector<resources::Row>& mappings){
 // All caller memory, shape and identity checks finish before touching local files.
 std::set<std::wstring,resources::OrdinalIgnoreCase> heldDirectories;
 std::set<std::pair<DWORD,uint64_t>> identities;
 uint64_t total=0;
 for(const auto& row:mappings){
  const auto localAnsi=exactAnsi(row.local);if(localAnsi.size()>=MAX_PATH)fail(MDA9_UNSUPPORTED,"mapped resource path exceeds pinned MAX_PATH limit");
  // Origins are identity strings only. Keep local ancestors from being renamed or
  // substituted by a reparse point while compilation/reset may open resources.
  size_t end=3;
  for(;;){
   const std::wstring directory=row.local.substr(0,end);
   if(heldDirectories.insert(directory).second){
    if(heldDirectories.size()>65536)fail(MDA9_UNSUPPORTED,"mapped resource ancestor budget exceeded");
    HANDLE h=CreateFileW(directory.c_str(),FILE_READ_ATTRIBUTES,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_BACKUP_SEMANTICS|FILE_FLAG_OPEN_REPARSE_POINT,nullptr);
    if(h==INVALID_HANDLE_VALUE)fail(MDA9_INVALID_ARGUMENT,"cannot lease mapped resource ancestor");next.leases.emplace_back(h);
    BY_HANDLE_FILE_INFORMATION info{};
    if(!GetFileInformationByHandle(h,&info)||!(info.dwFileAttributes&FILE_ATTRIBUTE_DIRECTORY)||(info.dwFileAttributes&FILE_ATTRIBUTE_REPARSE_POINT))fail(MDA9_INVALID_ARGUMENT,"mapped resource ancestor is not a plain directory");
   }
   end=row.local.find(L'\\',end+1);if(end==std::wstring::npos)break;
  }
  const HANDLE file=CreateFileW(row.local.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT,nullptr);
  if(file==INVALID_HANDLE_VALUE)fail(MDA9_INVALID_ARGUMENT,"mapped resource file is missing, unreadable or mutable");next.leases.emplace_back(file);
  BY_HANDLE_FILE_INFORMATION info{};
  if(!GetFileInformationByHandle(file,&info)||(info.dwFileAttributes&(FILE_ATTRIBUTE_DIRECTORY|FILE_ATTRIBUTE_REPARSE_POINT)))fail(MDA9_INVALID_ARGUMENT,"mapped resource must be a plain file");
  const uint64_t length=(uint64_t(info.nFileSizeHigh)<<32)|info.nFileSizeLow;
  if(length>256ULL*1024*1024||length>512ULL*1024*1024-total)fail(MDA9_UNSUPPORTED,"mapped resource byte budget exceeded");total+=length;
  if(!identities.emplace(info.dwVolumeSerialNumber,(uint64_t(info.nFileIndexHigh)<<32)|info.nFileIndexLow).second)fail(MDA9_INVALID_ARGUMENT,"mapped resource files share physical identity");
 }
}
void publishPackageFx(Session& s,std::unique_ptr<ObjectFxState> next,const std::vector<resources::Row>& mappings){
 std::set<std::string> allowed;
 if(next->sceneEffects){for(const auto& row:mappings)allowed.insert(exactAnsi(row.local));}
 else for(const auto& row:next->rows)if(row.assignment==objectfx::File)allowed.insert(row.path);
 MdaMmeObjectFxCallbacksV1 hook{sizeof hook,MDA_MME_BRIDGE_VERSION,&s,applyObjectFx};
 MdaMmeResourceCallbacksV1 resolver{sizeof resolver,MDA_MME_BRIDGE_VERSION,next.get(),resolveEffectResource};
 const auto bound=next->sceneEffects?MdaMmeBindPackageSceneFxV1(&hook,&resolver):MdaMmeBindPackageObjectFxV1(&hook,&resolver);
 if(bound!=MDA_MME_OK)fail(MDA9_INTERNAL_ERROR,"package FX hooks could not bind transactionally");
 mme::MdaObjectFxAllowedPaths=std::move(allowed);s.objectFx=std::move(next);
}
void configurePackageFx(Session& s,const Mda9ObjectFxConfigurationV2* c,bool sceneEffects){
 if(!c||c->size<sizeof(*c)||c->version!=2)fail(MDA9_ABI_MISMATCH,"package FX configuration requires version2");
 if(s.poisoned)fail(MDA9_DEVICE_FAILURE,"host is poisoned; destroy before configuring");
 if(s.objectFx||s.generation)fail(MDA9_UNSUPPORTED,"package FX must be configured once before the first frame");
 if(c->reserved[0]||c->reserved[1]||c->assignmentCount>65536||c->resourceCount>65536||(!c->assignments&&c->assignmentCount)||(!c->resources&&c->resourceCount))fail(MDA9_INVALID_ARGUMENT,"invalid package FX table span");
 if(sceneEffects&&!c->resourceCount)fail(MDA9_INVALID_ARGUMENT,"scene FX requires a nonempty immutable package resource map");
 auto next=std::make_unique<ObjectFxState>(sceneEffects);next->rows.reserve(static_cast<size_t>(c->assignmentCount));
 std::vector<resources::Row> mappings;mappings.reserve(static_cast<size_t>(c->resourceCount));
 uint64_t copied=0;
 auto copyPath=[&](const char* data,uint64_t length){
  if(!data||!length||length>32767)fail(MDA9_INVALID_ARGUMENT,"invalid package resource UTF8 span");
  copied+=length;if(copied>16*1024*1024)fail(MDA9_UNSUPPORTED,"package FX path budget exceeded");
  std::string path(data,static_cast<size_t>(length));if(path.find('\0')!=std::string::npos)fail(MDA9_INVALID_ARGUMENT,"NUL in package resource path");return path;
 };
 for(uint64_t i=0;i<c->assignmentCount;++i){
  const auto& row=c->assignments[i];structure(&row);
  if(row.reserved[0]||row.reserved[1]||((row.assignment!=MDA9_FX_FILE)&&(row.utf8Path||row.pathLength)))fail(MDA9_INVALID_ARGUMENT,"invalid package FX assignment");
  next->rows.push_back({row.objectId,row.subset,row.assignment,row.assignment==MDA9_FX_FILE?copyPath(row.utf8Path,row.pathLength):std::string()});
 }
 for(uint64_t i=0;i<c->resourceCount;++i){
  const auto& row=c->resources[i];structure(&row);
  if(row.reserved[0]||row.reserved[1])fail(MDA9_INVALID_ARGUMENT,"invalid package resource reserved fields");
  mappings.push_back({wide(copyPath(row.originUtf8,row.originLength)),wide(copyPath(row.materializedUtf8,row.materializedLength))});
 }
 try {
  objectfx::ValidateRows(next->rows);
  preparePackageResources(*next,mappings);
  for(auto& row:next->rows)if(row.assignment==objectfx::File){
   const auto path=resources::Normalize(wide(row.path));
   if(!next->resources->ContainsLocal(path))fail(MDA9_INVALID_ARGUMENT,"FX root is absent from package resource map");
   row.path=exactAnsi(next->resources->Resolve(path,path));if(row.path.size()>=MAX_PATH)fail(MDA9_UNSUPPORTED,"mapped FX path exceeds pinned MAX_PATH limit");
  }
 }catch(const std::invalid_argument& error){throw Failure(MDA9_INVALID_ARGUMENT,error.what());}
 leasePackageResources(*next,mappings);
 publishPackageFx(s,std::move(next),mappings);
}
void configureEmm(Session& s,const Mda9EmmConfigurationV1* c){
 if(s.poisoned)fail(MDA9_DEVICE_FAILURE,"host is poisoned; destroy before configuring");
 if(s.objectFx||s.generation)fail(MDA9_UNSUPPORTED,"EMM must be configured once before the first frame");
 auto next=std::make_unique<ObjectFxState>(true);
 std::vector<resources::Row> mappings;
 try {
  next->emm=std::make_unique<emmcfg::State>(emmcfg::Copy(c));
  mappings.reserve(next->emm->resources.size());
  for(const auto& row:next->emm->resources)mappings.push_back({wide(row.origin),wide(row.local)});
  preparePackageResources(*next,mappings);
  const auto text=[](std::string& value){value=exactAnsi(wide(value));};
  const auto file=[&](std::string& value,bool named){
   if(value.empty()||value=="none"||(named&&(value=="hide"||value=="main_default")))return;
   const auto local=resources::Normalize(wide(value));
   if(!next->resources->ContainsLocal(local))fail(MDA9_INVALID_ARGUMENT,"EMM FILE is absent from package resource local column");
   // Use the map's canonical local spelling, including case, because the
   // host's immutable allowlist and compiled-source cache use exact strings.
   value=exactAnsi(next->resources->Resolve(local,local));if(value.size()>=MAX_PATH)fail(MDA9_UNSUPPORTED,"EMM mapped FX path exceeds pinned MAX_PATH limit");
   next->emmFiles.insert(value);
  };
  // Translate only the detached copy. Scoped literals retain their exact host
  // meaning; ordinary base hide/main_default remain FILE values, not aliases.
  for(auto& section:next->emm->sections){
   text(section.name);text(section.baseName);text(section.ownerKey);text(section.ownerMaterialName);
   file(section.defaultPath,!section.isDefault());
   for(auto& entry:section.entries){text(entry.key);file(entry.effectPath,!section.isDefault());for(auto& row:entry.subsetPaths)file(row.second,!section.isDefault());}
  }
  for(auto& seed:next->emm->rootSeeds)file(seed.second.effectPath,false);
 }catch(const emmcfg::AbiMismatch& error){throw Failure(MDA9_ABI_MISMATCH,error.what());}
 catch(const std::invalid_argument& error){throw Failure(MDA9_INVALID_ARGUMENT,error.what());}
 leasePackageResources(*next,mappings);
 publishPackageFx(s,std::move(next),mappings);
}

}

// The pinned archive's former probe seams feed per-session diagnostics. They
// neither show MessageBox UI nor emit misleading GPU metrics.
extern "C" int WINAPI MdaProbeMessageBoxA(HWND,LPCSTR message,LPCSTR,UINT){if(callbacks){++callbacks->diagnostic.noUi;std::snprintf(callbacks->diagnostic.message,sizeof callbacks->diagnostic.message,"MME no-UI diagnostic (host failure)");(void)message;}return IDOK;}
extern "C" void MdaProbeEffectDispatch(){if(callbacks)++callbacks->diagnostic.dispatches;}
extern "C" void MdaProbeEffectPassResult(long result,int){if(callbacks){if(result<0)++callbacks->diagnostic.passFailures;else ++callbacks->diagnostic.passDraws;}}
extern "C" void MdaProbeFallbackDraw(){if(callbacks)++callbacks->diagnostic.fallbacks;}

extern "C" {
float ExpGetFrameTime(){return callbacks?callbacks->scene.frameSeconds:0;}
int ExpGetPmdNum(){return callbacks?static_cast<int>(callbacks->metadata.models.size()):0;}
char* ExpGetPmdFilename(int i){const int n=callbacks?callbacks->typedIndex(packet::Model,i):-1;return n>=0?const_cast<char*>(callbacks->metadata.ansiPaths[n].c_str()):nullptr;}
int ExpGetPmdOrder(int i){const auto* o=callbacks?callbacks->object(packet::Model,i):nullptr;return o?o->signedOrder:0;}
int ExpGetPmdMatNum(int i){const auto* o=callbacks?callbacks->object(packet::Model,i):nullptr;return o?static_cast<int>(o->materialCount):0;}
D3DMATERIAL9 ExpGetPmdMaterial(int i,int j){return callbacks?callbacks->material(callbacks->typedIndex(packet::Model,i),j):D3DMATERIAL9{};}
int ExpGetPmdBoneNum(int i){const auto* o=callbacks?callbacks->object(packet::Model,i):nullptr;return o?static_cast<int>(o->bones.size()):0;}
char* ExpGetPmdBoneName(int i,int j){const int n=callbacks?callbacks->typedIndex(packet::Model,i):-1;return n>=0&&j>=0&&size_t(j)<callbacks->metadata.bones[n].size()?const_cast<char*>(callbacks->metadata.bones[n][j].c_str()):nullptr;}
D3DMATRIX ExpGetPmdBoneWorldMat(int i,int j){const auto* o=callbacks?callbacks->object(packet::Model,i):nullptr;return o&&j>=0&&size_t(j)<o->bones.size()?matrix(o->bones[j].world):identity();}
int ExpGetPmdMorphNum(int i){const auto* o=callbacks?callbacks->object(packet::Model,i):nullptr;return o?static_cast<int>(o->morphs.size()):0;}
char* ExpGetPmdMorphName(int i,int j){const int n=callbacks?callbacks->typedIndex(packet::Model,i):-1;return n>=0&&j>=0&&size_t(j)<callbacks->metadata.morphs[n].size()?const_cast<char*>(callbacks->metadata.morphs[n][j].c_str()):nullptr;}
float ExpGetPmdMorphValue(int i,int j){const auto* o=callbacks?callbacks->object(packet::Model,i):nullptr;return o&&j>=0&&size_t(j)<o->morphs.size()?o->morphs[j].weight:0;}
bool ExpGetPmdDisp(int i){const auto* o=callbacks?callbacks->object(packet::Model,i):nullptr;return o&&o->visible!=0;}
void* ExpGetPmdID(int i){const auto* o=callbacks?callbacks->object(packet::Model,i):nullptr;return o?reinterpret_cast<void*>(static_cast<uintptr_t>(o->id)):nullptr;}
int ExpGetAcsNum(){return callbacks?static_cast<int>(callbacks->metadata.accessories.size()):0;}
int ExpGetPreAcsNum(){return callbacks?callbacks->scene.rawPreAccessorySplit:0;}
char* ExpGetAcsFilename(int i){const int n=callbacks?callbacks->typedIndex(packet::Accessory,i):-1;return n>=0?const_cast<char*>(callbacks->metadata.ansiPaths[n].c_str()):nullptr;}
int ExpGetAcsOrder(int i){const auto* o=callbacks?callbacks->object(packet::Accessory,i):nullptr;return o?o->signedOrder:0;}
D3DMATRIX ExpGetAcsWorldMat(int i){const auto* o=callbacks?callbacks->object(packet::Accessory,i):nullptr;return o?matrix(o->world):identity();}
float ExpGetAcsX(int i){const auto* o=callbacks?callbacks->object(packet::Accessory,i):nullptr;return o?o->translation[0]:0;}
float ExpGetAcsY(int i){const auto* o=callbacks?callbacks->object(packet::Accessory,i):nullptr;return o?o->translation[1]:0;}
float ExpGetAcsZ(int i){const auto* o=callbacks?callbacks->object(packet::Accessory,i):nullptr;return o?o->translation[2]:0;}
float ExpGetAcsRx(int i){const auto* o=callbacks?callbacks->object(packet::Accessory,i):nullptr;return o?o->rotation[0]:0;}
float ExpGetAcsRy(int i){const auto* o=callbacks?callbacks->object(packet::Accessory,i):nullptr;return o?o->rotation[1]:0;}
float ExpGetAcsRz(int i){const auto* o=callbacks?callbacks->object(packet::Accessory,i):nullptr;return o?o->rotation[2]:0;}
float ExpGetAcsSi(int i){const auto* o=callbacks?callbacks->object(packet::Accessory,i):nullptr;return o?o->rawScale*10.f:1;}
float ExpGetAcsTr(int i){const auto* o=callbacks?callbacks->object(packet::Accessory,i):nullptr;return o?o->opacity:1;}
bool ExpGetAcsDisp(int i){const auto* o=callbacks?callbacks->object(packet::Accessory,i):nullptr;return o&&o->visible!=0;}
void* ExpGetAcsID(int i){const auto* o=callbacks?callbacks->object(packet::Accessory,i):nullptr;return o?reinterpret_cast<void*>(static_cast<uintptr_t>(o->id)):nullptr;}
int ExpGetAcsMatNum(int i){const auto* o=callbacks?callbacks->object(packet::Accessory,i):nullptr;return o?static_cast<int>(o->materialCount):0;}
D3DMATERIAL9 ExpGetAcsMaterial(int i,int j){return callbacks?callbacks->material(callbacks->typedIndex(packet::Accessory,i),j):D3DMATERIAL9{};}
int ExpGetCurrentObject(){return callbacks?callbacks->currentObject:0;}int ExpGetCurrentMaterial(){return callbacks?callbacks->currentMaterial:0;}int ExpGetCurrentTechnic(){return callbacks?callbacks->currentTechnic:1;}void ExpSetRenderRepeatCount(int n){if(callbacks)callbacks->repeatCount=n;}int ExpGetRenderRepeatCount(){return callbacks?callbacks->repeatCount:0;}bool ExpGetEnglishMode(){return false;}

Mda9Status MDA9_CALL MdaD3D9QueryAbiV1(Mda9AbiV1* out,Mda9ErrorV1* e){return invoke(e,[&]{structure(out);out->abiVersion=1;out->pointerBits=64;out->maxWidth=out->maxHeight=MDA9_MAX_EXTENT;out->supportedFeatures=MDA9_FEATURE_SURFACE|MDA9_FEATURE_UV1|MDA9_FEATURE_SHADOW|MDA9_FEATURE_OUTLINE|MDA9_FEATURE_ACCESSORY|MDA9_FEATURE_PRESENT|MDA9_FEATURE_OBJECT_FX|MDA9_FEATURE_EFFECT_RESOURCES|MDA9_FEATURE_SCENE_EFFECTS|MDA9_FEATURE_MODEL_MATERIAL_METADATA|MDA9_FEATURE_EMM_CONFIGURATION|MDA9_FEATURE_MEDIA|MDA9_FEATURE_EDITOR_OVERLAY;out->reserved[0]=out->reserved[1]=0;});}
Mda9Status MDA9_CALL MdaD3D9CreateV1(const Mda9CreateV1* c,Mda9Session* out,Mda9ErrorV1* e){if(out)*out=0;return invoke(e,[&]{if(!out)fail(MDA9_INVALID_ARGUMENT,"null session handle result");structure(c);extent(c->width,c->height);if(c->reserved)fail(MDA9_INVALID_ARGUMENT,"create reserved field must be zero");if(active)fail(MDA9_BUSY,"one live session per DLL is supported");if(!nextId)fail(MDA9_INTERNAL_ERROR,"session handle space exhausted");auto s=std::make_unique<Session>(nextId++);s->initialize(*c);*out=s->id;active=std::move(s);});}
Mda9Status MDA9_CALL MdaD3D9CreatePreviewV1(const Mda9CreatePreviewV1* c,Mda9Session* out,Mda9ErrorV1* e){if(out)*out=0;return invoke(e,[&]{
 if(!out)fail(MDA9_INVALID_ARGUMENT,"null session handle result");structure(c);extent(c->width,c->height);
 if(c->flags||c->reserved[0]||c->reserved[1]||c->reserved[2]||c->reserved[3])fail(MDA9_INVALID_ARGUMENT,"preview create flags and reserved fields must be zero");
 const HWND target=reinterpret_cast<HWND>(static_cast<uintptr_t>(c->targetHwnd));const DWORD thread=validateTarget(target);
 if(active)fail(MDA9_BUSY,"one live session per DLL is supported");if(!nextId)fail(MDA9_INTERNAL_ERROR,"session handle space exhausted");
 auto s=std::make_unique<Session>(nextId++,Session::Mode::Preview,target,thread,GetCurrentProcessId());
 const Mda9CreateV1 common{sizeof(Mda9CreateV1),MDA9_ABI_VERSION,c->width,c->height,c->adapterOrdinal,0};s->initialize(common);*out=s->id;active=std::move(s);
 });}
Mda9Status MDA9_CALL MdaD3D9ResizeV1(Mda9Session id,const Mda9ResizeV1* c,Mda9ErrorV1* e){return invoke(e,[&]{auto& s=session(id);if(s.poisoned)fail(MDA9_DEVICE_FAILURE,"host state is poisoned; destroy this session before creating another");structure(c);extent(c->width,c->height);if(s.mode==Session::Mode::Preview)validateTarget(s.borrowedTarget,s.targetThread,s.targetProcess);s.resize(c->width,c->height);});}
Mda9Status MDA9_CALL MdaD3D9DestroyV1(Mda9Session id,Mda9ErrorV1* e){return invoke(e,[&]{session(id);active.reset();});}
Mda9Status MDA9_CALL MdaD3D9ConfigureObjectFxV1(Mda9Session id,const Mda9ObjectFxConfigurationV1* c,Mda9ErrorV1* e){return invoke(e,[&]{
 auto& s=session(id);structure(c);
 if(s.poisoned)fail(MDA9_DEVICE_FAILURE,"host state is poisoned; destroy this session before creating another");
 if(s.objectFx||s.generation)fail(MDA9_UNSUPPORTED,"object FX must be configured exactly once before the first frame");
 if(c->reserved[0]||c->reserved[1]||c->assignmentCount>65536||(!c->assignments&&c->assignmentCount))fail(MDA9_INVALID_ARGUMENT,"invalid object FX assignment span or reserved fields");
 auto next=std::make_unique<ObjectFxState>();next->rows.reserve(static_cast<size_t>(c->assignmentCount));next->leases.reserve(static_cast<size_t>(c->assignmentCount));uint64_t copied=0;
 // Complete the caller-memory copy and pure shape validation before path I/O,
 // host-policy publication or any subsequent frame/device work.
 for(uint64_t i=0;i<c->assignmentCount;++i){
  const auto& row=c->assignments[i];structure(&row);
  if(row.reserved[0]||row.reserved[1]||row.pathLength>32767||(!row.utf8Path&&row.pathLength))fail(MDA9_INVALID_ARGUMENT,"invalid object FX path span or reserved fields");
  if(row.assignment!=MDA9_FX_FILE&&(row.utf8Path||row.pathLength))fail(MDA9_INVALID_ARGUMENT,"only FILE assignments may carry a path");
  copied+=row.pathLength;if(copied>16*1024*1024)fail(MDA9_UNSUPPORTED,"object FX copied path budget exceeded");
  next->rows.push_back({row.objectId,row.subset,row.assignment,row.pathLength?std::string(row.utf8Path,static_cast<size_t>(row.pathLength)):std::string()});
 }
 try{objectfx::ValidateRows(next->rows);}catch(const std::runtime_error& error){throw Failure(MDA9_INVALID_ARGUMENT,error.what());}
 std::set<std::string> allowed;
 for(auto& row:next->rows)if(row.assignment==objectfx::File){
  const auto original=wide(row.path);std::wstring full(32768,L'\0');
  const DWORD count=GetFullPathNameW(original.c_str(),static_cast<DWORD>(full.size()),full.data(),nullptr);
  if(!count||count>=full.size())fail(MDA9_INVALID_ARGUMENT,"object FX absolute path normalization failed");full.resize(count);
  row.path=exactAnsi(full);
  if(row.path.size()>=MAX_PATH)fail(MDA9_UNSUPPORTED,"object FX path exceeds the pinned loader's MAX_PATH contract");
  if(allowed.insert(row.path).second){
   const HANDLE file=CreateFileW(full.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
   if(file==INVALID_HANDLE_VALUE)fail(MDA9_INVALID_ARGUMENT,"object FX file is missing, unreadable or not available for an immutable read-share lease");
   next->leases.emplace_back(file);BY_HANDLE_FILE_INFORMATION information{};
   if(!GetFileInformationByHandle(file,&information)||(information.dwFileAttributes&FILE_ATTRIBUTE_DIRECTORY))fail(MDA9_INVALID_ARGUMENT,"object FX path is not a regular readable file");
  }
 }
 // No failing allocation remains after the separate hook is bound. Invalid
 // requests leave the current session/configuration choice unconsumed.
 mme::MdaObjectFxAllowedPaths=std::move(allowed);
 MdaMmeObjectFxCallbacksV1 hook{sizeof hook,MDA_MME_BRIDGE_VERSION,&s,applyObjectFx};
 if(MdaMmeBindObjectFxV1(&hook)!=MDA_MME_OK){mme::MdaObjectFxAllowedPaths.clear();fail(MDA9_INTERNAL_ERROR,"object FX frame-hook binding failed");}
 s.objectFx=std::move(next);
 });}
Mda9Status MDA9_CALL MdaD3D9ConfigureObjectFxV2(Mda9Session id,const Mda9ObjectFxConfigurationV2* c,Mda9ErrorV1* e){return invoke(e,[&]{configurePackageFx(session(id),c,false);});}
Mda9Status MDA9_CALL MdaD3D9ConfigureSceneFxV1(Mda9Session id,const Mda9ObjectFxConfigurationV2* c,Mda9ErrorV1* e){return invoke(e,[&]{configurePackageFx(session(id),c,true);});}
Mda9Status MDA9_CALL MdaD3D9ConfigureEmmV1(Mda9Session id,const Mda9EmmConfigurationV1* c,Mda9ErrorV1* e){return invoke(e,[&]{configureEmm(session(id),c);});}
Mda9Status MDA9_CALL MdaD3D9CaptureV1(Mda9Session id,const Mda9CaptureV1* c,Mda9CaptureResultV1* out,Mda9ErrorV1* e){return invoke(e,[&]{
 auto& s=session(id);if(s.mode!=Session::Mode::Capture)fail(MDA9_UNSUPPORTED,"CaptureV1 cannot be used with a preview session");if(s.poisoned)fail(MDA9_DEVICE_FAILURE,"host state is poisoned; destroy this session before creating another");structure(c);structure(out);extent(c->width,c->height);
 if(s.objectFx){
  const uint64_t required=MDA9_FEATURE_SURFACE|s.effectFeatures();
  if((c->requiredFeatures&required)!=required||(c->requiredFeatures&~uint64_t(required|MDA9_FEATURE_UV1|MDA9_FEATURE_ACCESSORY|(s.emmConfigured()?uint64_t(MDA9_FEATURE_MODEL_MATERIAL_METADATA|MDA9_FEATURE_SHADOW|MDA9_FEATURE_OUTLINE|MDA9_FEATURE_MEDIA):0)))||c->flags!=s.configuredScope())
   fail(MDA9_UNSUPPORTED,s.emmConfigured()?"configured EMM capture requires scope4 and SURFACE|OBJECT_FX|SCENE_EFFECTS|EMM_CONFIGURATION with optional typed UV1/accessory/metadata, FRM5 original passes or FRM6 media":s.sceneEffects()?"configured scene FX capture requires scope3 and SURFACE|OBJECT_FX|SCENE_EFFECTS with optional UV1/ACCESSORY":"configured object FX capture requires scope2 and SURFACE|OBJECT_FX with optional UV1/ACCESSORY");
 }else{
  if(c->requiredFeatures&~uint64_t(MDA9_FEATURE_SURFACE|MDA9_FEATURE_UV1|MDA9_FEATURE_ACCESSORY))fail(MDA9_UNSUPPORTED,"requested shadow/outline/MME/EMM or unknown feature is unsupported by isolated surface capture");
  if(c->flags!=MDA9_CAPTURE_ISOLATED_SURFACE)fail(MDA9_UNSUPPORTED,"capture requires explicit isolated ordinary-surface scope; real MME metadata is unavailable");
 }
 if(c->renderOrder>MDA9_ORDER_OPAQUE_FIRST)fail(MDA9_INVALID_ARGUMENT,"unknown render order");
 if(!c->frameBytes||!c->visualBytes||c->frameLength<208||c->frameLength>packet::detail::MaxFile||c->visualLength<128||c->visualLength>16*1024*1024)fail(MDA9_INVALID_ARGUMENT,"invalid frozen packet byte span");
 if(!c->rgba||c->rgbaStride<uint64_t(c->width)*4||c->rgbaStride>SIZE_MAX||uint64_t(c->height-1)>(UINT64_MAX-uint64_t(c->width)*4)/c->rgbaStride)fail(MDA9_INVALID_ARGUMENT,"invalid caller RGBA pointer or stride");
 const uint64_t required=uint64_t(c->height-1)*c->rgbaStride+uint64_t(c->width)*4;if(required>SIZE_MAX||c->rgbaCapacity<required)fail(MDA9_BUFFER_TOO_SMALL,"caller RGBA capacity is too small");
 auto frozen=freezeDraw(*c,s);if(s.generation==UINT64_MAX||s.captures==UINT64_MAX)fail(MDA9_INTERNAL_ERROR,"capture generation space exhausted");
 RecoverOnFailure recovery{s};const uint32_t draws=s.render(frozen,c->renderOrder);
 check(s.device->GetRenderTargetData(s.target,s.readback),"GetRenderTargetData");D3DLOCKED_RECT r{};check(s.readback->LockRect(&r,nullptr,D3DLOCK_READONLY),"readback lock");
 for(uint32_t y=0;y<s.height;++y){auto src=static_cast<const uint8_t*>(r.pBits)+size_t(y)*r.Pitch;auto dst=c->rgba+size_t(y)*c->rgbaStride;for(uint32_t x=0;x<s.width;++x){dst[4*x]=src[4*x+2];dst[4*x+1]=src[4*x+1];dst[4*x+2]=src[4*x];dst[4*x+3]=src[4*x+3];}}
 check(s.readback->UnlockRect(),"readback unlock");++s.captures;
 out->width=s.width;out->height=s.height;out->sourceFrame=s.scene.sourceFrame;out->completedCaptures=s.captures;out->geometryGeneration=s.generation;out->rgbaBytesRequired=required;out->drawCalls=draws;out->flags=s.resultScope()|(s.softwareVp?MDA9_RESULT_SOFTWARE_VERTEX_PROCESSING:0);out->effectDispatches=s.diagnostic.dispatches;out->effectPassDraws=s.diagnostic.passDraws;out->effectPassFailures=s.diagnostic.passFailures;out->rawFallbackDraws=s.diagnostic.fallbacks;out->noUiDiagnostics=s.diagnostic.noUi;out->reserved=0;
 });}
Mda9Status MDA9_CALL MdaD3D9PresentV1(Mda9Session id, const Mda9PresentV1* request, Mda9PresentResultV1* result, Mda9ErrorV1* error) {
    return invoke(error, [&] { presentFrame(session(id), request, result); });
}
Mda9Status MDA9_CALL MdaD3D9PresentV2(Mda9Session id, const Mda9PresentV2* request, Mda9PresentResultV1* result, Mda9ErrorV1* error) {
    return invoke(error, [&] { presentFrame(session(id), request, result); });
}
}
