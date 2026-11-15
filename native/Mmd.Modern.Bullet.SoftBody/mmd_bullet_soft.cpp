// Project bridge; PMX field mapping references nanoem/ext/physics_bullet.cc
// 30acffaa29f5d2eb9e997d69418f2e4b97b5894f (MIT), LICENSE.Nanoem.txt.
// Bullet upstream files are unchanged and retain their zlib notices.
#include <btBulletDynamicsCommon.h>
#include <BulletSoftBody/btSoftBody.h>
#include <BulletSoftBody/btSoftBodyHelpers.h>
#include <BulletSoftBody/btSoftBodyRigidBodyCollisionConfiguration.h>
#include <BulletSoftBody/btSoftRigidDynamicsWorld.h>
#include "mmd_soft_plane_collision.h"
#include "mmd_soft_attachment.h"
#include <algorithm>
#include <memory>
#include <vector>
#include <utility>
#include <stdexcept>
#include <cmath>
#include <cstdio>
#include <set>
#include <limits>
#ifdef _WIN32
#define API extern "C" __declspec(dllexport)
#else
#define API extern "C" __attribute__((visibility("default")))
#endif
static_assert(sizeof(btScalar) == 4, "float32 ABI required");
namespace {
thread_local char message[512] = {};
template<class F> int guard(F f) noexcept { message[0]=0; try { f(); return 0; }
 catch(const std::exception& e){std::snprintf(message,sizeof(message),"%s",e.what());}
 catch(...){std::snprintf(message,sizeof(message),"Unknown soft-body failure");} return 1; }
btVector3 vec(const float* p){return btVector3(p[0],p[1],p[2]);}
// Soft-body collision quantizes node positions into btSparseSdf cells with an (int) conversion. Finite but huge
// coordinates (about 1.6e9 and above) overflow it and index far outside the cell table, crashing the process.
// MMD models live within a few thousand units; 1e6 leaves ample room while keeping float precision useful.
constexpr float MaxSoftCoordinate=1e6f;
void requirePoints(const float* points,long long values,const char* what){
 for(long long i=0;i<values;i++)if(!std::isfinite(points[i])||std::fabs(points[i])>MaxSoftCoordinate)throw std::invalid_argument(what);}
btTransform pose(const float* p){return btTransform(btQuaternion(p[3],p[4],p[5],p[6]),vec(p));}
struct Rigid { std::unique_ptr<btCollisionShape> shape; std::unique_ptr<btRigidBody> body; };
struct World {
 std::unique_ptr<btSoftBodyRigidBodyCollisionConfiguration> config;
 std::unique_ptr<btCollisionDispatcher> dispatcher;
 std::unique_ptr<btDbvtBroadphase> broad;
 std::unique_ptr<btSequentialImpulseConstraintSolver> solver;
 std::unique_ptr<btSoftRigidDynamicsWorld> ownedWorld;
 btSoftRigidDynamicsWorld* world = nullptr;
 bool borrowed = false, frozen = false, frozenSurfaceProjected = false;
 std::vector<btRigidBody*> rigidBodies;
 std::vector<int> authoredModes;
 struct Near { int soft, node, rigid, ordinal; };
 std::vector<Near> orderedNear;
 // Bullet 2.75 btAlignedObjectArray has a deep copy constructor but a shallow
 // implicit assignment. Never assign a Cluster: its node/mass/reference arrays
 // belong exclusively to the live btSoftBody. Snapshot numerical history only.
 struct ClusterHistory {
  btVector3 center;btTransform frame;
  btScalar inverseMass;btMatrix3x3 inverseWorldInertia;btVector3 linearVelocity,angularVelocity;
  btVector3 velocityImpulses[2],driftImpulses[2];int velocityCount,driftCount;
  explicit ClusterHistory(const btSoftBody::Cluster& c):center(c.m_com),frame(c.m_framexform),inverseMass(c.m_imass),inverseWorldInertia(c.m_invwi),linearVelocity(c.m_lv),angularVelocity(c.m_av),velocityCount(c.m_nvimpulses),driftCount(c.m_ndimpulses){
   for(int j=0;j<2;j++){velocityImpulses[j]=c.m_vimpulses[j];driftImpulses[j]=c.m_dimpulses[j];}
  }
  void restore(btSoftBody::Cluster& c)const{
   c.m_com=center;c.m_framexform=frame;
   c.m_imass=inverseMass;c.m_invwi=inverseWorldInertia;c.m_lv=linearVelocity;c.m_av=angularVelocity;c.m_nvimpulses=velocityCount;c.m_ndimpulses=driftCount;
   for(int j=0;j<2;j++){c.m_vimpulses[j]=velocityImpulses[j];c.m_dimpulses[j]=driftImpulses[j];}
  }
  void rotate(const btMatrix3x3& r){inverseWorldInertia=r*inverseWorldInertia*r.transpose();linearVelocity=r*linearVelocity;angularVelocity=r*angularVelocity;
   for(int j=0;j<2;j++){velocityImpulses[j]=r*velocityImpulses[j];driftImpulses[j]=r*driftImpulses[j];}
  }
  void transform(const btTransform& t){rotate(t.getBasis());center=t*center;frame=t*frame;}
 };
 struct History { std::vector<btSoftBody::Node> nodes; std::vector<ClusterHistory> clusters; std::vector<btVector3> faceNormals; };
 std::vector<History> history;
 std::vector<std::unique_ptr<Rigid>> rigid;
 std::unique_ptr<Rigid> ground;
 std::vector<std::unique_ptr<btSoftBody>> soft;
 std::vector<std::vector<int>> pins;
 std::vector<std::vector<std::pair<int,int>>> nearAnchors;
 World(const float* gravity,int hasGround,float y) {
  config.reset(new mmd_soft::PlaneSoftConfiguration()); dispatcher.reset(new btCollisionDispatcher(config.get()));
  broad.reset(new btDbvtBroadphase());solver.reset(new btSequentialImpulseConstraintSolver());
  ownedWorld.reset(new btSoftRigidDynamicsWorld(dispatcher.get(),broad.get(),solver.get(),config.get()));world=ownedWorld.get();
  world->setGravity(vec(gravity)); auto& info=world->getWorldInfo();
  info.m_gravity=vec(gravity);info.m_broadphase=broad.get();info.m_dispatcher=dispatcher.get();
  info.air_density=1.2f;info.water_density=0;info.water_offset=0;info.water_normal.setZero();info.m_sparsesdf.Initialize();
  if(hasGround){ground.reset(new Rigid());ground->shape.reset(new btStaticPlaneShape(btVector3(0,1,0),0));
   btRigidBody::btRigidBodyConstructionInfo ci(0,nullptr,ground->shape.get());ground->body.reset(new btRigidBody(ci));
   ground->body->setWorldTransform(btTransform(btQuaternion::getIdentity(),btVector3(0,y,0)));
   // All PMX groups (and soft clusters with a non-zero mask) collide with the ground; see mmd_bullet.cpp.
   ground->body->setInterpolationWorldTransform(ground->body->getWorldTransform());world->addRigidBody(ground->body.get(),short(-1),short(-1));}
 }
 World(btSoftRigidDynamicsWorld& value,const std::vector<btRigidBody*>& bodies,const std::vector<int>& modes)
  : world(&value),borrowed(true),rigidBodies(bodies),authoredModes(modes) {}
 ~World(){if(!world)return;for(auto& b:soft){if(borrowed&&frozen)static_cast<mmd_soft::CoupledDynamicsWorld*>(world)->forgetSuspended(b.get());world->removeSoftBody(b.get());}soft.clear();
  for(auto& b:rigid)world->removeRigidBody(b->body.get());rigid.clear();if(ground)world->removeRigidBody(ground->body.get());ground.reset();
  if(!borrowed)world->getWorldInfo().m_sparsesdf.Reset();}
};
World& ctx(void* p){if(!p)throw std::invalid_argument("Null soft world");return *static_cast<World*>(p);}
btSoftBody& body(World& w,int i){if(i<0||i>=int(w.soft.size()))throw std::out_of_range("Soft-body index");return *w.soft[i];}
void frozenNormals(btSoftBody& b){
 b.updateNormals();
 // Different invertible node maps can collapse a face. Its geometric normal
 // is zero; upstream normalized(zero) must not leave NaNs in collision data.
 for(int j=0;j<b.m_faces.size();j++){auto& n=b.m_faces[j].m_normal;
  if(!std::isfinite(n.x())||!std::isfinite(n.y())||!std::isfinite(n.z()))n.setZero();}
}
// Collision structures follow only live nodes; no rest constants or solver history change.
void refreshCollision(World& w,btSoftBody& b){
 const btScalar margin=b.getCollisionShape()->getMargin();
 for(int j=0;j<b.m_nodes.size();j++){auto& n=b.m_nodes[j];if(n.m_leaf){auto volume=btDbvtVolume::FromCR(n.m_x,margin);b.m_ndbvt.update(n.m_leaf,volume);}}
 for(int j=0;j<b.m_faces.size();j++){auto& f=b.m_faces[j];if(f.m_leaf){btVector3 points[]={f.m_n[0]->m_x,f.m_n[1]->m_x,f.m_n[2]->m_x};auto volume=btDbvtVolume::FromPoints(points,3);volume.Expand(btVector3(margin,margin,margin));b.m_fdbvt.update(f.m_leaf,volume);}}
 for(int j=0;j<b.m_clusters.size();j++){auto& c=*b.m_clusters[j];if(c.m_leaf&&c.m_nodes.size()){btVector3 lo=c.m_nodes[0]->m_x,hi=lo;for(int k=1;k<c.m_nodes.size();k++){lo.setMin(c.m_nodes[k]->m_x);hi.setMax(c.m_nodes[k]->m_x);}auto volume=btDbvtVolume::FromMM(lo,hi);volume.Expand(btVector3(margin,margin,margin));b.m_cdbvt.update(c.m_leaf,volume);}}
 b.updateBounds();w.world->updateSingleAabb(&b);
}
}
API const char* mmd_soft_error(){return message;}
API int mmd_soft_abi(){return BT_BULLET_VERSION==275?1:0;}
// Additive ABI1 capability. Existing callers and ordinary anchor payloads are unchanged.
API int mmd_soft_features(){return mmd_soft::KinematicNear|mmd_soft::ClusterPlane|mmd_soft::SharedNodes|8|mmd_soft::SharedClusters|32;}
API int mmd_soft_create(const float* gravity,int hasGround,float y,void** result){if(result)*result=nullptr;
 return guard([&]{if(!result||!gravity)throw std::invalid_argument("Null create arguments");*result=new World(gravity,hasGround,y);});}
