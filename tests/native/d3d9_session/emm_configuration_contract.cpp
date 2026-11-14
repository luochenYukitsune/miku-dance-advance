// CPU-only copied EMM transport contract; no DLL, renderer, device or file I/O.
#include "emm_configuration.hpp"
#include <iostream>
namespace {
int checks=0,failures=0;
void check(bool value,const char* name){++checks;if(!value){++failures;std::cerr<<"FAIL "<<name<<'\n';}}
template<class F>void rejects(F action,const char* name){try{action();check(false,name);}catch(const std::invalid_argument&){check(true,name);}}
Mda9Utf8SpanV1 text(const char* value){return {value,static_cast<uint64_t>(std::strlen(value))};}
}
int main(){
 Mda9EmmBindingV1 bindings[]={{sizeof(Mda9EmmBindingV1),1,text("Pmd17"),9001,1,0},{sizeof(Mda9EmmBindingV1),1,text("Acs2"),5002,2,0}};
 Mda9EmmRootSeedV1 seeds[]={{sizeof(Mda9EmmRootSeedV1),1,9001,1,0,{}},{sizeof(Mda9EmmRootSeedV1),1,5002,2,0,text("C:\\local\\seed.fx")},{sizeof(Mda9EmmRootSeedV1),1,7003,1,0,{}}};
 Mda9EmmEntryV1 baseRows[]={{sizeof(Mda9EmmEntryV1),1,text("Pmd17"),0,MDA9_EMM_SHOW_PRESENT|MDA9_EMM_SHOW_VALUE,{}},{sizeof(Mda9EmmEntryV1),1,text("Acs2"),-1,MDA9_EMM_PATH_PRESENT|MDA9_EMM_SHOW_PRESENT,text("none")}};
 Mda9EmmEntryV1 targetRows[]={{sizeof(Mda9EmmEntryV1),1,text("Pmd17"),-1,MDA9_EMM_PATH_PRESENT,text("main_default")}};
 Mda9EmmSectionV1 sections[]={{sizeof(Mda9EmmSectionV1),1,text("Effect"),{},{},{},text("none"),-1,MDA9_EMM_DEFAULT_PRESENT,baseRows,2,{}},{sizeof(Mda9EmmSectionV1),1,text("Effect@Depth(2)"),text("Depth"),text("Acs2"),{}, {},-1,0,targetRows,1,{}}};
 Mda9EffectResourceBindingV1 resource{sizeof(Mda9EffectResourceBindingV1),1,"D:\\original\\seed.fx",19,"C:\\local\\seed.fx",16,{}};
 resource.originLength=std::strlen(resource.originUtf8);resource.materializedLength=std::strlen(resource.materializedUtf8);
 Mda9EmmConfigurationV1 configuration{sizeof(Mda9EmmConfigurationV1),1,sections,2,bindings,2,seeds,3,&resource,1,{}};
 auto copied=emmcfg::Copy(&configuration);
 check(copied.sections.size()==2&&copied.objects.size()==2&&copied.rootSeeds.size()==3,"copied complete sections and unmatched root seed");
 check(copied.objects.at("Pmd17").objectId==9001&&copied.objects.at("Acs2").kind==2,"opaque IDs and kinds independent of keys/slots");
 check(copied.sections[0].entries[0].effectPath.empty()&&copied.sections[0].entries[0].subsetShows.at(0),"show-only subset preserves absent path and explicit true");
 check(copied.sections[0].entries[1].effectPath=="none"&&copied.sections[0].entries[1].hasShow&&!copied.sections[0].entries[1].shown,"explicit none and false stay present");
 check(copied.sections[1].name=="Effect@Depth(2)"&&copied.sections[1].baseName=="Depth"&&copied.sections[1].entries[0].effectPath=="main_default","target label and scoped literal preserved");
 baseRows[0].flags=MDA9_EMM_SHOW_PRESENT;
 check(copied.sections[0].entries[0].subsetShows.at(0),"caller mutation cannot change copied metadata");
 rejects([&]{auto value=configuration;value.sectionCount=65537;emmcfg::Copy(&value);},"oversized section table rejected");
 rejects([&]{auto value=configuration;value.sections=nullptr;emmcfg::Copy(&value);},"null section span rejected");
 rejects([&]{bindings[0].key=text("Pmd01");emmcfg::Copy(&configuration);},"noncanonical key rejected");bindings[0].key=text("Pmd17");
 rejects([&]{bindings[1].objectId=9001;emmcfg::Copy(&configuration);},"duplicate native identity rejected");bindings[1].objectId=5002;
 rejects([&]{bindings[1].kind=1;emmcfg::Copy(&configuration);},"record kind mismatch rejected");bindings[1].kind=2;
 rejects([&]{baseRows[0].flags=MDA9_EMM_SHOW_VALUE;emmcfg::Copy(&configuration);},"show value without presence rejected");baseRows[0].flags=MDA9_EMM_SHOW_PRESENT;
 rejects([&]{sections[1].parentTarget=text("Missing");emmcfg::Copy(&configuration);},"unknown parent rejected");
 rejects([&]{sections[1].parentTarget=text("Depth(2)");emmcfg::Copy(&configuration);},"parent cycle rejected");sections[1].parentTarget={};
 rejects([&]{auto value=configuration;value.rootSeedCount=1;emmcfg::Copy(&value);},"mapped object missing frozen seed rejected");
 const char invalid[]={char(0xc0),char(0x80)};
 rejects([&]{sections[1].baseName={invalid,2};emmcfg::Copy(&configuration);},"invalid strict UTF8 rejected");sections[1].baseName=text("Depth");
 sections[0].ownerKey=text("Acs2");sections[0].ownerSubset=0;sections[0].parentTarget=text("Unused(3)");
 auto baseOwner=emmcfg::Copy(&configuration);
 check(baseOwner.sections[0].ownerKey=="Acs2"&&baseOwner.sections[0].ownerSubsetIndex==0&&baseOwner.sections[0].ownerMaterialName=="Unused(3)","accepted base Owner metadata preserved without runtime reinterpretation");
 sections[0].ownerKey={};sections[0].ownerSubset=-1;sections[0].parentTarget={};
 auto visibility=configuration;visibility.resources=nullptr;visibility.resourceCount=0;seeds[1].effectPath={};
 check(emmcfg::Copy(&visibility).resources.empty(),"visibility-only EMM accepts empty resource map");seeds[1].effectPath=text("C:\\local\\seed.fx");
 // Full admitted section table: one base plus 65535 named owners. Iterative
 // memoized validation must handle a long chain and reject its final backedge.
 std::vector<std::string> labels;labels.reserve(65535);
 for(size_t i=0;i<65535;++i)labels.push_back("T"+std::to_string(i));
 std::vector<std::string> names;names.reserve(labels.size());for(const auto& label:labels)names.push_back("Effect@"+label);
 std::vector<Mda9EmmSectionV1> large;large.reserve(65536);large.push_back(sections[0]);large[0].entries=nullptr;large[0].entryCount=0;
 for(size_t i=0;i<labels.size();++i)large.push_back({sizeof(Mda9EmmSectionV1),1,text(names[i].c_str()),text(labels[i].c_str()),text("Acs2"),i+1<labels.size()?text(labels[i+1].c_str()):Mda9Utf8SpanV1{}, {},-1,0,nullptr,0,{}});
 auto many=configuration;many.sections=large.data();many.sectionCount=large.size();
 check(emmcfg::Copy(&many).sections.size()==65536,"maximum section chain validates without recursion or repeated ancestor walk");
 large.back().parentTarget=text(labels.front().c_str());
 rejects([&]{emmcfg::Copy(&many);},"maximum section cycle rejects before any runtime mutation");
 packet::Frame frame{};frame.objects.push_back({9001,0,1,1,"C:\\model.pmx","",{},{}});frame.objects.push_back({5002,1,1,1,"C:\\a.x","",{},{}});frame.objects.back().kind=packet::Accessory;frame.objects.push_back({7003,2,0,0,"C:\\hidden.pmx","",{},{}});
 emmcfg::ValidateFrame(copied,frame);check(true,"all live roots and actual visible subset validate");
 rejects([&]{auto value=frame;value.objects.pop_back();emmcfg::ValidateFrame(copied,value);},"stale extra root seed rejected");
 rejects([&]{auto value=frame;value.objects[0].visible=0;value.objects[0].materialCount=0;emmcfg::ValidateFrame(copied,value);},"hidden missing model subset metadata explicitly rejected");
 rejects([&]{auto value=frame;value.objects[1].kind=packet::Model;emmcfg::ValidateFrame(copied,value);},"frozen-frame kind mismatch rejected");
 std::cout<<"checks="<<checks<<" failures="<<failures<<'\n';return failures?1:0;
}
