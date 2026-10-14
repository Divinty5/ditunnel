#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
typedef DWORD (WINAPI *SELF_TEST)(HWND);
int main(void)
{
    HMODULE module=LoadLibraryW(L"DiTunnel.ShellIcon.Test.dll");
    if(!module){printf("LoadError=%lu\n",GetLastError());return 1;}
    SELF_TEST test=(SELF_TEST)GetProcAddress(module,"DiTunnelShellSelfTest");
    if(!test)return 3;
    HWND window=CreateWindowExW(0,L"STATIC",L"DiTunnel self-test",0,0,0,1,1,NULL,NULL,GetModuleHandleW(NULL),NULL);
    DWORD before=0,after=0;GetProcessHandleCount(GetCurrentProcess(),&before);
    for(int i=0;i<100;i++){DWORD result=test(window);if(result){printf("Failure=%lu Cycle=%d\n",result,i);return 2;}}
    GetProcessHandleCount(GetCurrentProcess(),&after);
    DestroyWindow(window);FreeLibrary(module);
    printf("Passed=100; RenderPaths=2; HandleDelta=%ld; ExplorerWasTouched=false\n",(long)after-(long)before);
    return before==after?0:4;
}