API void mmd_soft_destroy(void* p){if(p&&static_cast<World*>(p)->borrowed){std::snprintf(message,sizeof(message),"Only the coupled owner may destroy this binding");return;}delete static_cast<World*>(p);}
API int mmd_soft_add_collider(void* p,int shape,int group,int mask,const float* size,const float* transform){return guard([&]{
 if(!size||!transform||group<0||group>15||mask<0||mask>65535)throw std::invalid_argument("Collider description");
 auto& w=ctx(p);if(w.borrowed)throw std::invalid_argument("Attached binding cannot add proxy colliders");auto r=std::make_unique<Rigid>();
 switch(shape){case 0:r->shape.reset(new btSphereShape(size[0]));break;case 1:r->shape.reset(new btBoxShape(vec(size)));break;
 case 2:r->shape.reset(new btCapsuleShape(size[0],size[1]));break;default:throw std::invalid_argument("Collider shape");}
 btRigidBody::btRigidBodyConstructionInfo ci(0,nullptr,r->shape.get());r->body.reset(new btRigidBody(ci));
 r->body->setWorldTransform(pose(transform));r->body->setInterpolationWorldTransform(pose(transform));
 r->body->setCollisionFlags(r->body->getCollisionFlags()|btCollisionObject::CF_KINEMATIC_OBJECT);
 r->body->setActivationState(DISABLE_DEACTIVATION);w.world->addRigidBody(r->body.get(),short(1<<group),short(mask));w.rigidBodies.push_back(r->body.get());w.rigid.push_back(std::move(r));});}
