// Test the shipped shader code through D3D11 WARP on headless CI runners.
#include "Preview.cpp"
#include <vector>
#include <algorithm>
#include <cstdio>
#define VERIFY(test) do { if (!(test)) { std::fprintf(stderr,"Failed line %d: %s\n",__LINE__,#test); return 1; } } while (0)
int main() {
    VERIFY(UCamPreviewCreate(nullptr,nullptr)==E_POINTER);
    VERIFY(UCamPreviewResize(nullptr,16,16,1)==E_INVALIDARG);
    VERIFY(UCamPreviewRender(nullptr,nullptr,0,16,16,0,0)==E_INVALIDARG);
    Preview preview;
    VERIFY(SUCCEEDED(preview.Initialize(D3D_DRIVER_TYPE_WARP)));
    VERIFY(SUCCEEDED(preview.Resize(16,16)));
    ComPtr<ID3D11Texture2D> output, staging;
    VERIFY(SUCCEEDED(preview.swap->GetBuffer(0,IID_PPV_ARGS(&output))));
    D3D11_TEXTURE2D_DESC desc{}; output->GetDesc(&desc);
    desc.Usage=D3D11_USAGE_STAGING; desc.BindFlags=0; desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ; desc.MiscFlags=0;
    VERIFY(SUCCEEDED(preview.device->CreateTexture2D(&desc,nullptr,&staging)));
    auto pixel=[&](UINT x, UINT y) {
        preview.context->CopyResource(staging.Get(),output.Get());
        D3D11_MAPPED_SUBRESOURCE mapped{};
        if (FAILED(preview.context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped))) return -1;
        const int value=static_cast<BYTE*>(mapped.pData)[y*mapped.RowPitch+x*4];
        preview.context->Unmap(staging.Get(),0); return value;
    };
    std::vector<BYTE> image(16*8*3/2,128);
    std::fill(image.begin(),image.begin()+16*8,235);
    VERIFY(SUCCEEDED(preview.Render(image.data(),16,8,false,false,false)));
    VERIFY(pixel(8,1)<3); VERIFY(pixel(8,8)>250); // Fit letterbox and video-range white.
    VERIFY(SUCCEEDED(preview.Render(image.data(),16,8,true,false,false)));
    VERIFY(pixel(8,1)>250); // Fill crops to the surface.
    image.assign(16*16*3/2,128);
    for (int y=0;y<16;y++) for (int x=0;x<16;x++) image[y*16+x]=x<8?16:235;
    VERIFY(SUCCEEDED(preview.Render(image.data(),16,16,false,false,false)));
    VERIFY(pixel(3,8)<3); VERIFY(pixel(12,8)>250);
    VERIFY(SUCCEEDED(preview.Render(image.data(),16,16,false,true,false)));
    VERIFY(pixel(3,8)>250); VERIFY(pixel(12,8)<3); // Preview-only mirroring.
    std::puts("Preview argument, NV12 range, fit/fill and mirror checks passed.");
    return 0;
}
