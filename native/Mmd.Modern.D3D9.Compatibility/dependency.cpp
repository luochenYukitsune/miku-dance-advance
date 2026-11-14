// Project-owned CPU-only D3DX metadata inspection. No MME/device/runtime state.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define MDA_D3D9_SESSION_BUILD
#include "mda_d3d9_dependency.h"
#include "dependency_compiler_api.hpp"
#include "mmeffect/mda_effect_dependency.hpp"
#include <windows.h>
#include <algorithm>
#include <cstring>
#include <limits>
#include <map>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <tuple>
#include <vector>

static_assert(sizeof(void*)==8,"Windows x64 is required");
static_assert(sizeof(Mda9DependencyBytesV1)==16,"dependency span ABI");
static_assert(sizeof(Mda9DependencyInspectV1)==72&&sizeof(Mda9DependencyIncludeRequestV1)==96&&sizeof(Mda9DependencyIncludeResponseV1)==48,"dependency input ABI");
static_assert(sizeof(Mda9DependencyInfoV1)==112&&sizeof(Mda9DependencyProfileV1)==64,"dependency report ABI");
static_assert(sizeof(Mda9DependencyParameterV1)==96&&sizeof(Mda9DependencyAnnotationV1)==64,"dependency metadata ABI");
static_assert(sizeof(Mda9DependencyIncludeV1)==96&&sizeof(Mda9DependencyReferenceV1)==120&&sizeof(Mda9DependencyDiagnosticV1)==72,"dependency event ABI");
namespace {
constexpr uint64_t absent=UINT64_MAX, maxSource=256ull*1024*1024;
constexpr uint64_t maxSourceTotal=512ull*1024*1024, maxMetadata=32ull*1024*1024;
constexpr size_t maxName=32767, maxEvents=65536, maxParameters=65536;
constexpr size_t maxAnnotations=262144, maxFunctions=4096, maxDepth=128;
struct Failure:std::runtime_error {
 Mda9Status status;
 Failure(Mda9Status s,const char* text):std::runtime_error(text),status(s){}
};
[[noreturn]] void fail(Mda9Status s,const char* text){throw Failure(s,text);}
template<class T> bool valid(const T* p){return p&&p->size>=sizeof(T)&&p->version==1;}
template<class T> struct Com {
 T* value=nullptr;~Com(){reset();}
 void reset(){if(value)value->Release();value=nullptr;}
 T** out(){reset();return &value;}
};
struct TextBudget {
 uint64_t total=0;
 std::string copy(const char* data,size_t n){
  if(n>maxMetadata-total)fail(MDA9_UNSUPPORTED,"dependency metadata byte limit exceeded");
  std::string result(data?data:"",n);total+=n;return result;
 }
 std::string name(const char* value){
  if(!value)return {};
  size_t n=0;while(n<=maxName&&value[n])++n;
  if(n>maxName)fail(MDA9_UNSUPPORTED,"dependency metadata string limit exceeded");
  return copy(value,n);
 }
};
Mda9DependencyBytesV1 span(const std::string& value){return {reinterpret_cast<const uint8_t*>(value.data()),value.size()};}
std::string inputString(Mda9DependencyBytesV1 value,bool nonempty){
 if((value.length&&!value.data)||value.length>maxName||(!value.length&&nonempty))
  fail(MDA9_INVALID_ARGUMENT,"invalid dependency input string span");
 if(value.length&&std::memchr(value.data,0,static_cast<size_t>(value.length)))
  fail(MDA9_INVALID_ARGUMENT,"dependency input string contains embedded NUL");
 return value.length?std::string(reinterpret_cast<const char*>(value.data),static_cast<size_t>(value.length)):std::string();
}
std::wstring exactWide(const std::string& value){
 const UINT page=GetACP();
 const int n=MultiByteToWideChar(page,MB_ERR_INVALID_CHARS,value.data(),static_cast<int>(value.size()),nullptr,0);
 if(!n)fail(MDA9_UNSUPPORTED,"dependency origin cannot be decoded in the active ACP");
 std::wstring wide(n,L'\0');
 if(MultiByteToWideChar(page,MB_ERR_INVALID_CHARS,value.data(),static_cast<int>(value.size()),wide.data(),n)!=n)
  fail(MDA9_UNSUPPORTED,"dependency ACP origin conversion failed");
 BOOL used=FALSE;const DWORD flags=page==CP_UTF8?WC_ERR_INVALID_CHARS:WC_NO_BEST_FIT_CHARS;
 const int count=WideCharToMultiByte(page,flags,wide.data(),n,nullptr,0,nullptr,page==CP_UTF8?nullptr:&used);
 if(count!=static_cast<int>(value.size())||used)fail(MDA9_UNSUPPORTED,"dependency origin is not lossless ACP");
 std::string roundtrip(count,'\0');used=FALSE;
 if(WideCharToMultiByte(page,flags,wide.data(),n,roundtrip.data(),count,nullptr,page==CP_UTF8?nullptr:&used)!=count||used||roundtrip!=value)
  fail(MDA9_UNSUPPORTED,"dependency ACP origin changed on roundtrip");
 return wide;
}
std::string wideCompilerRoot(const std::wstring& value){
 // The pinned D3DX9_43 W entry converts only its root filename to UTF-8
 // (0x180125c28); child #include tokens retain their original ACP bytes.
 const int n=WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,value.data(),static_cast<int>(value.size()),nullptr,0,nullptr,nullptr);
 if(!n)fail(MDA9_UNSUPPORTED,"dependency compiler root cannot be encoded as UTF-8");
 std::string result(n,'\0');
 if(WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,value.data(),static_cast<int>(value.size()),result.data(),n,nullptr,nullptr)!=n)
  fail(MDA9_UNSUPPORTED,"dependency compiler root UTF-8 conversion failed");
 return result;
}
std::string modulePath(HMODULE module){
 if(!module)return {};
 wchar_t buffer[32768];const DWORD n=GetModuleFileNameW(module,buffer,32768);
 if(!n||n>=32768)fail(MDA9_UNSUPPORTED,"dependency compiler module identity unavailable");
 const int bytes=WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,buffer,n,nullptr,0,nullptr,nullptr);
 if(!bytes)fail(MDA9_UNSUPPORTED,"dependency compiler module path cannot be encoded");
 std::string path(bytes,'\0');
 if(WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,buffer,n,path.data(),bytes,nullptr,nullptr)!=bytes)
  fail(MDA9_UNSUPPORTED,"dependency compiler module path conversion failed");
 return path;
}
template<class T> T proc(HMODULE module,const char* name){
 auto value=GetProcAddress(module,name);if(!value)fail(MDA9_UNSUPPORTED,"required D3DX compiler export is unavailable");
 return reinterpret_cast<T>(value);
}
using CreateCompilerFile=HRESULT(WINAPI*)(LPCWSTR,const D3DXMACRO*,ID3DXInclude*,DWORD,MdaDependencyEffectCompiler**,ID3DXBuffer**);
using CompileShaderFile=HRESULT(WINAPI*)(LPCSTR,const D3DXMACRO*,ID3DXInclude*,LPCSTR,LPCSTR,DWORD,ID3DXBuffer**,ID3DXBuffer**,ID3DXConstantTable**);