// options: shape,flags,bend,clusters,aero,group,mask,V,P,D,C. values: mass,margin,config12,cluster6,stiffness3.
API int mmd_soft_add(void* p,const float* points,int count,const int* triangles,int numIndices,const int* options,
 const float* values,const int* pins,int numPins,const int* anchors,int numAnchors){return guard([&]{
 auto& w=ctx(p);if(w.frozen)throw std::invalid_argument("Cannot change frozen topology");if(count<2||!points||!options||!values)throw std::invalid_argument("Soft description");
 if(count>std::numeric_limits<int>::max()/3||(options[0]==0&&numIndices>0&&!triangles)||numPins<0||(numPins&&!pins)||
    numAnchors<0||numAnchors>std::numeric_limits<int>::max()/2||(numAnchors&&!anchors)||options[5]<0||options[5]>15||options[6]<0||options[6]>65535)
  throw std::invalid_argument("Soft description/counts");
 requirePoints(points,3LL*count,"Soft point is nonfinite or outside the supported coordinate range");
 std::vector<btVector3> vertices;for(int i=0;i<count;i++)vertices.push_back(vec(points+i*3));
 std::unique_ptr<btSoftBody> b;
 if(options[0]==0){if(numIndices<=0||numIndices%3)throw std::invalid_argument("Triangle count");
  // Construct all nodes explicitly: helpers may weld coordinate-identical vertices and destroy the PMX map.
  b.reset(new btSoftBody(&w.world->getWorldInfo(),count,vertices.data(),nullptr));
  for(int i=0;i<numIndices;i+=3){int a=triangles[i],c=triangles[i+1],d=triangles[i+2];
   if(a<0||c<0||d<0||a>=count||c>=count||d>=count)throw std::invalid_argument("Triangle index");
   b->appendLink(a,c,nullptr,true);b->appendLink(c,d,nullptr,true);b->appendLink(d,a,nullptr,true);b->appendFace(a,c,d);}}
 else if(options[0]==1){b.reset(new btSoftBody(&w.world->getWorldInfo(),count,vertices.data(),nullptr));
  for(int i=1;i<count;i++)b->appendLink(i-1,i);}
 else throw std::invalid_argument("Soft shape");
 b->setTotalMass(values[0]);b->getCollisionShape()->setMargin(values[1]);auto& c=b->m_cfg;
 c.aeromodel=static_cast<btSoftBody::eAeroModel::_>(options[4]);
 c.kVCF=values[2];c.kDP=values[3];c.kDG=values[4];c.kLF=values[5];c.kPR=values[6];c.kVC=values[7];
 c.kDF=values[8];c.kMT=values[9];c.kCHR=values[10];c.kKHR=values[11];c.kSHR=values[12];c.kAHR=values[13];
 c.kSRHR_CL=values[14];c.kSKHR_CL=values[15];c.kSSHR_CL=values[16];
 c.kSR_SPLT_CL=values[17];c.kSK_SPLT_CL=values[18];c.kSS_SPLT_CL=values[19];
 c.viterations=options[7];c.piterations=options[8];c.diterations=options[9];c.citerations=options[10];
 auto* material=b->m_materials[0];material->m_kLST=values[20];material->m_kAST=values[21];material->m_kVST=values[22];
 if(options[1]&1)b->generateBendingConstraints(options[2],material);
 if(options[1]&2){
  const int requested=btMin(options[3],b->m_nodes.size());
  // Positive requests may add a master cluster; zero requests use one per face.
  // Bullet 2.75 indexes their square with int, including on the single-source path.
  const long long maximum=requested>0?static_cast<long long>(requested)+(requested>1?1:0):b->m_faces.size();
  if(options[3]<0||maximum>46340)throw std::invalid_argument("Cluster connectivity exceeds int32 storage");
  b->generateClusters(options[3]);c.collisions=btSoftBody::fCollision::CL_RS|btSoftBody::fCollision::CL_SS;}
 else c.collisions=btSoftBody::fCollision::SDF_RS|btSoftBody::fCollision::VF_SS;
 if(options[1]&4)b->randomizeConstraints();
 std::vector<int> pinned;for(int i=0;i<numPins;i++){if(pins[i]<0||pins[i]>=count)throw std::invalid_argument("Pin");b->setMass(pins[i],0);pinned.push_back(pins[i]);}
 for(int i=0;i<numAnchors;i++){int node=anchors[i*2],rigid=anchors[i*2+1];if(node<0||node>=count||rigid<0||rigid>=int(w.rigidBodies.size()))throw std::invalid_argument("Anchor");b->appendAnchor(node,w.rigidBodies[rigid],false);}
 b->setPose(values[7]!=0,values[9]!=0);b->updateNormals();b->updateBounds();
 size_t next=w.soft.size()+1;w.soft.reserve(next);w.pins.reserve(next);w.nearAnchors.reserve(next);
 w.world->addSoftBody(b.get(),short(1<<options[5]),short(options[6]));w.pins.push_back(std::move(pinned));w.nearAnchors.emplace_back();w.soft.push_back(std::move(b));});}
