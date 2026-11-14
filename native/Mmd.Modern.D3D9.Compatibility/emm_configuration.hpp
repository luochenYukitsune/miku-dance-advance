// Project-owned copied EMM transport. No parser, renderer, discovery or file I/O.
#pragma once
#include "mda_d3d9_session.h"
#include "frame_packet.hpp"
#include "mmeffect/emm_manager.h"
#include <algorithm>
#include <map>
#include <set>
namespace emmcfg {
struct AbiMismatch:std::invalid_argument{using std::invalid_argument::invalid_argument;};
[[noreturn]] inline void Invalid(const char* text){throw std::invalid_argument(std::string("EMM configuration: ")+text);}
template<class T>inline void Structure(const T* value){if(!value)Invalid("null record");if(value->size<sizeof(T)||value->version!=1)throw AbiMismatch("EMM record size/version mismatch");}
template<class T>inline void Span(const T* value,uint64_t count){if(count>65536||(!value&&count))Invalid("invalid or oversized table span");}
inline bool Key(const std::string& value,int kind=0){
 if(value.size()<4||(value.compare(0,3,"Pmd")&&value.compare(0,3,"Acs")))return false;
 if(kind&&(value.compare(0,3,kind==1?"Pmd":"Acs")!=0))return false;
 if(value.size()>4&&value[3]=='0')return false;
 uint64_t slot=0;for(size_t i=3;i<value.size();++i){if(value[i]<'0'||value[i]>'9')return false;slot=slot*10+uint32_t(value[i]-'0');if(slot>INT32_MAX)return false;}return true;
}
inline bool Label(const std::string& label,const std::string& base){
 if(base.empty()||label.compare(0,base.size(),base))return false;
 for(unsigned char c:base)if(!(c=='_'||(c>='A'&&c<='Z')||(c>='a'&&c<='z')||(c>='0'&&c<='9')))return false;
 if(label.size()==base.size())return true;
 if(label.size()<base.size()+3||label[base.size()]!='('||label.back()!=')')return false;
 for(size_t i=base.size()+1;i+1<label.size();++i)if(label[i]<'0'||label[i]>'9')return false;return true;
}
inline bool TargetLabel(const std::string& label){return !label.empty()&&Label(label,label.substr(0,label.find('(')));}
struct Resource {std::string origin,local;};
struct State {
 std::vector<mme::EmmEffectSection> sections;
 std::map<std::string,mme::EmmMappedObject> objects;
 std::map<unsigned long long,mme::EmmRootSeed> rootSeeds;
 std::vector<Resource> resources;
 uint64_t copiedBytes=0;
};
class Copier {
 State state;
 uint64_t totalEntries=0;
 std::string Text(Mda9Utf8SpanV1 span,bool required=false){
  if(span.length>32767||(!span.data&&span.length)||(required&&!span.length))Invalid("invalid UTF8 string span");
  if(span.length>16ULL*1024*1024-state.copiedBytes)Invalid("combined copied-string budget exceeded");state.copiedBytes+=span.length;
  if(!span.length)return {};
  std::string value(span.data,static_cast<size_t>(span.length));
  if(value.find('\0')!=std::string::npos||!packet::detail::validUtf8(reinterpret_cast<const uint8_t*>(value.data()),value.size()))Invalid("invalid UTF8 or NUL in metadata");return value;
 }
 void Absent(Mda9Utf8SpanV1 span){if(span.data||span.length)Invalid("absent value must have a null/zero span");}
public:
 State Copy(const Mda9EmmConfigurationV1* value){
  Structure(value);Span(value->sections,value->sectionCount);Span(value->bindings,value->bindingCount);Span(value->rootSeeds,value->rootSeedCount);Span(value->resources,value->resourceCount);
  if(value->reserved[0]||value->reserved[1]||!value->sectionCount)Invalid("reserved fields or missing required section table");
  std::set<uint64_t> mapped;
  for(uint64_t i=0;i<value->bindingCount;++i){const auto& row=value->bindings[i];Structure(&row);const auto key=Text(row.key,true);
   if(row.reserved||!row.objectId||(row.kind!=1&&row.kind!=2)||!Key(key,row.kind)||!mapped.insert(row.objectId).second||!state.objects.emplace(key,mme::EmmMappedObject{row.objectId,static_cast<int>(row.kind)}).second)Invalid("invalid, duplicate or wrong-kind record identity");
  }
  for(uint64_t i=0;i<value->rootSeedCount;++i){const auto& row=value->rootSeeds[i];Structure(&row);auto path=Text(row.effectPath);
   if(row.reserved||!row.objectId||(row.kind!=1&&row.kind!=2)||path=="none"||path=="hide"||path=="main_default"||!state.rootSeeds.emplace(row.objectId,mme::EmmRootSeed{static_cast<int>(row.kind),std::move(path)}).second)Invalid("invalid or duplicate frozen root seed");
  }
  for(const auto& row:state.objects){auto found=state.rootSeeds.find(row.second.objectId);if(found==state.rootSeeds.end()||found->second.kind!=row.second.kind)Invalid("mapped object requires its matching frozen root seed");}
  std::set<std::string> names;std::map<std::string,size_t> labels;unsigned defaults=0;
  state.sections.reserve(static_cast<size_t>(value->sectionCount));
  for(uint64_t i=0;i<value->sectionCount;++i){const auto& row=value->sections[i];Structure(&row);Span(row.entries,row.entryCount);
   if(row.reserved[0]||row.reserved[1]||row.flags&~uint32_t(MDA9_EMM_DEFAULT_PRESENT)||row.ownerSubset< -1||row.entryCount>65536-totalEntries)Invalid("invalid section flags, subset or entry budget");totalEntries+=row.entryCount;
   mme::EmmEffectSection section;section.name=Text(row.name,true);section.baseName=Text(row.baseName);section.ownerKey=Text(row.ownerKey);section.ownerMaterialName=Text(row.parentTarget);section.ownerSubsetIndex=row.ownerSubset;
   if(!names.insert(section.name).second)Invalid("duplicate section name");
   if(section.ownerKey.empty()){
    if(row.ownerSubset!=-1||!section.ownerMaterialName.empty())Invalid("Owner subset/parent requires an Owner key");
   }else if(!Key(section.ownerKey)||!state.objects.count(section.ownerKey)||(!section.ownerMaterialName.empty()&&!TargetLabel(section.ownerMaterialName)))Invalid("invalid Owner record key or parent label");
   if(section.isDefault()){
    // The accepted parser can retain an Owner on base Effect. Its apply tail
    // ignores that field; preserve it rather than introducing a stricter parser.
    if(section.name!="Effect"||row.flags!=MDA9_EMM_DEFAULT_PRESENT)Invalid("invalid base Effect section");
    section.defaultPath=Text(row.defaultPath,true);++defaults;
   }else {
    if(row.flags||section.name.compare(0,7,"Effect@")||!Label(section.name.substr(7),section.baseName)||!Key(section.ownerKey)||!state.objects.count(section.ownerKey))Invalid("invalid named section or Owner");
    Absent(row.defaultPath);labels.emplace(section.name.substr(7),state.sections.size());
   }
   std::set<std::pair<std::string,int32_t>> rows;std::map<std::string,size_t> entries;
   for(uint64_t j=0;j<row.entryCount;++j){const auto& input=row.entries[j];Structure(&input);auto key=Text(input.key,true);
    if(input.subset< -1||!Key(key)||!state.objects.count(key)||!rows.emplace(key,input.subset).second||
       input.flags&~uint32_t(7)||!(input.flags&(MDA9_EMM_PATH_PRESENT|MDA9_EMM_SHOW_PRESENT))||((input.flags&MDA9_EMM_SHOW_VALUE)&&!(input.flags&MDA9_EMM_SHOW_PRESENT)))Invalid("invalid, duplicate or unknown assignment row");
    auto position=entries.find(key);if(position==entries.end()){mme::EmmEffectEntry entry;entry.key=key;position=entries.emplace(key,section.entries.size()).first;section.entries.push_back(std::move(entry));}
    auto& entry=section.entries[position->second];
    if(input.flags&MDA9_EMM_PATH_PRESENT){auto path=Text(input.path,true);if(input.subset==-1)entry.effectPath=std::move(path);else entry.subsetPaths.emplace(input.subset,std::move(path));}else Absent(input.path);
    if(input.flags&MDA9_EMM_SHOW_PRESENT){const bool shown=(input.flags&MDA9_EMM_SHOW_VALUE)!=0;if(input.subset==-1){entry.hasShow=true;entry.shown=shown;}else entry.subsetShows.emplace(input.subset,shown);}
   }
   state.sections.push_back(std::move(section));
  }
  if(defaults!=1)Invalid("exactly one base Effect section is required");
  // Iterative tri-color traversal visits each section once, including a chain
  // at the full admitted table bound. No recursion or quadratic ancestor walk.
  std::vector<uint8_t> colors(state.sections.size(),0);
  for(const auto& label:labels){
   size_t cursor=label.second;if(colors[cursor]==2)continue;std::vector<size_t> chain;
   for(;;){
    if(colors[cursor]==1)Invalid("Owner parent cycle");if(colors[cursor]==2)break;
    colors[cursor]=1;chain.push_back(cursor);
    const auto& parent=state.sections[cursor].ownerMaterialName;if(parent.empty())break;
    const auto found=labels.find(parent);if(found==labels.end())Invalid("unknown Owner parent label");cursor=found->second;
   }
   for(const auto index:chain)colors[index]=2;
  }
  state.resources.reserve(static_cast<size_t>(value->resourceCount));
  for(uint64_t i=0;i<value->resourceCount;++i){const auto& row=value->resources[i];Structure(&row);if(row.reserved[0]||row.reserved[1])Invalid("resource reserved fields");state.resources.push_back({Text({row.originUtf8,row.originLength},true),Text({row.materializedUtf8,row.materializedLength},true)});}
  return std::move(state);
 }
};
inline State Copy(const Mda9EmmConfigurationV1* value){return Copier{}.Copy(value);}
inline void ValidateFrame(const State& state,const packet::Frame& frame){
 std::map<uint64_t,const packet::Object*> live;
 for(const auto& object:frame.objects){if(!live.emplace(object.id,&object).second)Invalid("duplicate frozen-frame object identity");auto seed=state.rootSeeds.find(object.id);if(seed==state.rootSeeds.end()||seed->second.kind!=static_cast<int>(object.kind))Invalid("every live frozen-frame object requires its matching root seed");}
 if(live.size()!=state.rootSeeds.size())Invalid("frozen root seed is absent from frame");
 for(const auto& row:state.objects){const auto object=live.find(row.second.objectId);if(object==live.end()||object->second->kind!=uint32_t(row.second.kind))Invalid("mapped record is absent or has wrong frozen-frame kind");}
 const auto subset=[&](const std::string& key,int index){if(index<0)return;const auto& binding=state.objects.at(key);const auto* object=live.at(binding.objectId);if(object->kind==packet::Model&&!object->visible&&!object->materialCount)Invalid("hidden model subset requires retained material metadata; zero is not a real subset count");if(uint32_t(index)>=object->materialCount)Invalid("subset is outside actual frozen-frame material table");};
 for(const auto& section:state.sections){if(!section.isDefault())subset(section.ownerKey,section.ownerSubsetIndex);for(const auto& entry:section.entries){for(const auto& row:entry.subsetPaths)subset(entry.key,row.first);for(const auto& row:entry.subsetShows)subset(entry.key,row.first);}}
}
}