struct Profile {uint32_t kind=0,flags=0;Mda9Status status=MDA9_OK;HRESULT hr=S_OK;uint64_t parameter=absent;std::string entry,target;};
struct Parameter {uint64_t profile=0,parent=absent;size_t annotationBegin=0,annotationEnd=0;uint32_t ordinal=0,arrayElement=UINT32_MAX;D3DXPARAMETER_DESC desc{};int semanticId=-1;std::string name,semantic;};
struct Annotation {uint64_t parameter=0;uint32_t ordinal=0,type=0;HRESULT hr=E_FAIL;std::string name,value;};
struct IncludeEvent {uint64_t profile=0,parent=0,resource=0;uint32_t kind=0,depth=0;bool root=false,closed=false;Mda9Status status=MDA9_OK;std::string request,error;};
struct Reference {uint64_t profile=0,parameter=0,offset=0;uint32_t kind=0,row=UINT32_MAX;std::string annotation,raw,key,value,target;};
struct Diagnostic {uint64_t profile=absent,parameter=absent;uint32_t kind=0,page=CP_UTF8;Mda9Status status=MDA9_OK;HRESULT hr=S_OK;std::string annotation,text;};
struct Report {
 uint64_t root=0,sourceTotal=0;UINT page=GetACP();Mda9Status status=MDA9_OK;TextBudget text;
 std::string d3dxPath,compilerPath;
 std::vector<Profile> profiles;std::vector<Parameter> parameters;std::vector<Annotation> annotations;
 std::vector<IncludeEvent> includes;std::vector<Reference> references;std::vector<Diagnostic> diagnostics;
 void mark(Mda9Status value){if(status==MDA9_OK)status=value;}
 void diagnostic(uint64_t profile,uint64_t parameter,uint32_t kind,Mda9Status statusValue,
                 const char* message,HRESULT hr=S_OK,const char* annotation=nullptr)noexcept{
  if(statusValue!=MDA9_OK){mark(statusValue);if(profile<profiles.size()&&profiles[profile].status==MDA9_OK)profiles[profile].status=statusValue;}
  try{
   if(diagnostics.size()>=maxEvents){mark(MDA9_UNSUPPORTED);return;}
   Diagnostic d;d.profile=profile;d.parameter=parameter;d.kind=kind;d.status=statusValue;d.hr=hr;
   d.text=text.name(message);d.annotation=text.name(annotation);diagnostics.push_back(std::move(d));
  }catch(...){mark(statusValue==MDA9_OK?MDA9_OUT_OF_MEMORY:statusValue);}
 }
 void compilerDiagnostic(uint64_t profile,ID3DXBuffer* buffer,HRESULT hr){
  if(!buffer||!buffer->GetBufferSize())return;
  if(diagnostics.size()>=maxEvents)fail(MDA9_UNSUPPORTED,"dependency diagnostic count limit exceeded");
  Diagnostic d;d.profile=profile;d.kind=MDA9_DEPENDENCY_DIAGNOSTIC_COMPILER;d.page=page;d.hr=hr;
  d.status=FAILED(hr)?MDA9_UNSUPPORTED:MDA9_OK;
  d.text=text.copy(static_cast<const char*>(buffer->GetBufferPointer()),buffer->GetBufferSize());
  diagnostics.push_back(std::move(d));
 }
};