// Additive ABI1 shared-node topology. Each source retains its material and local bending graph.
// Source cluster memberships are generated upstream in authored local node/face order,
// then mapped to the same merged solver nodes. Cluster arrays always own their storage.
API int mmd_soft_add_shared(void* p,const float* points,int count,int partCount,
 const int* nodeOffsets,const int* nodes,int nodeCount,const int* triangleOffsets,
 const int* triangles,int triangleCount,const int* options,const float* values,
 const int* pins,int pinCount,const int* anchors,int anchorCount){return guard([&]{
 auto& w=ctx(p);if(w.frozen)throw std::invalid_argument("Cannot change frozen topology");
 if(count<2||count>std::numeric_limits<int>::max()/3||partCount<2||partCount>std::numeric_limits<int>::max()/23||
    !points||!nodeOffsets||!nodes||!triangleOffsets||!options||!values||nodeCount<0||triangleCount<0||
    (triangleCount&&!triangles)||pinCount<0||(pinCount&&!pins)||anchorCount<0||
    anchorCount>std::numeric_limits<int>::max()/2||(anchorCount&&!anchors))throw std::invalid_argument("Shared description/counts");
 if(nodeOffsets[0]!=0||nodeOffsets[partCount]!=nodeCount||triangleOffsets[0]!=0||triangleOffsets[partCount]!=triangleCount)
  throw std::invalid_argument("Shared offset endpoints");
 std::vector<btVector3> vertices;vertices.reserve(count);
 requirePoints(points,3LL*count,"Shared point is nonfinite or outside the supported coordinate range");
 for(int i=0;i<count;i++)vertices.push_back(vec(points+i*3));
 std::vector<double> masses(count,0);double totalMass=0;
 std::vector<int> owner(count,-1),parent(partCount);for(int i=0;i<partCount;i++)parent[i]=i;
 auto root=[&](int i){while(parent[i]!=i){parent[i]=parent[parent[i]];i=parent[i];}return i;};
 for(int part=0;part<partCount;part++){
  int begin=nodeOffsets[part],end=nodeOffsets[part+1],tb=triangleOffsets[part],te=triangleOffsets[part+1];
  if(begin<0||end<begin||end>nodeCount||end-begin<2||tb<0||te<tb||te>triangleCount)
   throw std::invalid_argument("Shared part offsets");
  const int* o=options+part*11;const float* v=values+part*23;
  if(o[0]<0||o[0]>1||o[1]<0||o[1]>7||o[2]<0||o[3]<0||o[4]<0||o[4]>4||o[5]<0||o[5]>15||o[6]<0||o[6]>65535)
   throw std::invalid_argument("Shared part options");
  for(int i=7;i<11;i++)if(o[i]<0||o[i]>1024)throw std::invalid_argument("Shared iterations");
  for(int i=0;i<23;i++)if(!std::isfinite(v[i]))throw std::invalid_argument("Shared nonfinite settings");
  if(v[0]<=0||v[1]<0)throw std::invalid_argument("Shared mass/margin");
  for(int i=1;i<20;i++)if(v[i]!=values[i])throw std::invalid_argument("Shared body-wide value conflict");
  for(int i=4;i<11;i++)if(o[i]!=options[i])throw std::invalid_argument("Shared body-wide option conflict");
  if((o[1]&4)!=(options[1]&4))throw std::invalid_argument("Shared randomization conflict");
  if((o[1]&2)!=(options[1]&2))throw std::invalid_argument("Shared cluster collision switch conflict");
  if((o[0]==0&&(te==tb||(te-tb)%3))||(o[0]==1&&te!=tb))throw std::invalid_argument("Shared source triangle count");
  std::set<int> seen;
  for(int j=begin;j<end;j++){
   int id=nodes[j];if(id<0||id>=count||!seen.insert(id).second)throw std::invalid_argument("Shared source node index/duplicate");
   if(owner[id]>=0)parent[root(part)]=root(owner[id]);else owner[id]=part;
   masses[id]+=double(v[0])/(end-begin);
  }
  for(int j=tb;j<te;j++)if(triangles[j]<0||triangles[j]>=end-begin)throw std::invalid_argument("Shared source triangle index");
  for(int j=tb;j<te;j+=3){
   auto a=vertices[nodes[begin+triangles[j]]],b=vertices[nodes[begin+triangles[j+1]]],c=vertices[nodes[begin+triangles[j+2]]];
   if((b-a).cross(c-a).length2()<=1e-16f)throw std::invalid_argument("Shared degenerate triangle");
  }
  totalMass+=v[0];
 }
 if(totalMass>std::numeric_limits<float>::max())throw std::invalid_argument("Shared total mass overflow");
 for(int part=1;part<partCount;part++)if(root(part)!=root(0))throw std::invalid_argument("Shared parts must form one overlap-connected component");
 for(double value:masses){float m=static_cast<float>(value);if(!std::isfinite(m)||m<=0||!std::isfinite(1.f/m))throw std::invalid_argument("Shared node mass outside float32 solver range");}
 std::set<int> pinned;
 for(int i=0;i<pinCount;i++){if(pins[i]<0||pins[i]>=count)throw std::invalid_argument("Shared pin");pinned.insert(pins[i]);}
 std::set<std::pair<int,int>> anchored;
 for(int i=0;i<anchorCount;i++){int node=anchors[i*2],rigid=anchors[i*2+1];
  if(node<0||node>=count||rigid<0||rigid>=int(w.rigidBodies.size()))throw std::invalid_argument("Shared anchor");anchored.emplace(node,rigid);}
 auto b=std::make_unique<btSoftBody>(&w.world->getWorldInfo(),count,vertices.data(),nullptr);
 for(int part=0;part<partCount;part++){
  int begin=nodeOffsets[part],length=nodeOffsets[part+1]-begin;const int* o=options+part*11;const float* v=values+part*23;
  auto* material=part==0?b->m_materials[0]:b->appendMaterial();
  material->m_kLST=v[20];material->m_kAST=v[21];material->m_kVST=v[22];
  std::vector<btVector3> local;local.reserve(length);for(int i=0;i<length;i++)local.push_back(vertices[nodes[begin+i]]);
  btSoftBody source(&w.world->getWorldInfo(),length,local.data(),nullptr);
  if(o[0]==0)for(int j=triangleOffsets[part];j<triangleOffsets[part+1];j+=3){int a=triangles[j],c=triangles[j+1],d=triangles[j+2];
   source.appendLink(a,c,nullptr,true);source.appendLink(c,d,nullptr,true);source.appendLink(d,a,nullptr,true);
   source.appendFace(a,c,d);
   b->appendFace(nodes[begin+a],nodes[begin+c],nodes[begin+d],material);
  }else for(int j=1;j<length;j++)source.appendLink(j-1,j);
  if(o[1]&1)source.generateBendingConstraints(o[2],source.m_materials[0]);
  for(int j=0;j<source.m_links.size();j++){
   const auto& link=source.m_links[j];int a=int(link.m_n[0]-&source.m_nodes[0]),c=int(link.m_n[1]-&source.m_nodes[0]);
   // Do not deduplicate across sources: two authored materials retain two constraints.
   b->appendLink(nodes[begin+a],nodes[begin+c],material,false);b->m_links[b->m_links.size()-1].m_bbending=link.m_bbending;
  }
  if(o[1]&2){
   const int requested=btMin(o[3],length);
   const int maximum=requested?requested+(requested>1?1:0):source.m_faces.size();
   if(maximum>46340||b->m_clusters.size()>46340-maximum)
    throw std::invalid_argument("Shared cluster connectivity exceeds int32 storage");
   source.generateClusters(o[3]);
   for(int j=0;j<source.m_clusters.size();j++){
    // Never copy/assign Cluster: btAlignedObjectArray's implicit assignment is
    // shallow in this Bullet version. Copy only identity membership and collision
    // role; initialize numerical constants from final shared masses and union pins.
    const auto& original=*source.m_clusters[j];
    b->m_clusters.reserve(b->m_clusters.size()+1);
    void* storage=btAlignedAlloc(sizeof(btSoftBody::Cluster),16);
    if(!storage)throw std::bad_alloc();
    auto* cluster=new(storage) btSoftBody::Cluster();
    b->m_clusters.push_back(cluster);
    cluster->m_collide=original.m_collide;
    cluster->m_nodes.reserve(original.m_nodes.size());
    for(int k=0;k<original.m_nodes.size();k++){
     int localId=int(original.m_nodes[k]-&source.m_nodes[0]);
     cluster->m_nodes.push_back(&b->m_nodes[nodes[begin+localId]]);
    }
   }
  }
 }
 for(int i=0;i<count;i++)b->setMass(i,static_cast<float>(masses[i]));
 for(int id:pinned)b->setMass(id,0);
 for(const auto& anchor:anchored)b->appendAnchor(anchor.first,w.rigidBodies[anchor.second],false);
 b->getCollisionShape()->setMargin(values[1]);auto& c=b->m_cfg;
 c.aeromodel=static_cast<btSoftBody::eAeroModel::_>(options[4]);
 c.kVCF=values[2];c.kDP=values[3];c.kDG=values[4];c.kLF=values[5];c.kPR=values[6];c.kVC=values[7];
 c.kDF=values[8];c.kMT=values[9];c.kCHR=values[10];c.kKHR=values[11];c.kSHR=values[12];c.kAHR=values[13];
 c.kSRHR_CL=values[14];c.kSKHR_CL=values[15];c.kSSHR_CL=values[16];
 c.kSR_SPLT_CL=values[17];c.kSK_SPLT_CL=values[18];c.kSS_SPLT_CL=values[19];
 c.viterations=options[7];c.piterations=options[8];c.diterations=options[9];c.citerations=options[10];
 c.collisions=(options[1]&2)?btSoftBody::fCollision::CL_RS|btSoftBody::fCollision::CL_SS
                            :btSoftBody::fCollision::SDF_RS|btSoftBody::fCollision::VF_SS;
 if(b->m_clusters.size()){
  const int count=b->m_clusters.size();
  if(count>46340)throw std::invalid_argument("Shared cluster connectivity exceeds int32 storage");
  b->initializeClusters();b->updateClusters();
  b->m_clusterConnectivity.resize(count*count);
  for(int i=0;i<count;i++){
   auto& a=*b->m_clusters[i];a.m_clusterIndex=i;
   for(int j=0;j<count;j++){
    const auto& other=*b->m_clusters[j];bool connected=false;
    for(int k=0;!connected&&k<a.m_nodes.size();k++)
     connected=other.m_nodes.findLinearSearch(a.m_nodes[k])!=other.m_nodes.size();
    b->m_clusterConnectivity[i+j*count]=connected;
   }
  }
 }
 if(options[1]&4)b->randomizeConstraints();
 b->setPose(values[7]!=0,values[9]!=0);b->updateNormals();b->updateBounds();
 // Allocate collection storage before registering, so rejected/allocation-failed candidates cannot append half state.
 std::vector<int> pinList(pinned.begin(),pinned.end());size_t next=w.soft.size()+1;
 w.soft.reserve(next);w.pins.reserve(next);w.nearAnchors.reserve(next);
 w.world->addSoftBody(b.get(),short(1<<options[5]),short(options[6]));
 w.pins.push_back(std::move(pinList));w.nearAnchors.emplace_back();w.soft.push_back(std::move(b));
 });}
