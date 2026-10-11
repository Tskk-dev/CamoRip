#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <memory>
#include <cstring>
using Microsoft::WRL::ComPtr;
#define HR(expr) do { HRESULT hr_ = (expr); if (FAILED(hr_)) return hr_; } while (0)
namespace {
struct Preview {
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<IDXGISwapChain1> swap;
    ComPtr<ID3D11RenderTargetView> target;
    ComPtr<ID3D11Texture2D> y, uv;
    ComPtr<ID3D11ShaderResourceView> yView, uvView;
    ComPtr<ID3D11VertexShader> vs;
    ComPtr<ID3D11PixelShader> ps;
    ComPtr<ID3D11SamplerState> sampler;
    ComPtr<ID3D11Buffer> constants;
    UINT width = 0, height = 0, videoWidth = 0, videoHeight = 0;
    HRESULT Initialize() {
        HR(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, &context));
        ComPtr<IDXGIDevice> dxgi; HR(device.As(&dxgi));
        ComPtr<IDXGIAdapter> adapter; HR(dxgi->GetAdapter(&adapter));
        ComPtr<IDXGIFactory2> factory; HR(adapter->GetParent(IID_PPV_ARGS(&factory)));
        DXGI_SWAP_CHAIN_DESC1 desc{}; desc.Width = desc.Height = 1;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; desc.BufferCount = 2;
        desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL; desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
        HR(factory->CreateSwapChainForComposition(device.Get(), &desc, nullptr, &swap));
        const char* shader = R"(
cbuffer Options : register(b0) { float2 scale; float mirror; float unused; };
struct V { float4 position : SV_Position; float2 uv : TEXCOORD0; };
V vertex(uint id : SV_VertexID) {
    float2 positions[4] = { float2(-1,1), float2(1,1), float2(-1,-1), float2(1,-1) };
    float2 coords[4] = { float2(0,0), float2(1,0), float2(0,1), float2(1,1) };
    V o; o.position = float4(positions[id] * scale,0,1); o.uv = coords[id];
    if (mirror > 0.5) o.uv.x = 1-o.uv.x; return o;
}
Texture2D<float> luma : register(t0); Texture2D<float2> chroma : register(t1);
SamplerState sampling : register(s0);
float4 pixel(V i) : SV_Target {
    float y = (luma.Sample(sampling,i.uv)-16.0/255.0)*255.0/219.0;
    float2 c = (chroma.Sample(sampling,i.uv)-128.0/255.0)*255.0/224.0;
    return float4(saturate(float3(y+1.5748*c.y, y-0.187324*c.x-0.468124*c.y, y+1.8556*c.x)),1);
})";
        ComPtr<ID3DBlob> v, p, errors;
        HR(D3DCompile(shader, strlen(shader), nullptr, nullptr, nullptr, "vertex", "vs_4_0", 0, 0, &v, &errors));
        HR(D3DCompile(shader, strlen(shader), nullptr, nullptr, nullptr, "pixel", "ps_4_0", 0, 0, &p, &errors));
        HR(device->CreateVertexShader(v->GetBufferPointer(), v->GetBufferSize(), nullptr, &vs));
        HR(device->CreatePixelShader(p->GetBufferPointer(), p->GetBufferSize(), nullptr, &ps));
        D3D11_SAMPLER_DESC sample{}; sample.Filter = D3D11_FILTER_MIN_MAG_LINEAR_MIP_POINT;
        sample.AddressU = sample.AddressV = sample.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        sample.MaxLOD = D3D11_FLOAT32_MAX; HR(device->CreateSamplerState(&sample, &sampler));
        D3D11_BUFFER_DESC cb{}; cb.ByteWidth = 16; cb.Usage = D3D11_USAGE_DEFAULT; cb.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        HR(device->CreateBuffer(&cb, nullptr, &constants));
        return Resize(1,1);
    }
    HRESULT Resize(UINT w, UINT h) {
        if (width == w && height == h) return S_OK;
        context->OMSetRenderTargets(0,nullptr,nullptr); target.Reset();
        HR(swap->ResizeBuffers(0,w,h,DXGI_FORMAT_UNKNOWN,0));
        ComPtr<ID3D11Texture2D> buffer; HR(swap->GetBuffer(0,IID_PPV_ARGS(&buffer)));
        HR(device->CreateRenderTargetView(buffer.Get(),nullptr,&target));
        width=w; height=h; return S_OK;
    }
    HRESULT Texture(UINT w, UINT h, DXGI_FORMAT format, ComPtr<ID3D11Texture2D>& texture, ComPtr<ID3D11ShaderResourceView>& view) {
        texture.Reset(); view.Reset(); D3D11_TEXTURE2D_DESC d{};
        d.Width=w; d.Height=h; d.MipLevels=d.ArraySize=1; d.Format=format;
        d.SampleDesc.Count=1; d.Usage=D3D11_USAGE_DEFAULT; d.BindFlags=D3D11_BIND_SHADER_RESOURCE;
        HR(device->CreateTexture2D(&d,nullptr,&texture)); HR(device->CreateShaderResourceView(texture.Get(),nullptr,&view)); return S_OK;
    }
    HRESULT Render(const BYTE* bytes, UINT w, UINT h, bool fill, bool mirror) {
        if (videoWidth!=w || videoHeight!=h) {
            ID3D11ShaderResourceView* empty[2]{}; context->PSSetShaderResources(0,2,empty);
            HR(Texture(w,h,DXGI_FORMAT_R8_UNORM,y,yView));
            HR(Texture(w/2,h/2,DXGI_FORMAT_R8G8_UNORM,uv,uvView)); videoWidth=w; videoHeight=h;
        }
        context->UpdateSubresource(y.Get(),0,nullptr,bytes,w,0);
        context->UpdateSubresource(uv.Get(),0,nullptr,bytes+size_t(w)*h,w,0);
        const float ratio=(float(w)/h)/(float(width)/height);
        float options[4] = { 1,1,mirror?1.0f:0.0f,0 };
        if ((ratio>1) != fill) options[1]=1/ratio; else options[0]=ratio;
        context->UpdateSubresource(constants.Get(),0,nullptr,options,0,0);
        const float black[4]={0,0,0,1}; context->ClearRenderTargetView(target.Get(),black);
        ID3D11RenderTargetView* output=target.Get(); context->OMSetRenderTargets(1,&output,nullptr);
        D3D11_VIEWPORT viewport{0,0,float(width),float(height),0,1}; context->RSSetViewports(1,&viewport);
        context->IASetInputLayout(nullptr); context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        context->VSSetShader(vs.Get(),nullptr,0); ID3D11Buffer* cb=constants.Get(); context->VSSetConstantBuffers(0,1,&cb);
        context->PSSetShader(ps.Get(),nullptr,0); ID3D11SamplerState* sampling=sampler.Get(); context->PSSetSamplers(0,1,&sampling);
        ID3D11ShaderResourceView* inputs[]={yView.Get(),uvView.Get()}; context->PSSetShaderResources(0,2,inputs);
        context->Draw(4,0);
        // Non-blocking: preview must never hold up USB/decode/webcam publication.
        HRESULT result=swap->Present(0,DXGI_PRESENT_DO_NOT_WAIT);
        return result==DXGI_ERROR_WAS_STILL_DRAWING ? S_FALSE : result;
    }
};
}
extern "C" __declspec(dllexport) HRESULT __stdcall UCamPreviewCreate(void** result, IUnknown** swap) {
    if (!result || !swap) return E_POINTER; *result=nullptr; *swap=nullptr;
    try { auto p=std::make_unique<Preview>(); HR(p->Initialize()); HR(p->swap.CopyTo(swap)); *result=p.release(); return S_OK; }
    catch (...) { return E_FAIL; }
}
extern "C" __declspec(dllexport) HRESULT __stdcall UCamPreviewResize(void* p, UINT w, UINT h, float scale) {
    if (!p || !w || !h || w>16384 || h>16384 || !(scale>0)) return E_INVALIDARG;
    auto preview=static_cast<Preview*>(p); HR(preview->Resize(w,h));
    ComPtr<IDXGISwapChain2> swap; HR(preview->swap.As(&swap));
    DXGI_MATRIX_3X2_F matrix{1/scale,0,0,1/scale,0,0}; return swap->SetMatrixTransform(&matrix);
}
extern "C" __declspec(dllexport) HRESULT __stdcall UCamPreviewRender(void* p, const BYTE* data, int length, UINT w, UINT h, int fill, int mirror) {
    if (!p || !data || !w || !h || (w|h)&1 || w>4096 || h>4096 || size_t(length)<size_t(w)*h*3/2) return E_INVALIDARG;
    return static_cast<Preview*>(p)->Render(data,w,h,fill!=0,mirror!=0);
}
extern "C" __declspec(dllexport) void __stdcall UCamPreviewDestroy(void* p) { delete static_cast<Preview*>(p); }
