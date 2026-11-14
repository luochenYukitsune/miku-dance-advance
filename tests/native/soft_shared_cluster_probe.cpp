// Inspect the real bridge-owned Bullet objects, without a production observer API.
// Build with the bridge's Bullet inputs/cache patches, omitting mmd_bullet_soft.cpp
// from the compiler source list because it is included here.
#include "../../native/Mmd.Modern.Bullet.SoftBody/mmd_bullet_soft.cpp"
#include <cstdio>

static void require(bool value,const char* reason){if(!value)throw std::runtime_error(reason);}
static void check(int status){if(status)throw std::runtime_error(mmd_soft_error());}

int main(){try{
 const float gravity[]={0,0,0};
 const float points[]={0,.01f,0,1,.01f,0,0,.01f,1,1,.01f,1,2,.01f,0,2,.01f,1};
 const int offsets[]={0,4,8},nodes[]={0,1,2,3,1,4,3,5},faceOffsets[]={0,6,12};
 const int triangles[]={0,1,2,1,3,2,0,1,2,1,3,2};
 const float values[]={4,.02f,1,.01f,0,0,0,0,.2f,0,1,.1f,1,.7f,1,.1f,.5f,.5f,.5f,.5f,.2f,.3f,.4f,
                       8,.02f,1,.01f,0,0,0,0,.2f,0,1,.1f,1,.7f,1,.1f,.5f,.5f,.5f,.5f,.8f,.7f,.6f};
 int cases=0,totalClusters=0,totalPlane=0,totalConvex=0;
 for(int requested=0;requested<3;requested++){
  void* handle=nullptr;check(mmd_soft_create(gravity,1,0,&handle));
  std::unique_ptr<World> cleanup(static_cast<World*>(handle));
  int options[]={0,3,2,requested,0,0,65535,0,4,0,4,0,3,2,2-requested,0,0,65535,0,4,0,4};
  require((mmd_soft_features()&16)!=0,"shared-cluster capability missing");
  check(mmd_soft_add_shared(handle,points,6,2,offsets,nodes,8,faceOffsets,triangles,12,options,values,nullptr,0,nullptr,0));
  auto& w=ctx(handle);auto& merged=body(w,0);
  require(merged.m_nodes.size()==6&&merged.m_faces.size()==4&&merged.m_links.size()==12&&merged.m_materials.size()==2,"source topology/material/bending lost");
  const float expectedMass[]={1,3,1,3,2,2};
  for(int i=0;i<6;i++)require(btFabs(1/merged.m_nodes[i].m_im-expectedMass[i])<.000001f,"shared mass not additive");
  require(merged.m_materials[0]->m_kLST==.2f&&merged.m_materials[1]->m_kLST==.8f,"independent materials lost");
  int destination=0,colliding=0;
  for(int part=0;part<2;part++){
   btVector3 local[4];for(int j=0;j<4;j++)local[j]=vec(points+nodes[part*4+j]*3);
   btSoftBody independent(&w.world->getWorldInfo(),4,local,nullptr);
   for(int j=0;j<6;j+=3){int a=triangles[part*6+j],b=triangles[part*6+j+1],c=triangles[part*6+j+2];
    independent.appendLink(a,b,nullptr,true);independent.appendLink(b,c,nullptr,true);independent.appendLink(c,a,nullptr,true);independent.appendFace(a,b,c);}
   independent.generateBendingConstraints(2,independent.m_materials[0]);
   independent.generateClusters(options[part*11+3]);
   for(int c=0;c<independent.m_clusters.size();c++,destination++){
    require(destination<merged.m_clusters.size(),"per-source cluster missing");
    const auto& expected=*independent.m_clusters[c];const auto& actual=*merged.m_clusters[destination];
    require(actual.m_collide==expected.m_collide&&actual.m_nodes.size()==expected.m_nodes.size(),"master/expanded cluster grouping changed");
    require(actual.m_masses.size()==actual.m_nodes.size()&&actual.m_framerefs.size()==actual.m_nodes.size(),"clusters not initialized with merged masses");
    for(int j=0;j<expected.m_nodes.size();j++){
     int localId=int(expected.m_nodes[j]-&independent.m_nodes[0]);
     require(actual.m_nodes[j]==&merged.m_nodes[nodes[part*4+localId]],"cluster node identity/order differs from upstream source");
    }
    require(actual.m_clusterIndex==destination,"cluster solver index incorrect");
    if(actual.m_collide){colliding++;require(actual.m_leaf!=nullptr,"colliding cluster absent from DBVT");}
   }
  }
  require(destination==merged.m_clusters.size(),"unexpected merged/global cluster");
  require(btDbvt::countLeaves(merged.m_cdbvt.m_root)==colliding,"cluster DBVT differs from actual collidable membership");
  int size=merged.m_clusters.size();require(merged.m_clusterConnectivity.size()==size*size,"connectivity missing");
  bool seamConnected=false;
  for(int i=0;i<size;i++)for(int j=0;j<size;j++){
   bool shares=false;for(int a=0;a<merged.m_clusters[i]->m_nodes.size();a++)for(int b=0;b<merged.m_clusters[j]->m_nodes.size();b++)
    if(merged.m_clusters[i]->m_nodes[a]==merged.m_clusters[j]->m_nodes[b])shares=true;
   require(merged.m_clusterConnectivity[i+j*size]==shares,"connectivity differs from shared solver pointer identity");
   if(i!=j&&shares)seamConnected=true;
  }
  require(seamConnected,"shared seam connectivity absent");
  const int flags=btSoftBody::fCollision::CL_RS|btSoftBody::fCollision::CL_SS;
  require(merged.m_cfg.collisions==flags,"shared body lost cluster collision switches");
  merged.predictMotion(1.f/120);w.world->performDiscreteCollisionDetection();
  require(merged.m_rcontacts.size()>0,"shared cluster plane contact absent");
  require(merged.m_cfg.collisions==flags,"analytic plane changed cluster flags");
  totalPlane+=merged.m_rcontacts.size();
  btSphereShape sphereShape(.5f);btRigidBody::btRigidBodyConstructionInfo ci(0,nullptr,&sphereShape);btRigidBody sphere(ci);
  sphere.setWorldTransform(btTransform(btQuaternion::getIdentity(),btVector3(1,.1f,.5f)));
  sphere.setInterpolationWorldTransform(sphere.getWorldTransform());w.world->addRigidBody(&sphere);
  w.world->performDiscreteCollisionDetection();
  require(merged.m_joints.size()>0,"actual CL_RS convex CJoint absent");
  totalConvex+=merged.m_joints.size();w.world->removeRigidBody(&sphere);
  auto* live=&merged;options[12]=1;
  require(mmd_soft_add_shared(handle,points,6,2,offsets,nodes,8,faceOffsets,triangles,12,options,values,nullptr,0,nullptr,0)!=0,"mixed cluster collision switch accepted");
  require(w.soft.size()==1&&&body(w,0)==live,"failed candidate changed active native body");
  totalClusters+=size;cases++;
 }
 std::printf("{\"cases\":%d,\"sourceCounts\":[0,1,2],\"actualClusters\":%d,\"planeNodeContacts\":%d,\"convexClusterJoints\":%d,\"membershipOrderAndMasters\":true,\"sharedPointerConnectivity\":true,\"clusterTree\":true,\"rollback\":true}\n",cases,totalClusters,totalPlane,totalConvex);
 return 0;
}catch(const std::exception& error){std::fprintf(stderr,"%s\n",error.what());return 1;}}