// Read-only ownership evidence; no solver step, topology mutation, or change to old ABI1 exports.
API int mmd_soft_topology(void* p,int index,int* counts,float* masses,int count){return guard([&]{
 auto& b=body(ctx(p),index);if(!counts||!masses||count!=b.m_nodes.size())throw std::invalid_argument("Topology output/count");
 counts[0]=b.m_nodes.size();counts[1]=b.m_faces.size();counts[2]=b.m_links.size();counts[3]=b.m_materials.size();
 for(int i=0;i<count;i++)masses[i]=b.m_nodes[i].m_im>0?1.f/b.m_nodes[i].m_im:0;
 });}
API int mmd_soft_near_anchors(void* p,int index,const int* pairs,int count){return guard([&]{
 auto& w=ctx(p);if(w.borrowed)throw std::invalid_argument("Attached Near requires global authored ordinals");auto& b=body(w,index);if(count<0||(count>0&&!pairs))throw std::invalid_argument("Near anchor pairs");
 std::vector<std::pair<int,int>> candidate;candidate.reserve(count);
 for(int i=0;i<count;i++){int node=pairs[i*2],rigid=pairs[i*2+1];
  if(node<0||node>=b.m_nodes.size()||rigid<0||rigid>=int(w.rigidBodies.size()))throw std::invalid_argument("Near anchor index");
  bool anchored=false;for(int j=0;j<b.m_anchors.size();j++)
   if(b.m_anchors[j].m_node==&b.m_nodes[node]&&b.m_anchors[j].m_body==w.rigidBodies[rigid]){anchored=true;break;}
  if(!anchored)throw std::invalid_argument("Near pair must reference an existing ordinary anchor");
  candidate.emplace_back(node,rigid);
 }
 w.nearAnchors[index]=std::move(candidate);});}
API int mmd_soft_targets(void* p,int index,const float* points,int count){return guard([&]{auto& w=ctx(p);auto& b=body(w,index);
 if(count!=b.m_nodes.size()||!points)throw std::invalid_argument("Target count");requirePoints(points,3LL*count,"Soft target is nonfinite or outside the supported coordinate range");if(w.frozen)return;for(int i:w.pins[index]){auto& n=b.m_nodes[i];n.m_x=vec(points+i*3);n.m_q=n.m_x;n.m_v.setZero();n.m_f.setZero();}b.updateBounds();});}
API int mmd_soft_collider_pose(void* p,int index,const float* transform){return guard([&]{auto& w=ctx(p);if(w.borrowed)throw std::invalid_argument("Attached binding cannot publish proxy poses");if(index<0||index>=int(w.rigidBodies.size()))throw std::out_of_range("Collider");
 auto* b=w.rigidBodies[index];b->setWorldTransform(pose(transform));w.world->updateSingleAabb(b);b->activate(true);});}
// Additive ABI1 feature 8: complete proxy pose transaction. No soft nodes, velocities,
// anchors or timing are rebuilt. All validation/allocation precedes the no-fail commit.
API int mmd_soft_collider_poses(void* p,const float* transforms,int count){return guard([&]{
 auto& w=ctx(p);
 if(w.borrowed)throw std::invalid_argument("Attached binding cannot publish proxy poses");
 if(count<0||count!=int(w.rigidBodies.size())||count>std::numeric_limits<int>::max()/7||(count&&!transforms))
  throw std::invalid_argument("Rigid proxy batch count/payload");
 std::vector<btTransform> candidate;candidate.reserve(count);
 for(int i=0;i<count;i++){
  const float* value=transforms+i*7;
  for(int j=0;j<7;j++)if(!std::isfinite(value[j]))throw std::invalid_argument("Nonfinite rigid proxy pose");
  double norm=0;for(int j=3;j<7;j++)norm+=double(value[j])*value[j];
  if(std::abs(norm-1)>1e-4)throw std::invalid_argument("Rigid proxy quaternion must be unit length");
  candidate.push_back(pose(value));
 }
 for(int i=0;i<count;i++){
  auto* b=w.rigidBodies[i];b->setWorldTransform(candidate[i]);
  b->setInterpolationWorldTransform(candidate[i]);b->activate(true);
 }
 // Bullet refreshes broadphase AABBs at the next step, before collision detection.
 // Avoid per-proxy broadphase allocation after this batch has started committing.
});}
API int mmd_soft_step(void* p,float seconds){return guard([&]{auto& w=ctx(p);if(w.borrowed)throw std::invalid_argument("Only the coupled owner may step this world");if(!std::isfinite(seconds)||seconds<=0||seconds>.1f)throw std::invalid_argument("Step seconds");w.world->stepSimulation(seconds,0,seconds);
 // PMX compatibility: after physics, bone-follow Near adds body-origin minus node-position
 // to node velocity. It is not appendAnchor's collision-disable option or a position snap.
 // Behavior reference: PMXEditor0236 PMX specification, SoftBody deformation / Near processing.
 for(int i=0;i<int(w.soft.size());i++)for(const auto& anchor:w.nearAnchors[i]){
  auto& node=w.soft[i]->m_nodes[anchor.first];node.m_v+=w.rigidBodies[anchor.second]->getWorldTransform().getOrigin()-node.m_x;
 }
 w.world->getWorldInfo().m_sparsesdf.GarbageCollect();});}
