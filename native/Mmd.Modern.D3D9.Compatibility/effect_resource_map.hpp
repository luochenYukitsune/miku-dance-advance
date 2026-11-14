// Project-owned package path identity. Pure lexical mapping; never opens origins.
#pragma once
#include <windows.h>
#include <map>
#include <string>
#include <vector>
#include <stdexcept>

namespace resources {
inline void Invalid(const char* message){throw std::invalid_argument(message);}
inline bool Drive(const std::wstring& p){return p.size()>=3&&((p[0]>=L'A'&&p[0]<=L'Z')||(p[0]>=L'a'&&p[0]<=L'z'))&&p[1]==L':'&&(p[2]==L'\\'||p[2]==L'/');}
struct OrdinalIgnoreCase {
 bool operator()(const std::wstring& a,const std::wstring& b)const {
  const int order=CompareStringOrdinal(a.data(),static_cast<int>(a.size()),b.data(),static_cast<int>(b.size()),TRUE);
  if(!order)throw std::runtime_error("Windows ordinal path comparison failed");
  return order==CSTR_LESS_THAN;
 }
};
inline std::wstring Normalize(const std::wstring& path){
 if(!Drive(path)||path.size()>32767)Invalid("package resource requires an absolute Windows drive file path");
 for(wchar_t c:path)if(c<32||c==L'"'||c==L'<'||c==L'>'||c==L'|'||c==L'?'||c==L'*')Invalid("invalid package resource path character");
 std::vector<std::wstring> parts;
 for(size_t begin=3;begin<=path.size();){
  size_t end=path.find_first_of(L"\\/",begin);if(end==std::wstring::npos)end=path.size();
  const auto part=path.substr(begin,end-begin);
  if(part==L".."){if(parts.empty())Invalid("package resource path escapes its drive root");parts.pop_back();}
  else if(!part.empty()&&part!=L"."){
   if(part.back()==L'.'||part.back()==L' '||part.find(L':')!=std::wstring::npos)Invalid("ambiguous package resource path segment");
   auto device=part.substr(0,part.find(L'.'));while(!device.empty()&&device.back()==L' ')device.pop_back();
   for(auto& c:device)if(c>=L'a'&&c<=L'z')c-=32;
   if(device==L"CON"||device==L"PRN"||device==L"AUX"||device==L"NUL"||device==L"CONIN$"||device==L"CONOUT$"||
      (device.size()==4&&(device.substr(0,3)==L"COM"||device.substr(0,3)==L"LPT")&&
       ((device[3]>=L'1'&&device[3]<=L'9')||device[3]==0xB9||device[3]==0xB2||device[3]==0xB3)))Invalid("reserved Windows device identity");
   parts.push_back(part);
  }
  if(end==path.size())break;begin=end+1;
 }
 if(parts.empty()||path.back()==L'\\'||path.back()==L'/')Invalid("package resource path must identify a file");
 std::wstring normalized;normalized+=path[0]>=L'a'&&path[0]<=L'z'?path[0]-32:path[0];normalized+=L":\\";
 for(size_t i=0;i<parts.size();++i){if(i)normalized+=L'\\';normalized+=parts[i];}
 return normalized;
}
struct Row {std::wstring origin,local;};
class Map {
 std::map<std::wstring,std::wstring,OrdinalIgnoreCase> origins_,locals_;
public:
 explicit Map(std::vector<Row> rows){
  if(rows.size()>65536)Invalid("too many package resource mappings");
  for(auto& row:rows){
   row.origin=Normalize(row.origin);row.local=Normalize(row.local);
   if(!origins_.emplace(row.origin,row.local).second||!locals_.emplace(row.local,row.origin).second)Invalid("duplicate package resource origin or materialized path");
  }
  const OrdinalIgnoreCase less;
  for(const auto& row:origins_){auto local=locals_.find(row.first);if(local!=locals_.end()&&(less(row.first,row.second)||less(row.second,row.first)))Invalid("package origin/materialized identities overlap ambiguously");}
 }
 std::wstring Resolve(const std::wstring& base,const std::wstring& request)const {
  const auto parent=locals_.find(Normalize(base));
  if(parent==locals_.end())Invalid("package resource base file is not mapped");
  if(request.empty())Invalid("empty package resource request");
  std::wstring absolute;
  if(Drive(request))absolute=Normalize(request);
  else {
   if(request[0]==L'\\'||request[0]==L'/'||request.find(L':')!=std::wstring::npos)Invalid("relative resource path has no unambiguous package origin");
   const auto& origin=parent->second;absolute=Normalize(origin.substr(0,origin.find_last_of(L'\\')+1)+request);
  }
  auto found=origins_.find(absolute);if(found!=origins_.end())return found->second;
  auto local=locals_.find(absolute);if(local!=locals_.end())return local->first;
  Invalid("requested effect resource is absent from the frozen package map");return {};
 }
 bool ContainsLocal(const std::wstring& local)const{return locals_.count(Normalize(local))!=0;}
};
}
