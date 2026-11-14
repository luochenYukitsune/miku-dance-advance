// Original CPU-only actual parsed-SAS policy fixtures. No effect method/device.
#include "mda_object_fx_policy.hpp"
#include <iostream>
int main(){
 int failures=0,checks=0;
 auto check=[&](bool value,const char* name){++checks;if(!value){++failures;std::cerr<<"FAIL "<<name<<'\n';}};
 mme::SasEffect plain;plain.effect=reinterpret_cast<ID3DXEffect*>(uintptr_t(1));
 check(mme::MdaObjectFxParsedAdmission(&plain).empty(),"default object/standard admitted");
 auto rejects=[&](const mme::SasEffect& model,const char* name){check(!mme::MdaObjectFxParsedAdmission(&model).empty(),name);};
 auto model=plain;model.scriptClass=mme::kSasClassScene;rejects(model,"scene rejected");
 model=plain;model.scriptClass=mme::kSasClassSceneOrObject;rejects(model,"sceneorobject rejected");
 model=plain;model.scriptOrder=mme::kSasOrderPreprocess;rejects(model,"preprocess rejected");
 model=plain;model.postEffect=true;rejects(model,"postEffect rejected");
 model=plain;model.runFailed=true;rejects(model,"runFailed rejected");
 model=plain;model.hasErrors=true;rejects(model,"parse errors rejected");
 model=plain;model.controls.emplace_back();rejects(model,"control rejected");
 model=plain;model.resources.emplace_back();model.resources.back().semanticId=0x2e;rejects(model,"offscreen rejected");
 model=plain;model.resources.emplace_back();model.resources.back().semanticId=0x33;rejects(model,"readback feedback rejected");
 model=plain;model.techniques.emplace_back();model.techniques.back().cmds.push_back({mme::kSasCmdScriptExternal});rejects(model,"technique external rejected");
 model=plain;model.techniques.emplace_back();model.techniques.back().passes.emplace_back();model.techniques.back().passes.back().cmds.push_back({mme::kSasCmdScriptExternal});rejects(model,"compiled pass external rejected");
 model=plain;model.techniques.emplace_back();model.techniques.back().passes.emplace_back();model.techniques.back().passes.back().scriptText=" Pass=Test; ScriptExternal = color;";rejects(model,"warning-only pass external rejected");
 check(!mme::MdaObjectFxHasExternalToken("Pass=scriptExternalColor;clear=color;"),"value/name does not guess feature");
 check(mme::MdaObjectFxHasExternalToken("pass=Draw;\tScRiPtExTeRnAl = color;"),"annotation key token checked");
 std::cout<<"checks="<<checks<<" failures="<<failures<<'\n';return failures?1:0;
}