API int mmd_soft_sample(void* p,int index,float* points,float* normals,int count){return guard([&]{auto& w=ctx(p);auto& b=body(w,index);if(count!=b.m_nodes.size())throw std::invalid_argument("Sample count");
 if(w.frozen&&w.frozenSurfaceProjected)frozenNormals(b);else b.updateNormals();for(int i=0;i<count;i++){const auto& n=b.m_nodes[i];for(int c=0;c<3;c++){points[i*3+c]=n.m_x[c];normals[i*3+c]=n.m_n[c];if(!std::isfinite(points[i*3+c])||!std::isfinite(normals[i*3+c]))throw std::runtime_error("Nonfinite Bullet soft output");}}});}
API int mmd_soft_gravity(void* p,const float* gravity){return guard([&]{auto& w=ctx(p);if(w.borrowed)throw std::invalid_argument("Only the coupled owner may set gravity");w.world->setGravity(vec(gravity));w.world->getWorldInfo().m_gravity=vec(gravity);});}

namespace mmd_soft {
void CoupledDynamicsWorld::predictUnconstraintMotion(btScalar seconds){
 btSoftRigidDynamicsWorld::predictUnconstraintMotion(seconds);
 for(int i=0;i<suspended.size();i++){
  auto& b=*suspended[i];b.m_sst.sdt=seconds*b.m_cfg.timescale;b.m_sst.isdt=1/b.m_sst.sdt;
  b.m_sst.velmrg=b.m_sst.sdt*3;b.m_sst.radmrg=b.getCollisionShape()->getMargin();b.m_sst.updmrg=b.m_sst.radmrg*.25f;
  // The saved previous position remains in History. Collision constraints see
  // an immovable current surface, with no residual pre-freeze displacement.
  for(int j=0;j<b.m_nodes.size();j++)b.m_nodes[j].m_q=b.m_nodes[j].m_x;
  b.m_rcontacts.resize(0);b.m_scontacts.resize(0);
 }
}
void CoupledDynamicsWorld::internalSingleStepSimulation(btScalar seconds){
 if(!suspended.size()){btSoftRigidDynamicsWorld::internalSingleStepSimulation(seconds);return;}
 auto& active=getSoftBodyArray();btSoftBodyArray contacts;
 contacts.reserve(active.size()+suspended.size());
 for(int i=0;i<active.size();i++)contacts.push_back(active[i]);
 for(int i=0;i<suspended.size();i++)contacts.push_back(suspended[i]);
 btDiscreteDynamicsWorld::internalSingleStepSimulation(seconds);
 // Bullet stores cluster contact joints on either participant. Include every
 // collision participant exactly once, retaining zero inverse mass on suspended
 // clusters. Their owned arrays and numerical history are never copied here.
 btSoftBody::solveClusters(contacts);
 for(int i=0;i<active.size();i++)active[i]->solveConstraints();
 for(int i=0;i<suspended.size();i++){
  auto* b=suspended[i];
  for(int j=0;j<b->m_cfg.piterations;j++){
   btSoftBody::PSolve_RContacts(b,1,j/btScalar(b->m_cfg.piterations));
   btSoftBody::PSolve_SContacts(b,1,j/btScalar(b->m_cfg.piterations));
  }
 }
 for(int i=0;i<active.size();i++)active[i]->defaultCollisionHandler(active[i]);
 for(int i=0;i<active.size();i++)active[i]->integrateMotion();
}
void* createBorrowed(btSoftRigidDynamicsWorld& world,const std::vector<btRigidBody*>& bodies,const std::vector<int>& modes){
 return new World(world,bodies,modes);
}
void destroyBorrowed(void* binding) noexcept {delete static_cast<World*>(binding);}
bool references(void* binding,btRigidBody* rigid){auto& w=ctx(binding);
 return std::find(w.rigidBodies.begin(),w.rigidBodies.end(),rigid)!=w.rigidBodies.end();
}
void postStep(void* binding){auto& w=ctx(binding);if(w.frozen)return;
 for(const auto& command:w.orderedNear){
  auto& node=body(w,command.soft).m_nodes[command.node];auto* rigid=w.rigidBodies[command.rigid];
  if(w.authoredModes[command.rigid]==0){node.m_v+=rigid->getWorldTransform().getOrigin()-node.m_x;continue;}
  // Temporary physics-off/frozen commands take precedence over authored dynamic Near.
  if(rigid->isStaticOrKinematicObject())continue;
  btTransform target=rigid->getWorldTransform();target.setOrigin(node.m_x);
  rigid->setCenterOfMassTransform(target);rigid->setInterpolationWorldTransform(target);
  if(rigid->getMotionState())rigid->getMotionState()->setWorldTransform(target);
  w.world->updateSingleAabb(rigid);rigid->activate(true);
 }
}
}
// Four integers per pair: solver-body index, node index, actor-local rigid index,
// original model soft/anchor ordinal. Never deduplicate or group-sort this list.
API int mmd_soft_attached_near(void* p,const int* commands,int count){return guard([&]{
 auto& w=ctx(p);if(!w.borrowed||count<0||count>std::numeric_limits<int>::max()/4||(count&&!commands))throw std::invalid_argument("Attached Near commands");
 std::vector<World::Near> candidate;candidate.reserve(count);std::set<int> ordinals;
 for(int i=0;i<count;i++){
  const int* c=commands+i*4;auto& b=body(w,c[0]);
  if(c[1]<0||c[1]>=b.m_nodes.size()||c[2]<0||c[2]>=int(w.rigidBodies.size())||c[3]<0||!ordinals.insert(c[3]).second)throw std::invalid_argument("Attached Near index/ordinal");
  bool found=false;for(int j=0;j<b.m_anchors.size();j++)if(b.m_anchors[j].m_node==&b.m_nodes[c[1]]&&b.m_anchors[j].m_body==w.rigidBodies[c[2]]){found=true;break;}
  if(!found)throw std::invalid_argument("Near must reference an ordinary anchor");candidate.push_back({c[0],c[1],c[2],c[3]});
 }
 std::sort(candidate.begin(),candidate.end(),[](const World::Near& a,const World::Near& b){return a.ordinal<b.ordinal;});
 w.orderedNear=std::move(candidate);
});}
// Keep frozen shapes in the collision world, but remove them from the numerical
// soft-body array. Preserve complete node/cluster history separately; mass zero
// only makes the retained collision side immovable for other actors' contacts.
API int mmd_soft_attached_frozen(void* p,int frozen){return guard([&]{
 auto& w=ctx(p);if(!w.borrowed)throw std::invalid_argument("Expected attached binding");if(w.frozen==(frozen!=0))return;
 auto& array=w.world->getSoftBodyArray();
 if(frozen){
  std::vector<World::History> candidate(w.soft.size());
  for(size_t i=0;i<w.soft.size();i++){
   auto& b=*w.soft[i];auto& h=candidate[i];h.nodes.reserve(b.m_nodes.size());h.clusters.reserve(b.m_clusters.size());h.faceNormals.reserve(b.m_faces.size());
   // Prepare authored-mass rest constants and node areas before snapshotting.
   // Targets may have changed since setPose's initial constants were computed.
   if(b.m_bUpdateRtCst){b.updateConstants();b.m_fdbvt.clear();if(b.m_cfg.collisions&btSoftBody::fCollision::VF_SS)b.initializeFaceTree();b.m_bUpdateRtCst=false;}
   for(int j=0;j<b.m_nodes.size();j++)h.nodes.push_back(b.m_nodes[j]);
   for(int j=0;j<b.m_clusters.size();j++)h.clusters.emplace_back(*b.m_clusters[j]);
   for(int j=0;j<b.m_faces.size();j++)h.faceNormals.push_back(b.m_faces[j].m_normal);
  }
  auto& coupled=*static_cast<mmd_soft::CoupledDynamicsWorld*>(w.world);coupled.reserveSuspended(int(w.soft.size()));
  w.history=std::move(candidate);
  for(auto& value:w.soft){auto& b=*value;
   coupled.suspend(&b);
   for(int j=0;j<b.m_nodes.size();j++){auto& n=b.m_nodes[j];n.m_im=0;n.m_v.setZero();n.m_f.setZero();}
   for(int j=0;j<b.m_clusters.size();j++){auto& c=*b.m_clusters[j];c.m_imass=0;c.m_invwi.setValue(0,0,0,0,0,0,0,0,0);c.m_lv.setZero();c.m_av.setZero();}
  }
  w.frozen=true;w.frozenSurfaceProjected=false;
 }else{
  // Reserve before restoring any body so unfreeze is atomic on allocation failure.
  array.reserve(array.size()+int(w.soft.size()));
  for(size_t i=0;i<w.soft.size();i++){auto& b=*w.soft[i];auto& h=w.history[i];
   for(int j=0;j<b.m_nodes.size();j++)b.m_nodes[j]=h.nodes[j];
   for(int j=0;j<b.m_clusters.size();j++)h.clusters[j].restore(*b.m_clusters[j]);
   for(int j=0;j<b.m_faces.size();j++)b.m_faces[j].m_normal=h.faceNormals[j];
   if(w.frozenSurfaceProjected)refreshCollision(w,b);
   static_cast<mmd_soft::CoupledDynamicsWorld*>(w.world)->forgetSuspended(&b);array.push_back(&b);
  }
  w.frozen=false;w.frozenSurfaceProjected=false;w.history.clear();
 }
});}

