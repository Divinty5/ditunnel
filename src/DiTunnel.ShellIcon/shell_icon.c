#define WIN32_LEAN_AND_MEAN
#define UNICODE
#define _UNICODE
#include <windows.h>
#include <psapi.h>
#include <wchar.h>
#include "shell_icon.h"
#include "MinHook.h"

typedef const wchar_t* (WINAPI *GET_ID)(void*);
typedef int (WINAPI *GET_COUNT)(void*);
typedef void* (WINAPI *GET_SHORTCUT)(void*);
typedef void (WINAPI *DRAW_ICON)(void*,void*,void*);
typedef HRESULT (WINAPI *GET_ICON)(void*,void*,HICON*);
static HMODULE self, server_reference;
static GET_ID get_id, original_resource;
static GET_COUNT get_count;
static GET_SHORTCUT original_shortcut;
static DRAW_ICON original_draw;
static GET_ICON original_get_icon;
static void *shortcut_target, *resource_target, *draw_target;
static __declspec(thread) unsigned drawing;
static volatile LONG active;
static HWND owner, taskbar_window;
static DWORD owner_pid;
static UINT_PTR timer;
static int mode;
static BOOL installed;
static HHOOK client_hook;
static HWND client_window;

static BOOL owner_alive(void)
{
    DWORD pid=0;
    return owner && GetWindowThreadProcessId(owner,&pid) && pid==owner_pid &&
        (int)(INT_PTR)GetPropW(owner,SHELL_MODE_PROPERTY)==mode;
}
static BOOL ours(void* group)
{
    const wchar_t* id=get_id(group);
    const wchar_t* expected=mode==2 ? L"DiVintyInteractive.DiTunnel.PublicIconProbe" : L"DiVintyInteractive.DiTunnel";
    return id && _wcsicmp(id,expected)==0 && get_count(group)>0 && owner_alive();
}
static void* WINAPI shortcut_hook(void* group)
{
    void* result;
    InterlockedIncrement(&active);
    result=drawing && ours(group)?NULL:original_shortcut(group);
    InterlockedDecrement(&active);
    return result;
}
static const wchar_t* WINAPI resource_hook(void* group)
{
    const wchar_t* result;
    InterlockedIncrement(&active);
    result=drawing && ours(group)?NULL:original_resource(group);
    InterlockedDecrement(&active);
    return result;
}
static void WINAPI draw_hook(void* group,void* dc,void* info)
{
    InterlockedIncrement(&active); ++drawing;
    original_draw(group,dc,info);
    --drawing; InterlockedDecrement(&active);
}
static HRESULT WINAPI get_icon_hook(void* group,void* item,HICON* icon)
{
    HRESULT result;
    InterlockedIncrement(&active); ++drawing;
    result=original_get_icon(group,item,icon);
    --drawing; InterlockedDecrement(&active);
    return result;
}
static DWORD install_hooks(void* shortcut,void* draw,GET_ID id,GET_COUNT count,void* resource,BOOL modern)
{
    MH_STATUS result=MH_Initialize();
    if(result!=MH_OK)return 1000+result;
    // Even a partially created/enabled set must be torn down before unloading.
    installed=TRUE;
    get_id=id;get_count=count;shortcut_target=shortcut;draw_target=draw;resource_target=resource;
    result=MH_CreateHook(shortcut,(LPVOID)shortcut_hook,(LPVOID*)&original_shortcut);
    if(result==MH_OK)result=MH_CreateHook(resource,(LPVOID)resource_hook,(LPVOID*)&original_resource);
    if(result==MH_OK)result=MH_CreateHook(draw,modern?(LPVOID)get_icon_hook:(LPVOID)draw_hook,
        modern?(LPVOID*)&original_get_icon:(LPVOID*)&original_draw);
    if(result==MH_OK)result=MH_QueueEnableHook(shortcut);
    if(result==MH_OK)result=MH_QueueEnableHook(resource);
    if(result==MH_OK)result=MH_QueueEnableHook(draw);
    if(result==MH_OK)result=MH_ApplyQueued();
    if(result!=MH_OK)return 1000+result;
    return 0;
}
static BOOL remove_hooks(void)
{
    if(!installed)return TRUE;
    // Hooks are scoped to the taskbar UI thread; keep trampolines until its
    // callbacks have returned. On failure retain the module and retry later.
    if(MH_DisableHook(MH_ALL_HOOKS)!=MH_OK)return FALSE;
    if(InterlockedCompareExchange(&active,0,0))return FALSE;
    if(MH_Uninitialize()!=MH_OK)return FALSE;
    installed=FALSE;
    return TRUE;
}
static VOID CALLBACK release_reference(PTP_CALLBACK_INSTANCE callback,void* context)
{
    DWORD_PTR ignored;
    // Barrier after the last UI timer/hook callback. If Explorer is hung,
    // retaining one inactive reference is safer than unloading executing code.
    if(SendMessageTimeoutW(taskbar_window,WM_NULL,0,0,SMTO_ABORTIFHUNG|SMTO_BLOCK,2000,&ignored))
        FreeLibraryWhenCallbackReturns(callback,(HMODULE)context);
}
static void stop_server(void)
{
    owner=NULL;owner_pid=0;
    if(!remove_hooks())return;
    if(timer){KillTimer(NULL,timer);timer=0;}
    if(server_reference){HMODULE held=server_reference;server_reference=NULL;
        if(!TrySubmitThreadpoolCallback(release_reference,held,NULL))server_reference=held;}
}
static VOID CALLBACK owner_timer(HWND window,UINT message,UINT_PTR id,DWORD time)
{
    (void)window;(void)message;(void)id;(void)time;
    if(!owner_alive())stop_server();
}
static BOOL executable_rva(HMODULE module,DWORD rva)
{
    const IMAGE_DOS_HEADER* dos=(const IMAGE_DOS_HEADER*)module;
    const IMAGE_NT_HEADERS* nt=(const IMAGE_NT_HEADERS*)((BYTE*)module+dos->e_lfanew);
    const IMAGE_SECTION_HEADER* section=IMAGE_FIRST_SECTION(nt);
    if(!rva || rva>=nt->OptionalHeader.SizeOfImage)return FALSE;
    for(WORD i=0;i<nt->FileHeader.NumberOfSections;i++,section++)
        if((section->Characteristics & IMAGE_SCN_MEM_EXECUTE) && rva>=section->VirtualAddress &&
            rva-section->VirtualAddress<section->Misc.VirtualSize)return TRUE;
    return FALSE;
}
static BOOL validate_symbols(HMODULE module,const SHELL_SYMBOLS* symbols)
{
    const IMAGE_DOS_HEADER* dos=(const IMAGE_DOS_HEADER*)module;
    const IMAGE_NT_HEADERS* nt=(const IMAGE_NT_HEADERS*)((BYTE*)module+dos->e_lfanew);
    const DWORD* rvas=&symbols->app_id;
    if(symbols->version!=SHELL_PROTOCOL || nt->FileHeader.TimeDateStamp!=symbols->timestamp ||
        nt->OptionalHeader.SizeOfImage!=symbols->image_size)return FALSE;
    for(int i=0;i<5;i++)if(!executable_rva(module,rvas[i]))return FALSE;
    return TRUE;
}
static DWORD install_for_shell(HWND client,int requested_mode,const SHELL_SYMBOLS* symbols)
{
    HMODULE modules[1024],platform=NULL;DWORD size=0,kind=0;
    void *shortcut=NULL,*draw=NULL,*resource=NULL;GET_ID id=NULL;GET_COUNT count=NULL;
    if(installed)return owner==client?0:ERROR_ALREADY_EXISTS;
    if(!EnumProcessModules(GetCurrentProcess(),modules,sizeof(modules),&size))return GetLastError();
    for(unsigned i=0;i<size/sizeof(HMODULE) && i<1024;i++)
    {
        wchar_t path[MAX_PATH];if(!GetModuleFileNameW(modules[i],path,MAX_PATH))continue;
        wchar_t* name=wcsrchr(path,L'\\');name=name?name+1:path;
        if(_wcsnicmp(name,L"ep_taskbar.",11)==0){platform=modules[i];kind=0;break;}
        if(_wcsicmp(name,L"taskbar.dll")==0){platform=modules[i];kind=1;}
    }
    if(!platform){platform=GetModuleHandleW(NULL);kind=2;}
    if(kind==0)
    {
        shortcut=(LPVOID)GetProcAddress(platform,"?GetShortcutIDList@CTaskGroup@@UEAAPEBU_ITEMIDLIST_ABSOLUTE@@XZ");
        resource=(LPVOID)GetProcAddress(platform,"?GetIconResource@CTaskGroup@@UEAAPEBGXZ");
        draw=(LPVOID)GetProcAddress(platform,"?_DrawRegularButton@CTaskBtnGroup@@AEAAXPEAUHDC__@@AEBUBUTTONRENDERINFO@@@Z");
        id=(GET_ID)GetProcAddress(platform,"?GetAppID@CTaskGroup@@UEAAPEBGXZ");
        count=(GET_COUNT)GetProcAddress(platform,"?GetNumItems@CTaskGroup@@UEAAHXZ");
        if(!shortcut || !draw || !id || !count || !resource)return ERROR_PROC_NOT_FOUND;
    }
    else
    {
        if(symbols->module_kind!=kind || !validate_symbols(platform,symbols))return ERROR_REVISION_MISMATCH;
        shortcut=(BYTE*)platform+symbols->shortcut;resource=(BYTE*)platform+symbols->resource;
        draw=(BYTE*)platform+symbols->draw;id=(GET_ID)((BYTE*)platform+symbols->app_id);
        count=(GET_COUNT)((BYTE*)platform+symbols->item_count);
    }
    if(!server_reference && !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
        (LPCWSTR)install_for_shell,&server_reference))return GetLastError();
    owner=client;mode=requested_mode;GetWindowThreadProcessId(client,&owner_pid);
    // Establish crash cleanup before enabling any detour.
    timer=SetTimer(NULL,0,1000,owner_timer);
    if(!timer){DWORD error=GetLastError();stop_server();return error?error:ERROR_NOT_ENOUGH_MEMORY;}
    DWORD result=install_hooks(shortcut,draw,id,count,resource,kind==1);
    if(result)stop_server();
    return result;
}
static BOOL valid_client(HWND client,int requested_mode)
{
    DWORD pid=0;wchar_t path[MAX_PATH],module[MAX_PATH];DWORD length=MAX_PATH;
    if((requested_mode!=1 && requested_mode!=2) || !GetWindowThreadProcessId(client,&pid))return FALSE;
    HANDLE process=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,pid);
    if(!process)return FALSE;
    BOOL found=QueryFullProcessImageNameW(process,0,path,&length);CloseHandle(process);
    if(!found || !GetModuleFileNameW(self,module,MAX_PATH))return FALSE;
    wchar_t *name=wcsrchr(path,L'\\'),*dll_name=wcsrchr(module,L'\\');
    if(!name || !dll_name)return FALSE;
    if(_wcsicmp(name+1,requested_mode==2?L"DiTunnel.Icon.Smoke.exe":L"Di-Tunnel.exe")!=0)return FALSE;
    *name=0;*dll_name=0;
    return _wcsicmp(path,module)==0;
}
__declspec(dllexport) LRESULT CALLBACK DiTunnelShellHook(int code,WPARAM wparam,LPARAM lparam)
{
    if(code>=0)
    {
        const CWPSTRUCT* message=(const CWPSTRUCT*)lparam;
        if(message->message==WM_COPYDATA && message->hwnd==FindWindowW(L"Shell_TrayWnd",NULL))
        {
            const COPYDATASTRUCT* data=(const COPYDATASTRUCT*)message->lParam;
            HWND client=(HWND)message->wParam;
            int requested_mode=(int)(INT_PTR)GetPropW(client,SHELL_MODE_PROPERTY);
            if(data && data->dwData==SHELL_COPYDATA && data->cbData==sizeof(SHELL_SYMBOLS) && data->lpData && valid_client(client,requested_mode))
            {
                taskbar_window=message->hwnd;
                DWORD result=install_for_shell(client,requested_mode,(const SHELL_SYMBOLS*)data->lpData);
                SetPropW(client,SHELL_STATUS_PROPERTY,(HANDLE)(UINT_PTR)(result+1));
            }
        }
        if(message->message==RegisterWindowMessageW(SHELL_CONTROL) && message->wParam==2 && owner==(HWND)message->lParam)
            stop_server();
    }
    return CallNextHookEx(NULL,code,wparam,lparam);
}
__declspec(dllexport) DWORD WINAPI DiTunnelShellAttach(HWND client,int requested_mode,const SHELL_SYMBOLS* symbols)
{
    DWORD pid=0,current_pid=0;HWND shell=FindWindowW(L"Shell_TrayWnd",NULL);
    DWORD thread=GetWindowThreadProcessId(shell,&pid);
    GetWindowThreadProcessId(client,&current_pid);
    if(!thread || !pid || current_pid!=GetCurrentProcessId() || !symbols || symbols->version!=SHELL_PROTOCOL ||
        !valid_client(client,requested_mode))return ERROR_INVALID_PARAMETER;
    HANDLE process=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,pid);
    wchar_t path[MAX_PATH],expected[MAX_PATH];DWORD length=MAX_PATH;
    if(!process)return GetLastError();
    BOOL found=QueryFullProcessImageNameW(process,0,path,&length);DWORD error=GetLastError();CloseHandle(process);
    if(!found)return error;
    GetWindowsDirectoryW(expected,MAX_PATH);wcscat_s(expected,MAX_PATH,L"\\explorer.exe");
    if(_wcsicmp(path,expected)!=0)return ERROR_ACCESS_DENIED;
    if(client_hook)return client_window==client?0:ERROR_ALREADY_EXISTS;
    if(!SetPropW(client,SHELL_MODE_PROPERTY,(HANDLE)(INT_PTR)requested_mode))return GetLastError();
    RemovePropW(client,SHELL_STATUS_PROPERTY);
    client_hook=SetWindowsHookExW(WH_CALLWNDPROC,DiTunnelShellHook,self,thread);client_window=client;
    if(!client_hook){RemovePropW(client,SHELL_MODE_PROPERTY);return GetLastError();}
    SHELL_SYMBOLS copy=*symbols;COPYDATASTRUCT data={SHELL_COPYDATA,sizeof(copy),&copy};DWORD_PTR ignored;
    if(!SendMessageTimeoutW(shell,WM_COPYDATA,(WPARAM)client,(LPARAM)&data,SMTO_ABORTIFHUNG|SMTO_BLOCK,3000,&ignored))error=ERROR_TIMEOUT;
    else {INT_PTR status=(INT_PTR)GetPropW(client,SHELL_STATUS_PROPERTY);error=status?((DWORD)status-1):ERROR_NOT_READY;}
    if(error){UnhookWindowsHookEx(client_hook);client_hook=NULL;client_window=NULL;RemovePropW(client,SHELL_MODE_PROPERTY);RemovePropW(client,SHELL_STATUS_PROPERTY);}
    return error;
}
__declspec(dllexport) DWORD WINAPI DiTunnelShellDetach(HWND client)
{
    if(!client_hook || client_window!=client)return 0;
    HWND shell=FindWindowW(L"Shell_TrayWnd",NULL);DWORD_PTR ignored;
    SendMessageTimeoutW(shell,RegisterWindowMessageW(SHELL_CONTROL),2,(LPARAM)client,SMTO_ABORTIFHUNG|SMTO_BLOCK,1000,&ignored);
    BOOL removed=UnhookWindowsHookEx(client_hook);DWORD result=removed?0:GetLastError();client_hook=NULL;client_window=NULL;
    RemovePropW(client,SHELL_MODE_PROPERTY);RemovePropW(client,SHELL_STATUS_PROPERTY);
    return result;
}

#ifdef DITUNNEL_SHELL_TESTS
#include "self_test_hooks.h"
#endif
BOOL WINAPI DllMain(HINSTANCE module,DWORD reason,LPVOID reserved)
{
    (void)reserved;if(reason==DLL_PROCESS_ATTACH)self=module;
    return TRUE;
}
