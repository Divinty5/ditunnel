// Included only in the separate test DLL. No Explorer interaction in these tests.
typedef struct {const wchar_t* id;int count;void* shortcut;} TEST_GROUP;
__declspec(noinline) static const wchar_t* WINAPI test_id(void* group){return ((TEST_GROUP*)group)->id;}
__declspec(noinline) static int WINAPI test_count(void* group){return ((TEST_GROUP*)group)->count;}
__declspec(noinline) static void* WINAPI test_shortcut(void* group){return ((TEST_GROUP*)group)->shortcut;}
__declspec(noinline) static const wchar_t* WINAPI test_resource(void* group){(void)group;return L"test.ico";}
static void* test_result;
static const wchar_t* resource_result;
__declspec(noinline) static void WINAPI test_draw(void* group,void* dc,void* info)
{(void)dc;(void)info;test_result=test_shortcut(group);resource_result=test_resource(group);}
__declspec(noinline) static HRESULT WINAPI test_get_icon(void* group,void* item,HICON* icon)
{(void)item;(void)icon;test_draw(group,0,0);return S_FALSE;}
__declspec(dllexport) DWORD WINAPI DiTunnelShellSelfTest(HWND client)
{
    TEST_GROUP mine={L"DiVintyInteractive.DiTunnel",1,(void*)11},foreign={L"Other.Application",1,(void*)22},idle={L"DiVintyInteractive.DiTunnel",0,(void*)33};
    for(int modern=0;modern<2;modern++)
    {
        owner=client;mode=1;GetWindowThreadProcessId(client,&owner_pid);SetPropW(client,SHELL_MODE_PROPERTY,(HANDLE)1);
        DWORD result=install_hooks((LPVOID)test_shortcut,modern?(LPVOID)test_get_icon:(LPVOID)test_draw,test_id,test_count,(LPVOID)test_resource,modern);
        if(result)return result;
        if(test_shortcut(&mine)!=(void*)11 || !test_resource(&mine))result=1;
        if(modern){if(test_get_icon(&mine,0,0)!=S_FALSE)result=7;}else test_draw(&mine,0,0);
        if(test_result!=NULL || resource_result!=NULL)result=2;
        if(modern)test_get_icon(&foreign,0,0);else test_draw(&foreign,0,0);
        if(test_result!=(void*)22 || !resource_result)result=3;
        if(modern)test_get_icon(&idle,0,0);else test_draw(&idle,0,0);
        if(test_result!=(void*)33 || !resource_result)result=4;
        RemovePropW(client,SHELL_MODE_PROPERTY);
        if(modern)test_get_icon(&mine,0,0);else test_draw(&mine,0,0);
        if(test_result!=(void*)11 || !resource_result)result=8;
        if(!remove_hooks())return 9;owner=NULL;owner_pid=0;
        test_draw(&mine,0,0);if(test_result!=(void*)11)result=5;
        if(installed || active || drawing)result=6;
        if(result)return result;
    }
    // A later CreateHook failure must also remove an earlier successful hook.
    DWORD partial=install_hooks((LPVOID)test_shortcut,(LPVOID)test_draw,test_id,test_count,(LPVOID)1,FALSE);
    if(!partial || !remove_hooks() || test_shortcut(&mine)!=(void*)11)return 15;
    const IMAGE_DOS_HEADER* dos=(const IMAGE_DOS_HEADER*)self;
    const IMAGE_NT_HEADERS* nt=(const IMAGE_NT_HEADERS*)((BYTE*)self+dos->e_lfanew);
    DWORD rva=(DWORD)((BYTE*)test_draw-(BYTE*)self);
    SHELL_SYMBOLS symbols={1,1,nt->FileHeader.TimeDateStamp,nt->OptionalHeader.SizeOfImage,rva,rva,rva,rva,rva};
    if(!validate_symbols(self,&symbols))return 10;
    symbols.timestamp++;if(validate_symbols(self,&symbols))return 11;symbols.timestamp--;
    symbols.draw=0;if(validate_symbols(self,&symbols))return 12;
    symbols.draw=nt->OptionalHeader.SizeOfImage;if(validate_symbols(self,&symbols))return 13;
    symbols.draw=dos->e_lfanew;if(validate_symbols(self,&symbols))return 14;
    return 0;
}
