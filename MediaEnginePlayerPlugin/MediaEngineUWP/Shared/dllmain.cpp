//*********************************************************
//
// Copyright (c) Microsoft. All rights reserved.
// This code is licensed under the MIT License (MIT).
// THIS CODE IS PROVIDED *AS IS* WITHOUT WARRANTY OF
// ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING ANY
// IMPLIED WARRANTIES OF FITNESS FOR A PARTICULAR
// PURPOSE, MERCHANTABILITY, OR NON-INFRINGEMENT.
//
//*********************************************************

#include "MediaEngine.h"
#include "Unity/PlatformBase.h"
#include "MediaEnginePlayer.h"
#include <map>

using namespace Microsoft::WRL;
using namespace Platform;
using namespace Windows::Foundation;

using ABI::Windows::Foundation::Collections::IMap;
using ABI::Windows::Foundation::Collections::IPropertySet;
using Microsoft::WRL::Wrappers::HStringReference;

static UnityGfxRenderer s_DeviceType = kUnityGfxRenderernullptr;
static IUnityInterfaces* s_UnityInterfaces = nullptr;
static IUnityGraphics* s_Graphics = nullptr;

static std::map<UINT32, MEPlayer^> s_playerMap;

static Microsoft::WRL::ComPtr<ABI::Windows::Media::IMediaExtensionManager> s_mediaExtensionManager;
static Microsoft::WRL::ComPtr<ABI::Windows::Foundation::Collections::IMap<HSTRING, IInspectable*>> s_extensionManagerProperties;

void SetupSchemeHandler()
{
	using Windows::Foundation::ActivateInstance;
	// Create a media extension manager.  It's used to register a scheme handler.
	HRESULT hr = ActivateInstance(HStringReference(RuntimeClass_Windows_Media_MediaExtensionManager).Get(), &s_mediaExtensionManager);
	if (FAILED(hr))
	{
		throw ref new COMException(hr, ref new String(L"Failed to create media extension manager"));
	}
	// Create an IMap container.  It maps a source URL with an IMediaSource so it can be retrieved by the scheme handler.
	ComPtr<IMap<HSTRING, IInspectable*>> props;
	hr = ActivateInstance(HStringReference(RuntimeClass_Windows_Foundation_Collections_PropertySet).Get(), &props);
	if (FAILED(hr))
	{
		throw ref new COMException(hr, ref new String(L"Failed to create collection property set"));
	}
	// Register the scheme handler.  It takes the IMap container so it can be passed to the scheme
	// handler when its invoked with a given source URL.
	// The SchemeHandler will extract the IMediaSource from the map.
	ComPtr<IPropertySet> propSet;
	props.As(&propSet);
	HStringReference clsid(L"WebRtcScheme.SchemeHandler");
	HStringReference scheme(L"webrtc:");
	hr = s_mediaExtensionManager->RegisterSchemeHandlerWithSettings(clsid.Get(), scheme.Get(), propSet.Get());
	if (FAILED(hr))
	{
		throw ref new COMException(hr, ref new String(L"Failed to to register scheme handler"));
	}
	s_extensionManagerProperties = props;
}

STDAPI_(BOOL) DllMain(
    _In_opt_ HINSTANCE hInstance, _In_ DWORD dwReason, _In_opt_ LPVOID lpReserved)
{
    UNREFERENCED_PARAMETER(lpReserved);

    if (DLL_PROCESS_ATTACH == dwReason)
    {
        //  Don't need per-thread callbacks
        DisableThreadLibraryCalls(hInstance);

        Microsoft::WRL::Module<Microsoft::WRL::InProc>::GetModule().Create();

		SetupSchemeHandler();
    }
    else if (DLL_PROCESS_DETACH == dwReason)
    {
        Microsoft::WRL::Module<Microsoft::WRL::InProc>::GetModule().Terminate();
		s_mediaExtensionManager.Reset();
		s_extensionManagerProperties.Reset();
    }

    return TRUE;
}

STDAPI DllGetActivationFactory(_In_ HSTRING activatibleClassId, _COM_Outptr_ IActivationFactory** factory)
{
    auto &module = Microsoft::WRL::Module< Microsoft::WRL::InProc>::GetModule();
    return module.GetActivationFactory(activatibleClassId, factory);
}

