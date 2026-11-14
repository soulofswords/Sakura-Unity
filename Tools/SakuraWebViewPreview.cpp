#include <Windows.h>
#include <objbase.h>
#include <cstdint>
#include <cstring>
#include "WebViewToolkit/WebViewManager.h"
#include "WebViewToolkit/WebView.h"
namespace WebViewToolkit { WebViewManager* GetWebViewManager(); }

#include <WebView2.h>
#include <wrl.h>
#include <vector>
#include <unordered_map>
#include <mutex>
#include <memory>
namespace {
struct SakuraPreview { std::mutex mutex; bool ready=false; bool failed=false; std::vector<unsigned char> bytes; };
std::mutex sakuraPreviewMutex;
std::unordered_map<uint32_t,std::shared_ptr<SakuraPreview>> sakuraPreviews;
}
extern "C" __declspec(dllexport) int32_t WebViewToolkit_RequestPreview(uint32_t handle) {
    auto manager=WebViewToolkit::GetWebViewManager();
    auto view=manager ? manager->GetWebView(handle) : nullptr;
    if(!view || !view->IsReady() || !view->GetController()) return -1;
    auto slot=std::make_shared<SakuraPreview>();
    { std::lock_guard<std::mutex> lock(sakuraPreviewMutex);
      if(sakuraPreviews.find(handle)!=sakuraPreviews.end() || sakuraPreviews.size()>=64) return -1;
      sakuraPreviews[handle]=slot; }
    Microsoft::WRL::ComPtr<ICoreWebView2> core;
    HRESULT hr=static_cast<ICoreWebView2Controller*>(view->GetController())->get_CoreWebView2(&core);
    Microsoft::WRL::ComPtr<IStream> stream;
    if(SUCCEEDED(hr)) hr=CreateStreamOnHGlobal(nullptr, TRUE, &stream);
    if(SUCCEEDED(hr)) hr=core->CapturePreview(COREWEBVIEW2_CAPTURE_PREVIEW_IMAGE_FORMAT_PNG,stream.Get(),
      Microsoft::WRL::Callback<ICoreWebView2CapturePreviewCompletedHandler>([slot,stream](HRESULT result)->HRESULT {
        std::lock_guard<std::mutex> lock(slot->mutex);
        STATSTG stat={};
        if(FAILED(result) || FAILED(stream->Stat(&stat,STATFLAG_NONAME)) || stat.cbSize.QuadPart==0 || stat.cbSize.QuadPart>16*1024*1024) {slot->failed=true;slot->ready=true;return S_OK;}
        slot->bytes.resize(static_cast<size_t>(stat.cbSize.QuadPart));
        LARGE_INTEGER start={}; stream->Seek(start,STREAM_SEEK_SET,nullptr);
        ULONG read=0; result=stream->Read(slot->bytes.data(),static_cast<ULONG>(slot->bytes.size()),&read);
        slot->failed=FAILED(result)||read!=slot->bytes.size();slot->ready=true;return S_OK;
      }).Get());
    if(FAILED(hr)) {std::lock_guard<std::mutex> lock(slot->mutex);slot->failed=true;slot->ready=true;return -1;}
    return 1;
}
extern "C" __declspec(dllexport) int32_t WebViewToolkit_ReadPreview(uint32_t handle,void* destination,int32_t capacity) {
    std::shared_ptr<SakuraPreview> slot;
    {std::lock_guard<std::mutex> lock(sakuraPreviewMutex);auto found=sakuraPreviews.find(handle);if(found==sakuraPreviews.end())return 0;
     if(capacity<0){sakuraPreviews.erase(found);return 0;}slot=found->second;}
    std::lock_guard<std::mutex> lock(slot->mutex);
    if(!slot->ready)return 0;
    if(slot->failed)return -1;
    int32_t size=static_cast<int32_t>(slot->bytes.size());
    if(!destination || capacity<size)return size;
    memcpy(destination,slot->bytes.data(),size);
    {std::lock_guard<std::mutex> lock2(sakuraPreviewMutex);sakuraPreviews.erase(handle);}
    return size;
}
