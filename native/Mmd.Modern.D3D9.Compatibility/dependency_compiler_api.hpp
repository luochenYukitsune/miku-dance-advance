// Project-owned CPU compiler ID3DXBaseEffect COM ABI prefix, slots 0..56 only.
// ALTERED: derived from fixed MikuDanceStudio d3dx9.h, MIT; copyright retained in compatibility/d3d9-mme-host/upstream/LICENSE.
// No cast to ID3DXEffect; no effect-only/device methods are available.
// Copyright (c) 2026 MikuDanceStudio contributors
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
#pragma once
#include "d3dx9.h"
struct MdaDependencyEffectCompiler : public IUnknown {

    // --- ID3DXBaseEffect (slots 3..56) --------------------------------------
    virtual HRESULT __stdcall GetDesc(D3DXEFFECT_DESC* desc) = 0;                 // 3
    virtual HRESULT __stdcall GetParameterDesc(D3DXHANDLE parameter,
                                               D3DXPARAMETER_DESC* desc) = 0;    // 4
    virtual HRESULT __stdcall GetTechniqueDesc(D3DXHANDLE technique,
                                               D3DXTECHNIQUE_DESC* desc) = 0;    // 5
    virtual HRESULT __stdcall GetPassDesc(D3DXHANDLE pass,
                                          D3DXPASS_DESC* desc) = 0;              // 6
    virtual HRESULT __stdcall GetFunctionDesc(D3DXHANDLE function,
                                              D3DXFUNCTION_DESC* desc) = 0; // 7
    virtual D3DXHANDLE __stdcall GetParameter(D3DXHANDLE object,
                                              unsigned int index) = 0;           // 8
    virtual D3DXHANDLE __stdcall GetParameterByName(D3DXHANDLE object,
                                                    const char* name) = 0;       // 9
    virtual D3DXHANDLE __stdcall GetParameterBySemantic(D3DXHANDLE object,
                                                        const char* semantic) = 0;// 10
    virtual D3DXHANDLE __stdcall GetParameterElement(D3DXHANDLE array,
                                                     unsigned int index) = 0;    // 11
    virtual D3DXHANDLE __stdcall GetTechnique(unsigned int index) = 0;            // 12
    virtual D3DXHANDLE __stdcall GetTechniqueByName(const char* name) = 0;        // 13
    virtual D3DXHANDLE __stdcall GetPass(D3DXHANDLE technique,
                                        unsigned int index) = 0;                 // 14
    virtual D3DXHANDLE __stdcall GetPassByName(D3DXHANDLE technique,
                                               const char* name) = 0;             // 15
    virtual D3DXHANDLE __stdcall GetFunction(unsigned int index) = 0;             // 16
    virtual D3DXHANDLE __stdcall GetFunctionByName(const char* name) = 0;         // 17
    virtual D3DXHANDLE __stdcall GetAnnotation(D3DXHANDLE object,
                                               unsigned int index) = 0;          // 18
    virtual D3DXHANDLE __stdcall GetAnnotationByName(D3DXHANDLE object,
                                                     const char* name) = 0;      // 19
    virtual HRESULT __stdcall SetValue(D3DXHANDLE parameter, const void* data,
                                       unsigned int bytes) = 0;                  // 20
    virtual HRESULT __stdcall GetValue(D3DXHANDLE parameter, void* data,
                                       unsigned int bytes) = 0;                  // 21
    virtual HRESULT __stdcall SetBool(D3DXHANDLE parameter, BOOL value) = 0;      // 22
    virtual HRESULT __stdcall GetBool(D3DXHANDLE parameter, BOOL* value) = 0;     // 23
    virtual HRESULT __stdcall SetBoolArray(D3DXHANDLE parameter, const BOOL* values,
                                           unsigned int count) = 0;               // 24
    virtual HRESULT __stdcall GetBoolArray(D3DXHANDLE parameter, BOOL* values,
                                           unsigned int count) = 0;               // 25
    virtual HRESULT __stdcall SetInt(D3DXHANDLE parameter, int value) = 0;        // 26
    virtual HRESULT __stdcall GetInt(D3DXHANDLE parameter, int* value) = 0;       // 27
    virtual HRESULT __stdcall SetIntArray(D3DXHANDLE parameter, const int* values,
                                          unsigned int count) = 0;                // 28
    virtual HRESULT __stdcall GetIntArray(D3DXHANDLE parameter, int* values,
                                          unsigned int count) = 0;                // 29
    virtual HRESULT __stdcall SetFloat(D3DXHANDLE parameter, float value) = 0;    // 30
    virtual HRESULT __stdcall GetFloat(D3DXHANDLE parameter, float* value) = 0;   // 31
    virtual HRESULT __stdcall SetFloatArray(D3DXHANDLE parameter, const float* values,
                                            unsigned int count) = 0;              // 32
    virtual HRESULT __stdcall GetFloatArray(D3DXHANDLE parameter, float* values,
                                            unsigned int count) = 0;              // 33
    virtual HRESULT __stdcall SetVector(D3DXHANDLE parameter,
                                        const D3DXVECTOR4* vector) = 0;          // 34
    virtual HRESULT __stdcall GetVector(D3DXHANDLE parameter,
                                        D3DXVECTOR4* vector) = 0;                // 35
    virtual HRESULT __stdcall SetVectorArray(D3DXHANDLE parameter,
                                             const D3DXVECTOR4* vector,
                                             unsigned int count) = 0;             // 36
    virtual HRESULT __stdcall GetVectorArray(D3DXHANDLE parameter,
                                             D3DXVECTOR4* vector,
                                             unsigned int count) = 0;             // 37
    virtual HRESULT __stdcall SetMatrix(D3DXHANDLE parameter,
                                        const D3DMATRIX* matrix) = 0;            // 38
    virtual HRESULT __stdcall GetMatrix(D3DXHANDLE parameter,
                                        D3DMATRIX* matrix) = 0;                  // 39
    virtual HRESULT __stdcall SetMatrixArray(D3DXHANDLE parameter,
                                             const D3DMATRIX* matrix,
                                             unsigned int count) = 0;             // 40
    virtual HRESULT __stdcall GetMatrixArray(D3DXHANDLE parameter,
                                             D3DMATRIX* matrix,
                                             unsigned int count) = 0;             // 41
    virtual HRESULT __stdcall SetMatrixPointerArray(D3DXHANDLE parameter,
                                                    const D3DMATRIX** matrix,
                                                    unsigned int count) = 0;      // 42
    virtual HRESULT __stdcall GetMatrixPointerArray(D3DXHANDLE parameter,
                                                    D3DMATRIX** matrix,
                                                    unsigned int count) = 0;      // 43
    virtual HRESULT __stdcall SetMatrixTranspose(D3DXHANDLE parameter,
                                                 const D3DMATRIX* matrix) = 0;    // 44
    virtual HRESULT __stdcall GetMatrixTranspose(D3DXHANDLE parameter,
                                                 D3DMATRIX* matrix) = 0;          // 45
    virtual HRESULT __stdcall SetMatrixTransposeArray(D3DXHANDLE parameter,
                                                      const D3DMATRIX* matrix,
                                                      unsigned int count) = 0;     // 46
    virtual HRESULT __stdcall GetMatrixTransposeArray(D3DXHANDLE parameter,
                                                      D3DMATRIX* matrix,
                                                      unsigned int count) = 0;     // 47
    virtual HRESULT __stdcall SetMatrixTransposePointerArray(D3DXHANDLE parameter,
                                                             const D3DMATRIX** matrix,
                                                             unsigned int count) = 0;// 48
    virtual HRESULT __stdcall GetMatrixTransposePointerArray(D3DXHANDLE parameter,
                                                             D3DMATRIX** matrix,
                                                             unsigned int count) = 0;// 49
    virtual HRESULT __stdcall SetString(D3DXHANDLE parameter,
                                        const char* string) = 0;                  // 50
    virtual HRESULT __stdcall GetString(D3DXHANDLE parameter,
                                        const char** string) = 0;                 // 51
    virtual HRESULT __stdcall SetTexture(D3DXHANDLE parameter,
                                         IDirect3DBaseTexture9* texture) = 0;     // 52
    virtual HRESULT __stdcall GetTexture(D3DXHANDLE parameter,
                                         IDirect3DBaseTexture9** texture) = 0;    // 53
    virtual HRESULT __stdcall GetPixelShader(D3DXHANDLE parameter,
                                             IDirect3DPixelShader9** shader) = 0; // 54
    virtual HRESULT __stdcall GetVertexShader(D3DXHANDLE parameter,
                                              IDirect3DVertexShader9** shader) = 0; // 55
    virtual HRESULT __stdcall SetArrayRange(D3DXHANDLE parameter, unsigned int start,
                                            unsigned int end) = 0;                // 56

    
};