// ID3DXInclude has exactly Open/Close, no IUnknown or virtual destructor.
class FrozenInclude final:public ID3DXInclude {
 struct Buffer {std::unique_ptr<char[]> data;uint64_t resource=0,size=0;uint32_t depth=0;size_t event=0;};
 Report& report_;const Mda9DependencyInspectV1& input_;const std::string& rootRequest_;
 const std::vector<uint8_t>& rootBytes_;uint64_t profile_;std::map<const void*,Buffer> live_;
 bool rootOpened_=false;char emptyRoot_=0;
 HRESULT rememberFailure(size_t index,Mda9Status status,const char* message,HRESULT hr) noexcept {
  report_.mark(status);
  if(profile_<report_.profiles.size()&&report_.profiles[profile_].status==MDA9_OK)report_.profiles[profile_].status=status;
  if(index<report_.includes.size()){
   auto& event=report_.includes[index];event.status=status;
   try{event.error=report_.text.name(message);}catch(...){}
  }
  return hr;
 }
public:
 FrozenInclude(Report& report,const Mda9DependencyInspectV1& input,const std::string& rootRequest,
               const std::vector<uint8_t>& bytes,uint64_t profile)
  :report_(report),input_(input),rootRequest_(rootRequest),rootBytes_(bytes),profile_(profile){}
 HRESULT __stdcall Open(D3DXINCLUDE_TYPE kind,LPCSTR request,LPCVOID parent,LPCVOID* data,UINT* length) override {
  if(data)*data=nullptr;if(length)*length=0;size_t eventIndex=SIZE_MAX;
  try{
   if(!data||!length||!request||(kind!=D3DXINC_LOCAL&&kind!=D3DXINC_SYSTEM))
    return rememberFailure(eventIndex,MDA9_INVALID_ARGUMENT,"invalid compiler include arguments",E_INVALIDARG);
   if(report_.includes.size()>=maxEvents)
    return rememberFailure(eventIndex,MDA9_UNSUPPORTED,"dependency include event limit exceeded",E_FAIL);
   IncludeEvent event;event.profile=profile_;event.kind=static_cast<uint32_t>(kind);event.parent=report_.root;
   event.request=report_.text.name(request);
   if(event.request.empty())return rememberFailure(eventIndex,MDA9_INVALID_ARGUMENT,"empty compiler include request",E_INVALIDARG);
   event.depth=1;
   if(parent){
    const auto found=live_.find(parent);
    if(found==live_.end()){
     report_.includes.push_back(std::move(event));eventIndex=report_.includes.size()-1;
     return rememberFailure(eventIndex,MDA9_INVALID_ARGUMENT,"unknown compiler include parent pointer",E_INVALIDARG);
    }
    event.parent=found->second.resource;event.depth=found->second.depth+1;
   }
   // FromFile's first Open is the exact entry-specific root name with NULL parent. A
   // mismatch is rejected, never delegated to the filesystem or source map.
   event.root=!rootOpened_;
   if(event.root)event.depth=0;
   report_.includes.push_back(std::move(event));eventIndex=report_.includes.size()-1;
   auto& saved=report_.includes[eventIndex];
   if(saved.depth>maxDepth||live_.size()>=maxEvents)
    return rememberFailure(eventIndex,MDA9_UNSUPPORTED,"dependency include depth/open-count limit exceeded",E_FAIL);
   const uint8_t* source=nullptr;uint64_t sourceLength=0,resource=0;
   if(saved.root){
    if(parent||kind!=D3DXINC_LOCAL||saved.request!=rootRequest_)
     return rememberFailure(eventIndex,MDA9_UNSUPPORTED,"D3DX did not request the exact frozen root origin",E_FAIL);
    rootOpened_=true;resource=report_.root;source=rootBytes_.data();sourceLength=rootBytes_.size();
   }else{
    Mda9DependencyIncludeRequestV1 query{};query.size=sizeof(query);query.version=1;
    query.rootResourceId=report_.root;query.parentResourceId=saved.parent;query.profileIndex=profile_;
    query.includeKind=saved.kind;query.depth=saved.depth;query.rawRequestAcp=span(saved.request);
    query.profileKind=report_.profiles[profile_].kind;query.entryAcp=span(report_.profiles[profile_].entry);query.targetAcp=span(report_.profiles[profile_].target);
    Mda9DependencyIncludeResponseV1 response{};response.size=sizeof(response);response.version=1;
    const Mda9Status status=input_.resolve(input_.context,&query,&response);
    if(!valid(&response))return rememberFailure(eventIndex,MDA9_ABI_MISMATCH,"include callback returned an invalid response ABI",E_FAIL);
    if(status!=MDA9_OK){
     std::string message=inputString(response.errorUtf8,false);
     if(message.empty())message="frozen include resolver rejected the request";
     return rememberFailure(eventIndex,status,message.c_str(),E_FAIL);
    }
    source=response.sourceBytes.data;sourceLength=response.sourceBytes.length;resource=response.resourceId;
    if(!resource||(!source&&sourceLength))return rememberFailure(eventIndex,MDA9_INVALID_ARGUMENT,"include callback returned invalid frozen bytes/identity",E_FAIL);
   }
   const uint64_t copied=saved.root?0:sourceLength;
   if(sourceLength>maxSource||copied>maxSourceTotal-report_.sourceTotal)
    return rememberFailure(eventIndex,MDA9_UNSUPPORTED,"dependency copied-source byte limit exceeded",E_FAIL);
   Buffer buffer;const void* pointer=sourceLength?static_cast<const void*>(source):static_cast<const void*>(&emptyRoot_);
   if(!saved.root){
    buffer.data.reset(new char[static_cast<size_t>(sourceLength)+1]);
    if(sourceLength)std::memcpy(buffer.data.get(),source,static_cast<size_t>(sourceLength));
    buffer.data[sourceLength]=0;pointer=buffer.data.get();
   }
   buffer.resource=resource;buffer.size=sourceLength;
   buffer.depth=saved.depth;buffer.event=eventIndex;
   live_.emplace(pointer,std::move(buffer));report_.sourceTotal+=copied;
   saved.resource=resource;*data=pointer;*length=static_cast<UINT>(sourceLength);return S_OK;
  }catch(const Failure& e){return rememberFailure(eventIndex,e.status,e.what(),E_FAIL);}
   catch(const std::bad_alloc&){return rememberFailure(eventIndex,MDA9_OUT_OF_MEMORY,"dependency include allocation failed",E_OUTOFMEMORY);}
   catch(...){return rememberFailure(eventIndex,MDA9_INTERNAL_ERROR,"dependency include callback failed",E_FAIL);}
 }
 HRESULT __stdcall Close(LPCVOID data) override {
  try{
   auto found=live_.find(data);if(found==live_.end())return rememberFailure(SIZE_MAX,MDA9_INTERNAL_ERROR,"unknown or duplicate compiler include Close",E_INVALIDARG);
   report_.includes[found->second.event].closed=true;live_.erase(found);return S_OK;
  }catch(...){return rememberFailure(SIZE_MAX,MDA9_INTERNAL_ERROR,"dependency include Close failed",E_FAIL);}
 }
 bool finished()const{return rootOpened_&&live_.empty();}
};

