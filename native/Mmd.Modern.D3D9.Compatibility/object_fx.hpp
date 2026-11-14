// Project-owned CPU configuration contract; contains no device or path I/O.
#pragma once
#include "frame_packet.hpp"
#include <set>
namespace objectfx {
enum Assignment : uint32_t { Inherit=0, Standard=1, File=2 };
struct Row { uint64_t object; int32_t subset; uint32_t assignment; std::string path; };
inline void Invalid(const char* text){throw std::runtime_error(std::string("object FX: ")+text);}
inline bool Absolute(const std::string& path){
 return path.size()>2&&((path[0]>='A'&&path[0]<='Z')||(path[0]>='a'&&path[0]<='z'))&&path[1]==':'&&(path[2]=='\\'||path[2]=='/');
}
inline void ValidateRows(const std::vector<Row>& rows) {
 if(rows.size()>65536)Invalid("too many assignments");
 std::set<std::pair<uint64_t,int32_t>> keys;
 for(const auto& row:rows){
  if(!row.object||row.subset< -1||row.assignment>File)Invalid("invalid object, subset or assignment");
  if(!keys.emplace(row.object,row.subset).second)Invalid("duplicate object/subset assignment");
  if(row.assignment==File){
   if(row.path.empty()||row.path.size()>32767||row.path.find('\0')!=std::string::npos||!Absolute(row.path))Invalid("file requires an absolute drive path without NUL");
  }else if(!row.path.empty())Invalid("inherit/standard assignment cannot carry a path");
 }
}
inline const Row* Resolve(const std::vector<Row>& rows,uint64_t object,int32_t subset){
 const Row* whole=nullptr;
 for(const auto& row:rows){
  if(row.object!=object)continue;
  if(row.subset==subset&&row.assignment!=Inherit)return row.assignment==File?&row:nullptr;
  if(row.subset== -1&&row.assignment==File)whole=&row;
 }
 return whole;
}
inline void ValidateFrame(const std::vector<Row>& rows,const packet::Frame& frame) {
 for(const auto& row:rows){
  auto found=std::find_if(frame.objects.begin(),frame.objects.end(),[&](const auto& object){return object.id==row.object;});
  if(found==frame.objects.end())Invalid("configured object is absent from the frozen frame");
  // Hidden objects intentionally carry no geometry/materials in FRM1. Their
  // configured files still compile/admit, but ranges are checked when visible.
  if(found->visible&&row.subset>=0&&uint32_t(row.subset)>=found->materialCount)Invalid("visible object subset is out of range");
 }
}
}
