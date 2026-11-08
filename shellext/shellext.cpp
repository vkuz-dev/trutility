// "Add to trutility" in Explorer for the MSIX build. The unpackaged build
// registers the same verb under HKCU\Software\Classes, but MSIX keeps a
// package's HKCU writes private, so packaging\AppxManifest.xml declares this
// COM handler instead. It only launches trutility.exe --add per selected item.

#include <windows.h>
#include <appmodel.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <wrl/implements.h>
#include <wrl/module.h>

#include <string>

#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "runtimeobject.lib")   // WRL's error reporting

using namespace Microsoft::WRL;

static HMODULE g_module;

// The package folder, holding this DLL and trutility.exe.
static std::wstring ModuleDir()
{
    wchar_t path[MAX_PATH * 2];
    DWORD length = GetModuleFileNameW(g_module, path, ARRAYSIZE(path));
    std::wstring s(path, length);
    return s.substr(0, s.find_last_of(L'\\'));
}

static std::wstring LocalAppData()
{
    PWSTR path = nullptr;
    std::wstring s;
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &path)))
        s = path;
    CoTaskMemFree(path);
    return s;
}

// Explorer may load us without package identity, so fall back to the install
// folder, which Windows names after the package full name.
static std::wstring PackageFamilyName()
{
    wchar_t name[PACKAGE_FAMILY_NAME_MAX_LENGTH + 1];
    UINT32 length = ARRAYSIZE(name);
    if (GetCurrentPackageFamilyName(&length, name) == ERROR_SUCCESS)
        return name;

    std::wstring dir = ModuleDir();
    std::wstring fullName = dir.substr(dir.find_last_of(L'\\') + 1);
    length = ARRAYSIZE(name);
    if (PackageFamilyNameFromFullName(fullName.c_str(), &length, name) == ERROR_SUCCESS)
        return name;
    return L"";
}

// The app's own setting is invisible from Explorer, so trutility mirrors it
// into this file; see TrayApplication.ShellVerbFlagPath.
static bool IsEnabled()
{
    std::wstring family = PackageFamilyName();
    if (family.empty()) return false;
    std::wstring flag = LocalAppData() + L"\\Packages\\" + family + L"\\LocalState\\shell-verb.on";
    return GetFileAttributesW(flag.c_str()) != INVALID_FILE_ATTRIBUTES;
}

class __declspec(uuid("d8df50e1-e6b1-469f-a378-d24474e0080e")) AddToTrutility final
    : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IExplorerCommand>
{
public:
    IFACEMETHODIMP GetTitle(IShellItemArray*, LPWSTR* name) override
    {
        return SHStrDupW(L"Add to trutility", name);
    }

    IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* icon) override
    {
        return SHStrDupW((ModuleDir() + L"\\trutility.exe,0").c_str(), icon);
    }

    IFACEMETHODIMP GetToolTip(IShellItemArray*, LPWSTR* tip) override
    {
        *tip = nullptr;
        return E_NOTIMPL;
    }

    IFACEMETHODIMP GetCanonicalName(GUID* guid) override
    {
        *guid = __uuidof(AddToTrutility);
        return S_OK;
    }

    IFACEMETHODIMP GetState(IShellItemArray*, BOOL, EXPCMDSTATE* state) override
    {
        *state = IsEnabled() ? ECS_ENABLED : ECS_HIDDEN;
        return S_OK;
    }

    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override
    {
        if (!items) return E_INVALIDARG;
        DWORD count = 0;
        HRESULT hr = items->GetCount(&count);
        if (FAILED(hr)) return hr;

        // The execution alias rather than the exe beside us: only the alias
        // starts trutility with its package identity.
        std::wstring exe = LocalAppData() + L"\\Microsoft\\WindowsApps\\trutility.exe";
        for (DWORD i = 0; i < count; i++)
        {
            ComPtr<IShellItem> item;
            PWSTR path = nullptr;
            if (FAILED(items->GetItemAt(i, &item)) ||
                FAILED(item->GetDisplayName(SIGDN_FILESYSPATH, &path)))
                continue;   // virtual items have no path to add

            std::wstring target(path);
            CoTaskMemFree(path);
            // A trailing backslash would escape the closing quote.
            if (!target.empty() && target.back() == L'\\') target += L'\\';
            std::wstring args = L"--add \"" + target + L"\"";
            ShellExecuteW(nullptr, nullptr, exe.c_str(), args.c_str(), nullptr, SW_SHOWNORMAL);
        }
        return S_OK;
    }

    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override
    {
        *flags = ECF_DEFAULT;
        return S_OK;
    }

    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override
    {
        *commands = nullptr;
        return E_NOTIMPL;
    }
};

CoCreatableClass(AddToTrutility)

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, void*)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void** object)
{
    return Module<InProc>::GetModule().GetClassObject(clsid, iid, object);
}

STDAPI DllCanUnloadNow()
{
    return Module<InProc>::GetModule().GetObjectCount() == 0 ? S_OK : S_FALSE;
}