struct Function {uint64_t parameter;std::string entry,target;};
struct Reflector {
 Report& report;MdaDependencyEffectCompiler* compiler;uint64_t profile;
 std::vector<Function>& functions;std::map<D3DXHANDLE,bool> ancestry;
 mme::MdaEffectDependencyAnnotation annotation(uint64_t parameter,const char* name){
  mme::MdaEffectDependencyAnnotation result;
  const auto& p=report.parameters[parameter];
  for(size_t i=p.annotationBegin;i<p.annotationEnd;++i){
   const auto& a=report.annotations[i];
   if(!mme::MdaEffectDependencyAsciiEqual(a.name,name))continue;
   result.present=true;result.valid=SUCCEEDED(a.hr);result.value=a.value;return result;
  }
  return result;
 }
 void reference(uint64_t parameter,uint32_t kind,const char* annotationName,const std::string& raw,
                const std::string& key={},const std::string& value={},const std::string& target={},
                uint32_t row=UINT32_MAX,uint64_t offset=0){
  if(report.references.size()>=maxEvents)fail(MDA9_UNSUPPORTED,"dependency reference count limit exceeded");
  Reference r;r.profile=profile;r.parameter=parameter;r.kind=kind;r.row=row;r.offset=offset;
  r.annotation=report.text.name(annotationName);r.raw=report.text.copy(raw.data(),raw.size());
  r.key=report.text.copy(key.data(),key.size());r.value=report.text.copy(value.data(),value.size());
  r.target=report.text.copy(target.data(),target.size());report.references.push_back(std::move(r));
 }
 void classify(uint64_t index){
  auto& p=report.parameters[index];
  mme::MdaEffectDependencyParameterDesc descriptor;
  descriptor.name=p.name;descriptor.semantic=p.semantic;descriptor.parameterClass=p.desc.Class;
  descriptor.parameterType=p.desc.Type;descriptor.rows=p.desc.Rows;descriptor.columns=p.desc.Columns;
  descriptor.elements=p.desc.Elements;descriptor.annotationCount=p.desc.Annotations;descriptor.flags=p.desc.Flags;
  auto resolved=mme::MdaResolveEffectDependencySemantic(descriptor);p.semanticId=resolved.semanticId;
  if(resolved.invalid){report.diagnostic(profile,index,MDA9_DEPENDENCY_DIAGNOSTIC_METADATA,MDA9_UNSUPPORTED,"host resource semantic descriptor is invalid");return;}
  mme::MdaEffectDependencyInput input;input.semanticId=p.semanticId;input.parameterType=p.desc.Type;
  input.parameterFlags=p.desc.Flags;input.annotationCount=p.desc.Annotations;
  input.resourceName=annotation(index,"ResourceName");input.resourceType=annotation(index,"ResourceType");
  input.function=annotation(index,"Function");input.target=annotation(index,"Target");input.defaultEffect=annotation(index,"DefaultEffect");
  const auto classified=mme::MdaClassifyEffectDependencies(input);
  for(const auto& d:classified.diagnostics)
   report.diagnostic(profile,index,MDA9_DEPENDENCY_DIAGNOSTIC_METADATA,MDA9_UNSUPPORTED,d.code.c_str(),S_OK,d.annotation.c_str());
  if(classified.resourceNameRequired){
   uint32_t kind=MDA9_DEPENDENCY_REFERENCE_RESOURCE;
   if(classified.animatedType==mme::MdaEffectDependencyAnimatedType::Png)kind=MDA9_DEPENDENCY_REFERENCE_ANIMATED_PNG;
   if(classified.animatedType==mme::MdaEffectDependencyAnimatedType::Gif)kind=MDA9_DEPENDENCY_REFERENCE_ANIMATED_GIF;
   reference(index,kind,"ResourceName",input.resourceName.value,{},input.resourceName.value);
  }
  for(const auto& row:classified.defaultEffectRows){
   uint32_t kind=MDA9_DEPENDENCY_REFERENCE_DEFAULT_PATH;
   if(row.kind==mme::MdaDefaultEffectKind::None)kind=MDA9_DEPENDENCY_REFERENCE_DEFAULT_NONE;
   if(row.kind==mme::MdaDefaultEffectKind::Hide)kind=MDA9_DEPENDENCY_REFERENCE_DEFAULT_HIDE;
   if(row.kind==mme::MdaDefaultEffectKind::MainDefault)kind=MDA9_DEPENDENCY_REFERENCE_DEFAULT_MAIN;
   reference(index,kind,"DefaultEffect",row.rawToken,row.key,row.value,{},row.ordinal,row.offset);
  }
  if(classified.textureFunctionRequired){
   reference(index,MDA9_DEPENDENCY_REFERENCE_FUNCTION,"Function",classified.functionName,{},classified.functionName,classified.functionTarget);
   const auto duplicate=std::find_if(functions.begin(),functions.end(),[&](const Function& f){return f.entry==classified.functionName&&f.target==classified.functionTarget;});
   if(duplicate==functions.end()){
    if(functions.size()>=maxFunctions)fail(MDA9_UNSUPPORTED,"dependency texture Function count limit exceeded");
    functions.push_back({index,classified.functionName,classified.functionTarget});
   }
  }
 }
 void parameter(D3DXHANDLE handle,uint64_t parent,uint32_t ordinal,uint32_t element,uint32_t depth){
  if(!handle)fail(MDA9_UNSUPPORTED,"compiler returned a null parameter handle");
  if(depth>maxDepth||report.parameters.size()>=maxParameters)fail(MDA9_UNSUPPORTED,"dependency parameter depth/count limit exceeded");
  if(ancestry.find(handle)!=ancestry.end())fail(MDA9_UNSUPPORTED,"compiler parameter traversal cycle");
  D3DXPARAMETER_DESC d{};const HRESULT hr=compiler->GetParameterDesc(handle,&d);
  if(FAILED(hr)){report.diagnostic(profile,absent,MDA9_DEPENDENCY_DIAGNOSTIC_METADATA,MDA9_UNSUPPORTED,"compiler GetParameterDesc failed",hr);return;}
  Parameter p;p.profile=profile;p.parent=parent;p.ordinal=ordinal;p.arrayElement=element;p.desc=d;
  p.name=report.text.name(d.Name);p.semantic=report.text.name(d.Semantic);p.desc.Name=nullptr;p.desc.Semantic=nullptr;
  p.annotationBegin=report.annotations.size();
  const uint64_t index=report.parameters.size();report.parameters.push_back(std::move(p));
  if(d.Annotations>maxAnnotations-report.annotations.size())fail(MDA9_UNSUPPORTED,"dependency annotation count limit exceeded");
  for(UINT i=0;i<d.Annotations;++i){
   D3DXHANDLE a=compiler->GetAnnotation(handle,i);D3DXPARAMETER_DESC ad{};
   if(!a||FAILED(compiler->GetParameterDesc(a,&ad))){report.diagnostic(profile,index,MDA9_DEPENDENCY_DIAGNOSTIC_METADATA,MDA9_UNSUPPORTED,"compiler annotation descriptor failed");continue;}
   Annotation item;item.parameter=index;item.ordinal=i;item.type=ad.Type;item.name=report.text.name(ad.Name);
   const char* value=nullptr;item.hr=compiler->GetString(a,&value);
   if(SUCCEEDED(item.hr)&&value)item.value=report.text.name(value);
   else if(SUCCEEDED(item.hr))item.hr=E_FAIL;
   report.annotations.push_back(std::move(item));
  }
  report.parameters[index].annotationEnd=report.annotations.size();
  classify(index);ancestry.emplace(handle,true);
  try{
   if(element==UINT32_MAX&&d.Elements){
    if(d.Elements>maxParameters-report.parameters.size())fail(MDA9_UNSUPPORTED,"dependency array element count limit exceeded");
    for(UINT i=0;i<d.Elements;++i)parameter(compiler->GetParameterElement(handle,i),index,i,i,depth+1);
   }else if(d.StructMembers){
    if(d.StructMembers>maxParameters-report.parameters.size())fail(MDA9_UNSUPPORTED,"dependency struct member count limit exceeded");
    for(UINT i=0;i<d.StructMembers;++i)parameter(compiler->GetParameter(handle,i),index,i,UINT32_MAX,depth+1);
   }
  }catch(...){ancestry.erase(handle);throw;}
  ancestry.erase(handle);
 }
 void run(){
  D3DXEFFECT_DESC desc{};HRESULT hr=compiler->GetDesc(&desc);
  if(FAILED(hr)){report.diagnostic(profile,absent,MDA9_DEPENDENCY_DIAGNOSTIC_METADATA,MDA9_UNSUPPORTED,"compiler GetDesc failed",hr);return;}
  if(desc.Parameters>maxParameters-report.parameters.size())fail(MDA9_UNSUPPORTED,"dependency top-level parameter count limit exceeded");
  for(UINT i=0;i<desc.Parameters;++i)parameter(compiler->GetParameter(nullptr,i),absent,i,UINT32_MAX,0);
 }
};

