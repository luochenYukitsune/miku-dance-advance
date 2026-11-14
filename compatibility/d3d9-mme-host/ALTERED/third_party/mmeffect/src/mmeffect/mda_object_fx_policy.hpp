// Project-owned admission policies for the opt-in finite-object and scene FX.
// Inspect the actual D3DX/SAS model; never infer features from source text.
#pragma once
#include "sas_exec.h"
#include "mda_mme_bridge_v1.h"
#include <algorithm>
#include <cctype>
#include <set>
namespace mme {
// One host/session per DLL, on its owning OS thread. Configuration supplies
// an owned immutable local-file allowlist: assigned FX in finite-object mode,
// all mapped package files in scene mode. Neither mode permits origin opens.
inline std::set<std::string> MdaObjectFxAllowedPaths;
// General execution reuses the existing SAS parser/planner. Retain validity
// and execution-failure gates without imposing the finite object's feature
// restrictions or its requirement to enumerate every declared technique.
inline std::string MdaSceneFxAdmission(const SasEffect* sas) {
 if(!sas||!sas->effect||sas->hasErrors)return "scene FX requires a valid compiled SAS effect";
 if(sas->runFailed)return "scene FX script execution failed";
 return {};
}
inline bool MdaObjectFxHasExternalToken(const std::string& annotation) {
 // This is the actual parsed Script annotation, not .fx source. The legacy
 // compiler warns and drops pass-level ScriptExternal; retain that rejection
 // even when no compiled command survives. Match a complete command key.
 size_t start=0;
 while(start<annotation.size()){
  const auto end=annotation.find(';',start);
  const auto stop=end==std::string::npos?annotation.size():end;
  size_t at=start;while(at<stop&&std::isspace(static_cast<unsigned char>(annotation[at])))++at;
  const size_t keyStart=at;while(at<stop&&(std::isalnum(static_cast<unsigned char>(annotation[at]))||annotation[at]=='_'))++at;
  std::string key=annotation.substr(keyStart,at-keyStart);
  for(auto& c:key)c=static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
  while(at<stop&&std::isspace(static_cast<unsigned char>(annotation[at])))++at;
  if(key=="scriptexternal"&&at<stop&&annotation[at]=='=')return true;
  if(end==std::string::npos)break;start=end+1;
 }
 return false;
}
inline std::string MdaObjectFxParsedAdmission(const SasEffect* sas) {
 if(!sas||!sas->effect||sas->hasErrors)return "object FX requires a valid compiled SAS effect";
 if(sas->scriptClass!=kSasClassObject||sas->scriptOrder!=kSasOrderStandard||sas->postEffect)
  return "object FX supports only ScriptClass object and ScriptOrder standard";
 if(sas->runFailed)return "object FX script execution failed";
 if(!sas->controls.empty())return "object FX CONTROLOBJECT is not supported";
 for(const auto& resource:sas->resources){
  if(resource.semanticId==0x2e)return "object FX OFFSCREENRENDERTARGET is not supported";
  if(resource.semanticId==0x33)return "object FX TEXTUREVALUE feedback is not supported";
 }
 for(const auto& technique:sas->techniques){
  for(const auto& command:technique.cmds)if(command.id==kSasCmdScriptExternal)return "object FX technique ScriptExternal is not supported";
  for(const auto& pass:technique.passes)for(const auto& command:pass.cmds)
   if(command.id==kSasCmdScriptExternal)return "object FX pass ScriptExternal is not supported";
  for(const auto& pass:technique.passes)if(MdaObjectFxHasExternalToken(pass.scriptText))
   return "object FX pass ScriptExternal is not supported";
 }
 return {};
}
inline std::string MdaObjectFxScriptAnnotations(ID3DXEffect* effect,D3DXHANDLE object,UINT count){
 for(UINT i=0;i<count;++i){
  const auto annotation=effect->GetAnnotation(object,i);D3DXPARAMETER_DESC description{};
  if(!annotation||FAILED(effect->GetParameterDesc(annotation,&description)))return "object FX annotation enumeration failed";
  std::string name=description.Name?description.Name:"";
  for(auto& c:name)c=static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
  if(name!="script")continue;
  const char* text=nullptr;
  if(description.Type!=D3DXPT_STRING||FAILED(effect->GetString(annotation,&text))||!text)return "object FX Script annotation is not a readable string";
  if(MdaObjectFxHasExternalToken(text))return "object FX technique/pass ScriptExternal is not supported";
 }
 return {};
}
inline std::string MdaObjectFxAdmission(const SasEffect* sas) {
 const auto parsed=MdaObjectFxParsedAdmission(sas);
 if(!parsed.empty())return parsed;
 // STANDARDSGLOBAL Script may replace the parser's technique list. Until all
 // declared techniques are parsed, reject that reduced representation rather
 // than accidentally admitting an excluded ScriptExternal technique/pass.
 D3DXEFFECT_DESC description{};
 if(FAILED(sas->effect->GetDesc(&description)))return "object FX technique enumeration failed";
 for(UINT i=0;i<description.Techniques;++i){
  const auto handle=sas->effect->GetTechnique(i);
  const auto found=std::find_if(sas->techniques.begin(),sas->techniques.end(),[&](const auto& technique){return technique.handle==handle;});
  if(!handle||found==sas->techniques.end())return "object FX requires every declared technique to be parsed";
  D3DXTECHNIQUE_DESC technique{};
  if(FAILED(sas->effect->GetTechniqueDesc(handle,&technique)))return "object FX technique description failed";
  auto rejection=MdaObjectFxScriptAnnotations(sas->effect,handle,technique.Annotations);
  if(!rejection.empty())return rejection;
  for(UINT passIndex=0;passIndex<technique.Passes;++passIndex){
   const auto pass=sas->effect->GetPass(handle,passIndex);D3DXPASS_DESC passDescription{};
   if(!pass||FAILED(sas->effect->GetPassDesc(pass,&passDescription)))return "object FX pass description failed";
   rejection=MdaObjectFxScriptAnnotations(sas->effect,pass,passDescription.Annotations);
   if(!rejection.empty())return rejection;
  }
 }
 return {};
}
}
