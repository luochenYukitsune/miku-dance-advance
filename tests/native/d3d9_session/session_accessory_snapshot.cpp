// CPU-only adapter admission/getter checks. Link the real pinned host archive
// and D3DX imports exactly as Session; no device/window/runtime is initialized.
#define main frameAccessoryCpuMain
#include "frame_accessory_parser.cpp"
#undef main
#include "../../../native/Mmd.Modern.D3D9.Compatibility/session.cpp"
namespace {
Bytes snapshotVisual(const Fixture& f){
 Bytes b;for(char c:std::string("MDA9VIS1"))b.push_back(c);u32(b,4);u32(b,1);u32(b,3);
 const auto digest=visual::hash(f.b.data(),f.b.size());b.insert(b.end(),digest.begin(),digest.end());
 for(float x:{0.f,0.f,5.f})f32(b,x);mat(b);
 for(float x:{.4f,.5f,.6f,10.f,10.f,10.f,.7f,.8f,.9f})f32(b,x);
 u64(b,Accessory);u32(b,0);u32(b,1);
 for(int i=0;i<12;++i)f32(b,float(i)*.1f);f32(b,0);
 for(int i=0;i<16;++i)f32(b,float(i)*.01f);
 for(float x:{.8f,.6f,.4f,1.f})f32(b,x);
 for(int i=0;i<18;++i)f32(b,float(i)+.5f);return b;
}
void publish(Session& s,FrozenDraw&& f){s.scene=std::move(f.frame);s.metadata=std::move(f.metadata);s.materials=std::move(f.materials);}
}
int main(){
 try {
  auto f=make();auto v=snapshotVisual(f);Session s(1);
  Mda9CaptureV1 c{};c.frameBytes=f.b.data();c.frameLength=f.b.size();c.visualBytes=v.data();c.visualLength=v.size();c.requiredFeatures=MDA9_FEATURE_SURFACE|MDA9_FEATURE_ACCESSORY;c.renderOrder=MDA9_ORDER_ORIGINAL;c.width=c.height=8;
  auto frozen=freezeDraw(c,s);
  std::vector<packet::Object> mixed;
  for(const auto row:std::vector<std::pair<uint32_t,int32_t>>{{packet::Accessory,1},{packet::Model,3},{packet::Accessory,-2},{packet::Model,-1},{packet::Accessory,-1},{packet::Model,3},{packet::Model,0},{packet::Accessory,2},{packet::Model,-1}}){packet::Object o{};o.id=mixed.size()+1;o.kind=row.first;o.signedOrder=row.second;mixed.push_back(o);}
  std::stable_sort(mixed.begin(),mixed.end(),originalDrawBefore);
  const std::vector<uint64_t> expected{5,3,4,9,7,2,6,1,8};std::vector<uint64_t> actual;for(const auto& o:mixed)actual.push_back(o.id);
  check(actual==expected,"Original draw uses negative ACS/model/positive ACS groups, exact signed model order and stable ties");
  check(frozen.frame.vertices[0].normal[2]==1.f,"accessory VB preserves source-local normals");
  check(frozen.metadata.models.size()==1&&frozen.metadata.accessories.size()==1,"separate source-order typed tables");
  const auto stored=frozen.materials[0];
  check(stored.Diffuse.r==.8f*.1f&&stored.Diffuse.a==.4f&&stored.Ambient.r==.8f&&stored.Specular.b==.1f*.1f&&stored.Emissive.b==.07f&&stored.Power==12.f,"one raw-to-stored material conversion, no opacity");
  publish(s,std::move(frozen));callbacks=&s;
  check(ExpGetPmdNum()==1&&ExpGetAcsNum()==1,"getters enumerate true kinds only");
  check(uint64_t(reinterpret_cast<uintptr_t>(ExpGetPmdID(0)))==Model&&uint64_t(reinterpret_cast<uintptr_t>(ExpGetAcsID(0)))==Accessory,"getter IDs retain high64 bits");
  check(ExpGetPreAcsNum()==17&&ExpGetPmdOrder(0)==2&&ExpGetAcsOrder(0)==-1,"explicit signed orders and unclamped raw split");
  s.scene.objects[0].signedOrder=0;check(ExpGetPmdOrder(0)==0,"model zero getter remains the original sentinel value");s.scene.objects[0].signedOrder=-1;check(ExpGetPmdOrder(0)==-1,"model negative getter remains exact");
  check(ExpGetAcsX(0)==.2f&&ExpGetAcsRx(0)==7.f&&ExpGetAcsSi(0)==.23f*10.f&&ExpGetAcsTr(0)==.6f,"raw pseudo-bone channels retain units");
  check(!ExpGetPmdDisp(0)&&ExpGetPmdBoneNum(0)==1&&ExpGetAcsMatNum(0)==1&&ExpGetAcsDisp(0),"hidden parent bone metadata and accessory materials");
  const auto getter=ExpGetAcsMaterial(0,0);check(getter.Diffuse.a==.4f&&getter.Emissive.b==.07f,"material getter is stored state before draw opacity");
  check(std::wstring(modelPath(0)).find(L".pmx")!=std::wstring::npos&&std::wstring(accessoryPath(0)).find(L".x")!=std::wstring::npos,"isolated kind-correct filename namespaces");
  MdaMmeParentV1 p{sizeof p,MDA_MME_BRIDGE_VERSION};
  check(parent(&s,Accessory,&p)==0&&p.attached==1&&p.model_id==Model&&p.bone_index==0,"checked parent callback includes hidden high64 model");
  check(parent(&s,Model,&p)!=0&&parent(&s,Accessory+1,&p)!=0,"parent callback rejects wrong kind and unknown ID");
  auto& a=s.scene.objects[1];a.attached=0;a.parentModelId=0;a.parentBoneIndex=-1;
  check(parent(&s,Accessory,&p)==0&&p.attached==0&&p.model_id==0&&p.bone_index==-1,"known unattached callback succeeds explicitly");
  s.generation=23;s.gpu=std::make_unique<FrameGpu>();
  // Never dereferenced/AddRef'd: this only exercises adapter identity admission.
  // The separate real-host/GPU checks own borrowed-COM lifetime validation.
  s.gpu->vertices.p=reinterpret_cast<IDirect3DVertexBuffer9*>(uintptr_t(0x1234));
  s.geometryToken.p=reinterpret_cast<IDirect3DVertexBuffer9*>(uintptr_t(0x5678));
  // Declared after `s`, so it runs first on every exit, including a throwing check: the fake pointers must never reach
  // ~Session's Release, which would crash before the UNEXPECTED handler reports the failure.
  struct FakeComGuard{Session& s;~FakeComGuard(){if(s.gpu)s.gpu->vertices.p=nullptr;s.geometryToken.p=nullptr;}} fakeComGuard{s};
  MdaMmeGeometryV1 g{sizeof g,MDA_MME_BRIDGE_VERSION};
  check(geometry(&s,MDA_MME_ACCESSORY,Accessory,&g)!=0,"geometry callback rejects an object without a published identity");
  // Identity is per object and stays stable across frames until that object's own layout changes, so the host
  // keeps its cached object instead of rebuilding every model each frame. The non-null token above means the
  // production refresh never touches the device here.
  s.refreshObjectIdentities();const uint64_t first=s.objectIdentities.at(Accessory).token;
  const uint64_t firstModel=s.objectIdentities.at(Model).token;
  check(geometry(&s,MDA_MME_ACCESSORY,Accessory,&g)==0&&g.identity==first&&g.borrowed_iunknown==s.geometryToken.p,"geometry callback returns the object identity and the stable session token");
  s.generation=24;s.gpu->vertices.p=reinterpret_cast<IDirect3DVertexBuffer9*>(uintptr_t(0x9abc));
  s.refreshObjectIdentities();
  check(s.objectIdentities.at(Accessory).token==first&&s.objectIdentities.at(Model).token==firstModel,"refresh keeps every unchanged object's token on the next frame");
  check(geometry(&s,MDA_MME_ACCESSORY,Accessory,&g)==0&&g.identity==first&&g.borrowed_iunknown==s.geometryToken.p,"a new frame and vertex buffer keep the same host identity");
  auto& moved=s.scene.objects[1];const uint32_t originalStart=moved.vertexStart;++moved.vertexStart;s.refreshObjectIdentities();
  const uint64_t relaid=s.objectIdentities.at(Accessory).token;
  check(relaid!=first&&s.objectIdentities.at(Model).token==firstModel,"a layout change renews only that object's token");
  moved.vertexStart=originalStart;
  const auto removed=s.scene.objects[1];s.scene.objects.erase(s.scene.objects.begin()+1);s.refreshObjectIdentities();
  check(!s.objectIdentities.count(Accessory),"a removed object loses its identity");
  s.scene.objects.insert(s.scene.objects.begin()+1,removed);s.refreshObjectIdentities();
  check(s.objectIdentities.at(Accessory).token!=first&&s.objectIdentities.at(Accessory).token!=relaid,"re-adding an object issues a new token");
  check(geometry(&s,MDA_MME_MODEL,Accessory,&g)!=0&&geometry(&s,9,Accessory,&g)!=0&&geometry(&s,MDA_MME_ACCESSORY,Accessory+1,&g)!=0,"geometry callback rejects ID-kind mismatch");
  s.gpu->vertices.p=nullptr;s.geometryToken.p=nullptr;s.objectIdentities.clear();
  c.requiredFeatures=MDA9_FEATURE_SURFACE;try{freezeDraw(c,s);check(false,"missing accessory capability");}catch(const Failure& e){check(e.status==MDA9_UNSUPPORTED,"missing accessory capability");}
  c.requiredFeatures=MDA9_FEATURE_SURFACE|MDA9_FEATURE_ACCESSORY;c.renderOrder=MDA9_ORDER_OPAQUE_FIRST;try{freezeDraw(c,s);check(false,"opaque-first mixed scene");}catch(const Failure& e){check(e.status==MDA9_UNSUPPORTED,"opaque-first mixed scene");}
  callbacks=nullptr;
 }catch(const std::exception& e){callbacks=nullptr;++failures;std::cerr<<"UNEXPECTED "<<e.what()<<'\n';}
 std::cout<<"CPU accessory Session snapshot checks="<<checks<<" failures="<<failures<<'\n';return failures?1:0;
}