std::mutex reportsMutex;
std::map<uint64_t,std::unique_ptr<Report>> reports;
uint64_t nextReport=1; // protected by reportsMutex; zero permanently means exhausted
void setError(Mda9ErrorV1* error,Mda9Status status,const char* text)noexcept{
 if(!valid(error))return;
 error->status=status;error->reserved=0;const size_t count=std::strlen(text);error->requiredBytes=count+1;
 if(error->utf8&&error->capacity){const size_t n=static_cast<size_t>(std::min<uint64_t>(error->capacity-1,count));std::memcpy(error->utf8,text,n);error->utf8[n]=0;}
}
template<class T,class F> Mda9Status get(uint64_t handle,T* output,F fill)noexcept{
 if(!output)return MDA9_INVALID_ARGUMENT;if(!valid(output))return MDA9_ABI_MISMATCH;
 try{std::lock_guard<std::mutex> lock(reportsMutex);auto found=reports.find(handle);if(found==reports.end())return MDA9_INVALID_HANDLE;
  T value{};value.size=sizeof(T);value.version=1;const Mda9Status status=fill(*found->second,value);
  if(status==MDA9_OK)*output=value;return status;
 }catch(...){return MDA9_INTERNAL_ERROR;}
}
}

extern "C" Mda9Status MDA9_CALL MdaD3D9InspectDependenciesV1(const Mda9DependencyInspectV1* input,Mda9DependencyInspection* output,Mda9ErrorV1* error){
 if(output)*output=0;
 try{
  if(!input||!output)fail(MDA9_INVALID_ARGUMENT,"dependency inspection input/output is null");
  if(!valid(input))fail(MDA9_ABI_MISMATCH,"dependency inspection ABI mismatch");
  if(!input->rootResourceId||!input->resolve||input->reserved[0]||input->reserved[1])
   fail(MDA9_INVALID_ARGUMENT,"invalid dependency inspection root/callback/reserved fields");
  if((input->rootSourceBytes.length&&!input->rootSourceBytes.data)||input->rootSourceBytes.length>maxSource)
   fail(MDA9_INVALID_ARGUMENT,"invalid dependency root source byte span");
  const std::string origin=inputString(input->rootOriginAcp,true);const std::wstring wide=exactWide(origin);
  if(origin.size()>=MAX_PATH||wide.size()>=MAX_PATH||wide.size()<3||!((wide[0]>=L'A'&&wide[0]<=L'Z')||(wide[0]>=L'a'&&wide[0]<=L'z'))||wide[1]!=L':'||(wide[2]!=L'\\'&&wide[2]!=L'/'))
   fail(MDA9_UNSUPPORTED,"dependency root must be an absolute drive origin shorter than MAX_PATH");
  const std::string effectRoot=wideCompilerRoot(wide);
  HMODULE module=GetModuleHandleW(L"D3DX9_43.dll");
  if(!module)fail(MDA9_UNSUPPORTED,"validated D3DX9_43 module must already be loaded");
  const auto create=proc<CreateCompilerFile>(module,"D3DXCreateEffectCompilerFromFileW");
  const auto compile=proc<CompileShaderFile>(module,"D3DXCompileShaderFromFileA");
  std::vector<uint8_t> bytes(static_cast<size_t>(input->rootSourceBytes.length));
  if(!bytes.empty())std::memcpy(bytes.data(),input->rootSourceBytes.data,bytes.size());
  auto report=std::make_unique<Report>();report->root=input->rootResourceId;report->sourceTotal=bytes.size();report->d3dxPath=modulePath(module);
  std::vector<Function> functions;
  for(uint32_t kind=1;kind<=2;++kind){
   Profile profile;profile.kind=kind;profile.flags=0x1000;const uint64_t index=report->profiles.size();report->profiles.push_back(profile);
   try{
    FrozenInclude include(*report,*input,effectRoot,bytes,index);Com<MdaDependencyEffectCompiler> compiler;Com<ID3DXBuffer> errors;
    D3DXMACRO macros[3]={{"_INDEX","PSIZE15"},{nullptr,nullptr},{nullptr,nullptr}};
    if(kind==MDA9_DEPENDENCY_PROFILE_MIP_ON)macros[1]={"MME_MIPMAP",""};
    HRESULT hr=create(wide.c_str(),macros,&include,0x1000,compiler.out(),errors.out());report->profiles[index].hr=hr;
    report->compilerDiagnostic(index,errors.value,hr);
    if(FAILED(hr)||!compiler.value){report->profiles[index].status=MDA9_UNSUPPORTED;report->mark(MDA9_UNSUPPORTED);
     report->diagnostic(index,absent,MDA9_DEPENDENCY_DIAGNOSTIC_COMPILER,MDA9_UNSUPPORTED,"D3DX effect compiler profile failed",hr);
    }else{Reflector reflect{*report,compiler.value,index,functions,{}};reflect.run();}
    compiler.reset();errors.reset();
    if(!include.finished())report->diagnostic(index,absent,MDA9_DEPENDENCY_DIAGNOSTIC_INTERNAL,MDA9_INTERNAL_ERROR,"compiler root Open/Close contract was not completed");
   }catch(const Failure& e){report->profiles[index].status=e.status;report->diagnostic(index,absent,MDA9_DEPENDENCY_DIAGNOSTIC_LIMIT,e.status,e.what());}
  }
  for(const auto& function:functions){
   Profile profile;profile.kind=MDA9_DEPENDENCY_PROFILE_TEXTURE_FUNCTION;profile.parameter=function.parameter;
   profile.entry=report->text.copy(function.entry.data(),function.entry.size());profile.target=report->text.copy(function.target.data(),function.target.size());
   const uint64_t index=report->profiles.size();report->profiles.push_back(std::move(profile));
   try{
    FrozenInclude include(*report,*input,origin,bytes,index);Com<ID3DXBuffer> code,errors;
    const HRESULT hr=compile(origin.c_str(),nullptr,&include,function.entry.c_str(),function.target.c_str(),0,code.out(),errors.out(),nullptr);
    report->profiles[index].hr=hr;report->compilerDiagnostic(index,errors.value,hr);
    if(FAILED(hr)||!code.value||!code.value->GetBufferSize()){
     report->profiles[index].status=MDA9_UNSUPPORTED;report->mark(MDA9_UNSUPPORTED);
     report->diagnostic(index,function.parameter,MDA9_DEPENDENCY_DIAGNOSTIC_COMPILER,MDA9_UNSUPPORTED,"D3DX texture Function profile failed",hr,"Function");
    }
    code.reset();errors.reset();
    if(!include.finished())report->diagnostic(index,function.parameter,MDA9_DEPENDENCY_DIAGNOSTIC_INTERNAL,MDA9_INTERNAL_ERROR,"Function root Open/Close contract was not completed");
   }catch(const Failure& e){report->profiles[index].status=e.status;report->diagnostic(index,function.parameter,MDA9_DEPENDENCY_DIAGNOSTIC_LIMIT,e.status,e.what());}
  }
  report->compilerPath=modulePath(GetModuleHandleW(L"D3DCompiler_43.dll"));
  if(report->compilerPath.empty())report->diagnostic(absent,absent,MDA9_DEPENDENCY_DIAGNOSTIC_INTERNAL,MDA9_UNSUPPORTED,"validated D3DCompiler_43 module is not loaded");
  uint64_t handle=0;
  {std::lock_guard<std::mutex> lock(reportsMutex);
   if(!nextReport)fail(MDA9_INTERNAL_ERROR,"dependency report handle space exhausted");
   handle=nextReport++;reports.emplace(handle,std::move(report));}
  *output=handle;setError(error,MDA9_OK,"");return MDA9_OK;
 }catch(const Failure& e){setError(error,e.status,e.what());return e.status;}
  catch(const std::bad_alloc&){setError(error,MDA9_OUT_OF_MEMORY,"dependency inspection allocation failed");return MDA9_OUT_OF_MEMORY;}
  catch(...){setError(error,MDA9_INTERNAL_ERROR,"dependency inspection failed");return MDA9_INTERNAL_ERROR;}
}