namespace {
using FrozenSurfaceCandidate=std::pair<std::vector<btVector3>,std::vector<std::vector<btVector3>>>;
// Shared validation is read-only and also accepts an active binding: the scene
// must reject every bad candidate before freezing or publishing its first actor.
FrozenSurfaceCandidate prepareFrozenSurface(World& w,const float* points,int count){
 if(!w.borrowed)throw std::invalid_argument("Attached surface required");
 size_t expected=0;for(auto& b:w.soft)expected+=size_t(b->m_nodes.size());
 if(count<0||count>std::numeric_limits<int>::max()/3||size_t(count)!=expected||(count&&!points))throw std::invalid_argument("Frozen surface count");
 std::vector<btVector3> candidate;candidate.reserve(expected);size_t offset=0;
 for(auto& b:w.soft){const float margin=b->getCollisionShape()->getMargin();for(int j=0;j<b->m_nodes.size();j++,offset++){
  const float* q=points+offset*3;
  for(int c=0;c<3;c++)if(!std::isfinite(q[c])||!std::isfinite(q[c]-margin)||!std::isfinite(q[c]+margin)||q[c]-margin < -BT_LARGE_FLOAT||q[c]+margin > BT_LARGE_FLOAT)throw std::invalid_argument("Invalid frozen surface bounds");
  candidate.push_back(vec(q));
 }}
 std::vector<std::vector<btVector3>> centers(w.soft.size());offset=0;
 for(size_t i=0;i<w.soft.size();i++){auto& b=*w.soft[i];centers[i].reserve(b.m_clusters.size());
  for(int j=0;j<b.m_faces.size();j++){auto& f=b.m_faces[j];const auto& a=candidate[offset+size_t(f.m_n[0]-&b.m_nodes[0])];const auto& v=candidate[offset+size_t(f.m_n[1]-&b.m_nodes[0])];const auto& z=candidate[offset+size_t(f.m_n[2]-&b.m_nodes[0])];
   auto normal=btCross(v-a,z-a);if(!std::isfinite(normal.x())||!std::isfinite(normal.y())||!std::isfinite(normal.z())||!std::isfinite(normal.length2()))throw std::invalid_argument("Frozen face geometry overflows");}
  for(int j=0;j<b.m_clusters.size();j++){auto& c=*b.m_clusters[j];double mass=0,xyz[3]={0,0,0};
   for(int k=0;k<c.m_nodes.size();k++){const auto& point=candidate[offset+size_t(c.m_nodes[k]-&b.m_nodes[0])];const double weight=c.m_masses[k];mass+=weight;for(int axis=0;axis<3;axis++)xyz[axis]+=double(point[axis])*weight;}
   btVector3 center=c.m_com;if(mass>0){for(int axis=0;axis<3;axis++){center[axis]=btScalar(xyz[axis]/mass);if(!std::isfinite(center[axis]))throw std::invalid_argument("Nonfinite frozen cluster center");}}
   centers[i].push_back(center);
  }offset+=size_t(b.m_nodes.size());
 }
 return {std::move(candidate),std::move(centers)};
}
}
API int mmd_soft_attached_validate_frozen_surface(void* p,const float* points,int count){return guard([&]{
 auto candidate=prepareFrozenSurface(ctx(p),points,count);
});}

// One batch in solver-body/node order, retaining the unmodified original trajectory.
API int mmd_soft_attached_frozen_surface(void* p,const float* points,int count){return guard([&]{
 auto& w=ctx(p);if(!w.borrowed||!w.frozen)throw std::invalid_argument("Frozen attached surface required");
 auto surface=prepareFrozenSurface(w,points,count);auto& candidate=surface.first;auto& centers=surface.second;
 size_t offset=0;for(size_t i=0;i<w.soft.size();i++){auto& b=*w.soft[i];
  for(int j=0;j<b.m_nodes.size();j++,offset++){b.m_nodes[j].m_x=candidate[offset];b.m_nodes[j].m_q=candidate[offset];}
  for(int j=0;j<b.m_clusters.size();j++){auto& c=*b.m_clusters[j];c.m_com=centers[i][j];c.m_framexform.setOrigin(c.m_com);}
  frozenNormals(b);b.m_rcontacts.resize(0);b.m_scontacts.resize(0);refreshCollision(w,b);
 }w.frozenSurfaceProjected=true;
});}

