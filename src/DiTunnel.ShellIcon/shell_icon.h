#pragma once
#include <windows.h>

// Fixed-width, pointer-free IPC payload. RVAs are validated against the loaded PE.
typedef struct {
    DWORD version, module_kind, timestamp, image_size;
    DWORD app_id, item_count, shortcut, resource, draw;
} SHELL_SYMBOLS;
#define SHELL_PROTOCOL 1
#define SHELL_COPYDATA 0x44544943
#define SHELL_MODE_PROPERTY L"DiTunnel.ShellIcon.Mode.v1"
#define SHELL_STATUS_PROPERTY L"DiTunnel.ShellIcon.Status.v1"
#define SHELL_CONTROL L"DiTunnel.ShellIcon.Control.v1"