extern "C" Mda9Status MDA9_CALL MdaD3D9DependencyInfoV1(uint64_t h,Mda9DependencyInfoV1* out){return get(h,out,[](const Report& r,Mda9DependencyInfoV1& v){
 v.inspectionStatus=r.status;v.codePage=r.page;v.pointerBits=64;v.rootResourceId=r.root;
 v.profileCount=r.profiles.size();v.parameterCount=r.parameters.size();v.annotationCount=r.annotations.size();v.includeCount=r.includes.size();v.referenceCount=r.references.size();v.diagnosticCount=r.diagnostics.size();
 v.d3dxModulePathUtf8=span(r.d3dxPath);v.compilerModulePathUtf8=span(r.compilerPath);return MDA9_OK;});}
extern "C" Mda9Status MDA9_CALL MdaD3D9DependencyProfileV1(uint64_t h,uint64_t i,Mda9DependencyProfileV1* out){return get(h,out,[i](const Report& r,Mda9DependencyProfileV1& v){
 if(i>=r.profiles.size())return MDA9_INVALID_ARGUMENT;const auto& p=r.profiles[i];v.kind=p.kind;v.compilerFlags=p.flags;v.status=p.status;v.hresult=p.hr;v.sourceParameterIndex=p.parameter;v.entryAcp=span(p.entry);v.targetAcp=span(p.target);return MDA9_OK;});}