API int mmd_soft_attached_rebase(void* p,const float* transform){return guard([&]{
 auto& w=ctx(p);if(!w.borrowed||!transform)throw std::invalid_argument("Attached rebase payload");
 for(int j=0;j<7;j++)if(!std::isfinite(transform[j]))throw std::invalid_argument("Nonfinite rebase");
 btQuaternion q(transform[3],transform[4],transform[5],transform[6]);if(btFabs(q.length2()-1)>.0001f)throw std::invalid_argument("Rebase rotation");
 btTransform t(q,vec(transform));const btMatrix3x3 r=t.getBasis(),rt=r.transpose();
 auto nodeTransform=[&](btSoftBody::Node& n){n.m_x=t*n.m_x;n.m_q=t*n.m_q;n.m_v=r*n.m_v;n.m_f=r*n.m_f;n.m_n=r*n.m_n;};
 auto clusterTransform=[&](btSoftBody::Cluster& c){
  c.m_com=t*c.m_com;c.m_framexform=t*c.m_framexform;c.m_invwi=r*c.m_invwi*rt;c.m_lv=r*c.m_lv;c.m_av=r*c.m_av;
  for(int j=0;j<2;j++){c.m_vimpulses[j]=r*c.m_vimpulses[j];c.m_dimpulses[j]=r*c.m_dimpulses[j];}
 };
 // Preflight every point/vector, including saved frozen history, before mutation.
 auto validate=[&](const btSoftBody::Node& original){auto n=original;nodeTransform(n);
  for(int j=0;j<3;j++)if(!std::isfinite(n.m_x[j])||!std::isfinite(n.m_q[j])||!std::isfinite(n.m_v[j])||!std::isfinite(n.m_f[j])||!std::isfinite(n.m_n[j]))throw std::invalid_argument("Rebase overflows soft history");};
 auto finiteVector=[](const btVector3& v){return std::isfinite(v.x())&&std::isfinite(v.y())&&std::isfinite(v.z());};
 auto validateCluster=[&](const btSoftBody::Cluster& original){auto c=original;clusterTransform(c);
  if(!finiteVector(c.m_com)||!finiteVector(c.m_framexform.getOrigin())||!finiteVector(c.m_lv)||!finiteVector(c.m_av))throw std::invalid_argument("Rebase overflows cluster history");
  for(int j=0;j<3;j++)if(!finiteVector(c.m_invwi[j])||!finiteVector(c.m_framexform.getBasis()[j]))throw std::invalid_argument("Rebase overflows cluster matrix");
  for(int j=0;j<2;j++)if(!finiteVector(c.m_vimpulses[j])||!finiteVector(c.m_dimpulses[j]))throw std::invalid_argument("Rebase overflows cluster impulses");};
 for(auto& b:w.soft){for(int j=0;j<b->m_clusters.size();j++)validateCluster(*b->m_clusters[j]);
  for(int j=0;j<b->m_faces.size();j++)if(!finiteVector(r*b->m_faces[j].m_normal))throw std::invalid_argument("Rebase overflows face normal");
  if(b->m_pose.m_bframe&&!finiteVector(t*b->m_pose.m_com))throw std::invalid_argument("Rebase overflows soft pose");}
 for(auto& h:w.history)for(auto c:h.clusters){c.transform(t);
  if(!finiteVector(c.center)||!finiteVector(c.frame.getOrigin())||!finiteVector(c.linearVelocity)||!finiteVector(c.angularVelocity))throw std::invalid_argument("Rebase overflows saved cluster history");
  for(int j=0;j<3;j++)if(!finiteVector(c.inverseWorldInertia[j]))throw std::invalid_argument("Rebase overflows saved cluster inertia");
  for(int j=0;j<2;j++)if(!finiteVector(c.velocityImpulses[j])||!finiteVector(c.driftImpulses[j]))throw std::invalid_argument("Rebase overflows saved cluster impulses");}
 for(auto& b:w.soft)for(int j=0;j<b->m_nodes.size();j++)validate(b->m_nodes[j]);
 for(auto& h:w.history)for(const auto& n:h.nodes)validate(n);
 for(auto& h:w.history)for(const auto& n:h.faceNormals)if(!finiteVector(r*n))throw std::invalid_argument("Rebase overflows saved face normal");
 for(auto& value:w.soft){auto& b=*value;const btScalar margin=b.getCollisionShape()->getMargin();
  for(int j=0;j<b.m_nodes.size();j++){auto& n=b.m_nodes[j];nodeTransform(n);auto volume=btDbvtVolume::FromCR(n.m_x,margin);b.m_ndbvt.update(n.m_leaf,volume);}
  for(int j=0;j<b.m_faces.size();j++)b.m_faces[j].m_normal=r*b.m_faces[j].m_normal;
  for(int j=0;j<b.m_faces.size();j++){auto& f=b.m_faces[j];if(f.m_leaf){btVector3 points[]={f.m_n[0]->m_x,f.m_n[1]->m_x,f.m_n[2]->m_x};auto volume=btDbvtVolume::FromPoints(points,3);volume.Expand(btVector3(margin,margin,margin));b.m_fdbvt.update(f.m_leaf,volume);}}
  for(int j=0;j<b.m_clusters.size();j++){auto& c=*b.m_clusters[j];clusterTransform(c);if(c.m_leaf){btVector3 lo=c.m_nodes[0]->m_x,hi=lo;for(int k=1;k<c.m_nodes.size();k++){lo.setMin(c.m_nodes[k]->m_x);hi.setMax(c.m_nodes[k]->m_x);}auto volume=btDbvtVolume::FromMM(lo,hi);volume.Expand(btVector3(margin,margin,margin));b.m_cdbvt.update(c.m_leaf,volume);}}
  // Link/anchor impulse matrices are derived on the next solveConstraints.
  // They may be uninitialized before the first owner step; never read them here.
  if(b.m_pose.m_bframe){b.m_pose.m_com=t*b.m_pose.m_com;b.m_pose.m_rot=r*b.m_pose.m_rot;}
  b.updateBounds();w.world->updateSingleAabb(&b);
 }
 for(auto& h:w.history){for(auto& n:h.nodes)nodeTransform(n);for(auto& c:h.clusters)c.transform(t);for(auto& n:h.faceNormals)n=r*n;}
});}
