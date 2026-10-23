// C++/CLI bridge DLL injected into the PCL process via remote LoadLibrary.
// It waits on a background thread, then loads PclDl.Plugin.dll with reflection
// and calls PluginEntry.Bootstrap. The CLR is already started by PCL itself.
#include <windows.h>

#using <System.dll>

using namespace System;
using namespace System::IO;
using namespace System::Reflection;

static String^ BaseDirectory()
{
    wchar_t buffer[32768];
    DWORD length = GetModuleFileNameW(NULL, buffer, 32768);
    String^ path = gcnew String(buffer, 0, (int)length);
    int index = path->LastIndexOf(L'\\');
    return index < 0 ? path : path->Substring(0, index + 1);
}

static void LogBridge(String^ message)
{
    try
    {
        String^ dir = Path::Combine(BaseDirectory(), "PCL2Plugins");
        Directory::CreateDirectory(dir);
        File::AppendAllText(Path::Combine(dir, "bridge.log"),
            DateTime::Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment::NewLine);
    }
    catch (...)
    {
    }
}

static void Worker()
{
    try
    {
        LogBridge("bridge: worker start");
        String^ dll = Path::Combine(BaseDirectory(), "PCL2Plugins\\PclDl.Plugin.dll");
        if (!File::Exists(dll))
        {
            LogBridge("bridge: plugin dll not found: " + dll);
            return;
        }

        Assembly^ assembly = Assembly::LoadFrom(dll);
        Type^ entry = assembly->GetType("PclDl.Plugin.PluginEntry", false);
        if (entry == nullptr)
        {
            LogBridge("bridge: PluginEntry not found");
            return;
        }

        MethodInfo^ method = entry->GetMethod("Bootstrap", BindingFlags::Public | BindingFlags::Static);
        if (method == nullptr)
        {
            LogBridge("bridge: Bootstrap not found");
            return;
        }

        array<Object^>^ args = gcnew array<Object^>(1);
        args[0] = "1";
        Object^ result = method->Invoke(nullptr, args);
        LogBridge("bridge: Bootstrap returned " + (result == nullptr ? "null" : result->ToString()));
    }
    catch (Exception^ ex)
    {
        LogBridge("bridge: failed " + ex->ToString());
    }
}

static DWORD WINAPI ThreadProc(LPVOID)
{
    Worker();
    return 0;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
        HANDLE thread = CreateThread(NULL, 0, ThreadProc, NULL, 0, NULL);
        if (thread != NULL) CloseHandle(thread);
    }
    return TRUE;
}