STDAPI DllCanUnloadNow()
{
    const auto &module = Microsoft::WRL::Module<Microsoft::WRL::InProc>::GetModule();
    return module.GetObjectCount() == 0 ? S_OK : S_FALSE;
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API CreateMediaPlayback(_In_ UINT32 id)
{
	Log(Log_Level_Info, L"CMediaEnginePlayer::CreateMediaPlayback()");

	if (nullptr == s_UnityInterfaces)
		return;

	if (s_DeviceType == kUnityGfxRendererD3D11)
	{
		wchar_t texLabelBuffer[100];
		int cx = swprintf(texLabelBuffer, 100, L"SharedTextureHandle_%d", id);

		String^ texLabel = ref new String(texLabelBuffer);

		IUnityGraphicsD3D11* d3d = s_UnityInterfaces->Get<IUnityGraphicsD3D11>();
		s_playerMap[id] = ref new MEPlayer(d3d->GetDevice(), texLabel, s_extensionManagerProperties);
	}
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ReleaseMediaPlayback(_In_ UINT32 id)
{
	auto key = id;

	auto it = s_playerMap.find(key);
	if (it != s_playerMap.end()) {
		auto & player = it->second;
		if (player != nullptr) {
			player->Pause();
			player->Shutdown();
			player = nullptr;
		}
		s_playerMap[key] = nullptr;
	}
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API GetPrimaryTexture(_In_ UINT32 id, _In_ UINT32 width, _In_ UINT32 height, _COM_Outptr_ void** playbackSRV)
{
	auto key = id;

	auto it = s_playerMap.find(key);
	if (it != s_playerMap.end()) {
		auto & player = it->second;
		if (player != nullptr) {
			player->GetPrimaryTexture(width, height, playbackSRV);
		}
	}
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API LoadMediaStreamSource(_In_ UINT32 id, Windows::Media::Core::IMediaStreamSource^ mediaSourceHandle)
{
	auto key = id;

	if (mediaSourceHandle != nullptr) {
		auto it = s_playerMap.find(key);
		if (it != s_playerMap.end()) {
			auto & player = it->second;
			if (player != nullptr) {
				player->SetMediaStreamSource(mediaSourceHandle);
			}
		}
	}
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnloadMediaStreamSource(_In_ UINT32 id)
{
	auto key = id;

	auto it = s_playerMap.find(key);
	if (it != s_playerMap.end()) {
		auto & player = it->second;
		if (player != nullptr) {
			player->SetMediaStreamSource(nullptr);
		}
	}
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API Play(_In_ UINT32 id)
{
	auto key = id;

	auto it = s_playerMap.find(key);
	if (it != s_playerMap.end()) {
		auto & player = it->second;
		if (player != nullptr) {
			player->Play();
		}
	}
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API Pause(_In_ UINT32 id)
{
	auto key = id;

	auto it = s_playerMap.find(key);
	if (it != s_playerMap.end()) {
		auto & player = it->second;
		if (player != nullptr) {
			player->Pause();
		}
	}
}

// --------------------------------------------------------------------------
// UnitySetInterfaces

// GraphicsDeviceEvent
static void UNITY_INTERFACE_API OnGraphicsDeviceEvent(UnityGfxDeviceEventType eventType)
{
    // Create graphics API implementation upon initialization
    if (eventType == kUnityGfxDeviceEventInitialize)
    {
        s_DeviceType = s_Graphics->GetRenderer();
    }

    // Cleanup graphics API implementation upon shutdown
    if (eventType == kUnityGfxDeviceEventShutdown)
    {
        s_DeviceType = kUnityGfxRenderernullptr;
    }
}

extern "C" void	UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* unityInterfaces)
{
    s_UnityInterfaces = unityInterfaces;
    s_Graphics = s_UnityInterfaces->Get<IUnityGraphics>();
    s_Graphics->RegisterDeviceEventCallback(OnGraphicsDeviceEvent);

    // Run OnGraphicsDeviceEvent(initialize) manually on plugin load
    OnGraphicsDeviceEvent(kUnityGfxDeviceEventInitialize);
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload()
{
    s_Graphics->UnregisterDeviceEventCallback(OnGraphicsDeviceEvent);
}
