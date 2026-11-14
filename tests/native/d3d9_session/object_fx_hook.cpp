// Original CPU-only optional bridge registration test. Link only bridge.cpp.
#ifndef __cdecl
#define __cdecl
#endif
#include "mda_mme_bridge_v1.h"
#include <iostream>
namespace {
int calls=0;
int32_t __cdecl apply(void* context){++calls;return *static_cast<int32_t*>(context);}
int failures=0;
void check(bool value,const char* name){if(!value){++failures;std::cerr<<"FAIL "<<name<<'\n';}}
}
int main(){
 MdaMmeUnbindV1();
 check(MdaMmeApplyObjectFxV1()==MDA_MME_OK,"absent optional hook preserves legacy");
 int32_t result=0;MdaMmeObjectFxCallbacksV1 hook{sizeof hook,MDA_MME_BRIDGE_VERSION,&result,apply};
 check(MdaMmeBindObjectFxV1(&hook)==MDA_MME_OK,"optional hook binds separately");
 check(MdaMmeExplicitObjectFxV1()==1,"explicit policy active before registration");
 check(MdaMmeBindObjectFxV1(&hook)==MDA_MME_ALREADY_BOUND,"hook immutable until destroy");
 check(MdaMmeApplyObjectFxV1()==MDA_MME_OK&&calls==1,"first frame applies");
 check(MdaMmeApplyObjectFxV1()==MDA_MME_OK&&calls==2,"next generation reapplies");
 result=-25;MdaMmeBeginFrameV1();
 check(MdaMmeApplyObjectFxV1()==MDA_MME_CALLBACK_FAILED,"hook failure explicit");
 MdaMmeErrorV1 error{sizeof error,MDA_MME_BRIDGE_VERSION,0,0,0,0,0};MdaMmeGetErrorV1(&error);
 check(error.status==MDA_MME_CALLBACK_FAILED&&error.callback_status==-25&&error.query==MDA_MME_QUERY_OBJECT_FX,"hook original status latched");
 MdaMmeUnbindV1();
 check(!MdaMmeExplicitObjectFxV1()&&MdaMmeApplyObjectFxV1()==MDA_MME_OK,"destroy removes hook and policy");
 hook.apply=nullptr;check(MdaMmeBindObjectFxV1(&hook)==MDA_MME_INVALID,"null hook rejected");
 return failures?1:0;
}
