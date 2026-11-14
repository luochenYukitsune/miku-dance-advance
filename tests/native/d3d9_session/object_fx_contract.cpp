// Original CPU-only configured-object contract. No DLL, device or user assets.
#include "object_fx.hpp"
#include <iostream>
namespace {
int checks=0, failures=0;
void check(bool value,const char* name){++checks;if(!value){++failures;std::cerr<<"FAIL "<<name<<'\n';}}
template<class F> void rejects(F action,const char* name){try{action();check(false,name);}catch(const std::runtime_error&){check(true,name);}}
objectfx::Row row(uint64_t object,int subset,uint32_t assignment,const char* path=""){
 return {object,subset,assignment,path};
}
}
int main(){
 using namespace objectfx;
 std::vector<Row> rows{row(17,-1,File,"C:\\original\\whole.fx"),row(17,0,Standard),row(17,1,Inherit)};
 ValidateRows(rows);
 check(Resolve(rows,17,0)==nullptr,"explicit standard overrides whole");
 check(Resolve(rows,17,1)&&Resolve(rows,17,1)->path=="C:\\original\\whole.fx","inherit preserves whole");
 check(Resolve(rows,18,0)==nullptr,"unconfigured object is standard");
 rejects([&]{auto copy=rows;copy.push_back(rows[0]);ValidateRows(copy);},"duplicate whole rejected");
 rejects([&]{ValidateRows({row(17,0,Standard),row(17,0,Inherit)});},"duplicate subset rejected");
 rejects([&]{ValidateRows({row(0,-1,Standard)});},"zero object rejected");
 rejects([&]{ValidateRows({row(17,-2,Standard)});},"negative subset rejected");
 rejects([&]{ValidateRows({row(17,-1,9)});},"unknown assignment rejected");
 rejects([&]{ValidateRows({row(17,0,Standard,"C:\\unexpected.fx")});},"standard cannot carry path");
 rejects([&]{ValidateRows({row(17,0,File)});},"file requires path");
 rejects([&]{ValidateRows({row(17,0,File,"relative.fx")});},"relative file rejected");
 rejects([&]{ValidateRows({Row{17,0,File,std::string("C:\\x\0.fx",8)}});},"embedded NUL rejected");
 packet::Frame frame{};frame.objects.push_back({17,0,2,1,"C:\\original\\model.pmx","",{}, {}});
 ValidateFrame(rows,frame);check(true,"whole and subset admitted for matching object");
 rejects([&]{auto copy=frame;copy.objects[0].materialCount=1;ValidateFrame(rows,copy);},"out of range subset rejected");
 rejects([&]{auto copy=frame;copy.objects[0].id=18;ValidateFrame(rows,copy);},"stale object assignment rejected");
 auto hidden=frame;hidden.objects[0].visible=0;hidden.objects[0].materialCount=0;
 ValidateFrame(rows,hidden);check(true,"hidden subset retained without synthetic material count");
 rejects([&]{hidden.objects[0].visible=1;ValidateFrame(rows,hidden);},"revealed object range checked again");
 std::cout<<"checks="<<checks<<" failures="<<failures<<'\n';return failures?1:0;
}