extern "C" Mda9Status MDA9_CALL MdaD3D9DependencyParameterV1(uint64_t h,uint64_t i,Mda9DependencyParameterV1* out){return get(h,out,[i](const Report& r,Mda9DependencyParameterV1& v){
 if(i>=r.parameters.size())return MDA9_INVALID_ARGUMENT;const auto& p=r.parameters[i];v.profileIndex=p.profile;v.parentParameterIndex=p.parent;v.ordinal=p.ordinal;v.arrayElement=p.arrayElement;
 v.parameterClass=p.desc.Class;v.parameterType=p.desc.Type;v.rows=p.desc.Rows;v.columns=p.desc.Columns;v.elements=p.desc.Elements;v.structMembers=p.desc.StructMembers;v.flags=p.desc.Flags;v.semanticId=static_cast<uint32_t>(p.semanticId);v.nameAcp=span(p.name);v.semanticAcp=span(p.semantic);return MDA9_OK;});}
extern "C" Mda9Status MDA9_CALL MdaD3D9DependencyAnnotationV1(uint64_t h,uint64_t i,Mda9DependencyAnnotationV1* out){return get(h,out,[i](const Report& r,Mda9DependencyAnnotationV1& v){
 if(i>=r.annotations.size())return MDA9_INVALID_ARGUMENT;const auto& a=r.annotations[i];v.parameterIndex=a.parameter;v.ordinal=a.ordinal;v.parameterType=a.type;v.stringHresult=a.hr;v.nameAcp=span(a.name);v.stringAcp=span(a.value);return MDA9_OK;});}
