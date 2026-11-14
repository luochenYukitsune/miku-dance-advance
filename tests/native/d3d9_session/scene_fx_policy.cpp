// Project-authored CPU checks of actual parsed-SAS admission; no device calls.
#include "mda_object_fx_policy.hpp"
#include <iostream>

int main() {
 int checks=0,failures=0;
 auto check=[&](bool value,const char* name){++checks;if(!value){++failures;std::cerr<<"FAIL "<<name<<'\n';}};
 mme::SasEffect valid;valid.effect=reinterpret_cast<ID3DXEffect*>(uintptr_t(1));
 check(mme::MdaSceneFxAdmission(&valid).empty(),"valid compiled effect admitted");
 check(!mme::MdaSceneFxAdmission(nullptr).empty(),"null effect rejected");
 auto invalid=valid;invalid.effect=nullptr;
 check(!mme::MdaSceneFxAdmission(&invalid).empty(),"missing compiled effect rejected");
 invalid=valid;invalid.hasErrors=true;
 check(!mme::MdaSceneFxAdmission(&invalid).empty(),"parse failure rejected");
 invalid=valid;invalid.runFailed=true;
 check(!mme::MdaSceneFxAdmission(&invalid).empty(),"execution failure rejected");
 auto scene=valid;scene.scriptClass=mme::kSasClassScene;
 scene.scriptOrder=mme::kSasOrderPreprocess;scene.postEffect=true;
 scene.controls.emplace_back();scene.resources.emplace_back();scene.resources.back().semanticId=0x2e;
 scene.techniques.emplace_back();scene.techniques.back().cmds.push_back({mme::kSasCmdScriptExternal});
 check(mme::MdaSceneFxAdmission(&scene).empty(),"existing scene planner features admitted together");
 check(!mme::MdaObjectFxParsedAdmission(&scene).empty(),"finite object contract remains restrictive");
 scene.hasErrors=true;
 check(!mme::MdaSceneFxAdmission(&scene).empty(),"scene features do not bypass parse failure");
 scene.hasErrors=false;scene.runFailed=true;
 check(!mme::MdaSceneFxAdmission(&scene).empty(),"scene features do not bypass runtime failure");
 std::cout<<"checks="<<checks<<" failures="<<failures<<'\n';return failures?1:0;
}