extern "C" Mda9Status MDA9_CALL MdaD3D9DependencyIncludeV1(uint64_t h,uint64_t i,Mda9DependencyIncludeV1* out){return get(h,out,[i](const Report& r,Mda9DependencyIncludeV1& v){
 if(i>=r.includes.size())return MDA9_INVALID_ARGUMENT;const auto& e=r.includes[i];v.profileIndex=e.profile;v.rootResourceId=r.root;v.parentResourceId=e.parent;v.resourceId=e.resource;v.includeKind=e.kind;v.depth=e.depth;v.rootOpen=e.root;v.closed=e.closed;v.status=e.status;v.rawRequestAcp=span(e.request);v.errorUtf8=span(e.error);return MDA9_OK;});}
extern "C" Mda9Status MDA9_CALL MdaD3D9DependencyReferenceV1(uint64_t h,uint64_t i,Mda9DependencyReferenceV1* out){return get(h,out,[i](const Report& r,Mda9DependencyReferenceV1& v){
 if(i>=r.references.size())return MDA9_INVALID_ARGUMENT;const auto& e=r.references[i];v.profileIndex=e.profile;v.parameterIndex=e.parameter;v.kind=e.kind;v.rowOrdinal=e.row;v.rawOffset=e.offset;v.annotationAcp=span(e.annotation);v.rawAcp=span(e.raw);v.keyAcp=span(e.key);v.valueAcp=span(e.value);v.targetAcp=span(e.target);return MDA9_OK;});}
extern "C" Mda9Status MDA9_CALL MdaD3D9DependencyDiagnosticV1(uint64_t h,uint64_t i,Mda9DependencyDiagnosticV1* out){return get(h,out,[i](const Report& r,Mda9DependencyDiagnosticV1& v){
 if(i>=r.diagnostics.size())return MDA9_INVALID_ARGUMENT;const auto& d=r.diagnostics[i];v.profileIndex=d.profile;v.parameterIndex=d.parameter;v.kind=d.kind;v.messageCodePage=d.page;v.status=d.status;v.hresult=d.hr;v.annotationAcp=span(d.annotation);v.messageBytes=span(d.text);return MDA9_OK;});}
extern "C" Mda9Status MDA9_CALL MdaD3D9DestroyDependencyInspectionV1(uint64_t h){
 try{std::lock_guard<std::mutex> lock(reportsMutex);auto found=reports.find(h);if(found==reports.end())return MDA9_INVALID_HANDLE;reports.erase(found);return MDA9_OK;}catch(...){return MDA9_INTERNAL_ERROR;}
}
