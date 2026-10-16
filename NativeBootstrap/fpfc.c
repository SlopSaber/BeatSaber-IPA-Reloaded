/* BSIPA's process-local desktop startup extension to Unity Doorstop 4.5.0.
 * Distributed with Doorstop under LGPL-2.1. See the accompanying source archive. */
#include "fpfc.h"
#include "../config/config.h"
#include "../crt.h"
#include <shellapi.h>

static wchar_t *desktop_boot;
static BOOL command_hook, boot_hook;

static wchar_t *join(wchar_t const *root, wchar_t const *tail) {
    wchar_t *path = calloc(strlen(root) + strlen(tail) + 2, sizeof(wchar_t));
    if (path) { strcpy(path, root); strcat(path, L"\\"); strcat(path, tail); }
    return path;
}

static char *read_file(wchar_t const *path, DWORD *length) {
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (file == INVALID_HANDLE_VALUE) return NULL;
    DWORD size = GetFileSize(file, NULL), count = 0;
    char *data = size <= 1024 * 1024 ? calloc((size_t)size + 1, 1) : NULL;
    if (data && (!ReadFile(file, data, size, &count, NULL) || count != size)) { free(data); data = NULL; }
    CloseHandle(file);
    if (data) *length = size;
    return data;
}

static BOOL space(char value) { return value == ' ' || value == '\r' || value == '\n' || value == '\t'; }
static int hex(char value) {
    if (value >= '0' && value <= '9') return value - '0';
    if (value >= 'a' && value <= 'f') return value - 'a' + 10;
    if (value >= 'A' && value <= 'F') return value - 'A' + 10;
    return -1;
}

/* Read only the two top-level Boolean settings used by FPFC. Skip strings,
 * escaped keys, nested values and JSON comments without matching their contents. */
static BOOL configured_fpfc(wchar_t const *directory) {
    wchar_t *path = join(directory, L"UserData\\Beat Saber IPA.json");
    DWORD size = 0;
    char *json = path ? read_file(path, &size) : NULL;
    free(path);
    if (!json) return FALSE;
    BOOL enabled = FALSE, regenerate = TRUE;
    int depth = 0;
    for (DWORD i = 0; i < size; i++) {
        char c = json[i];
        if (c == '/' && i + 1 < size && json[i + 1] == '/') {
            while (i < size && json[i] != '\n') i++;
            continue;
        }
        if (c == '/' && i + 1 < size && json[i + 1] == '*') {
            i += 2;
            while (i + 1 < size && !(json[i] == '*' && json[i + 1] == '/')) i++;
            i++;
            continue;
        }
        if (c == '{' || c == '[') depth++;
        else if (c == '}' || c == ']') depth--;
        else if (c == '"') {
            char key[32]; unsigned int count = 0;
            while (++i < size && json[i] != '"') {
                unsigned int value = (unsigned char)json[i];
                if (value == '\\') {
                    if (++i >= size) break;
                    value = (unsigned char)json[i];
                    if (value == 'u' && i + 4 < size) {
                        value = 0;
                        for (int digit = 0; digit < 4; digit++) {
                            int part = hex(json[++i]);
                            value = part < 0 ? 0x10000 : (value << 4) | part;
                        }
                    }
                }
                if (count < sizeof(key) - 1) key[count++] = value < 128 ? (char)value : '?';
            }
            key[count] = 0;
            if (depth != 1 || i >= size) continue;
            DWORD cursor = i + 1;
            while (cursor < size && space(json[cursor])) cursor++;
            if (cursor >= size || json[cursor++] != ':') continue;
            while (cursor < size && space(json[cursor])) cursor++;
            BOOL is_true = cursor + 4 <= size && json[cursor] == 't' && json[cursor + 1] == 'r' && json[cursor + 2] == 'u' && json[cursor + 3] == 'e';
            BOOL is_false = cursor + 5 <= size && json[cursor] == 'f' && json[cursor + 1] == 'a' && json[cursor + 2] == 'l' && json[cursor + 3] == 's' && json[cursor + 4] == 'e';
            if (!is_true && !is_false) continue;
            if (lstrcmpA(key, "EnableFPFC") == 0) enabled = is_true;
            if (lstrcmpA(key, "Regenerate") == 0) regenerate = is_true;
        }
    }
    free(json);
    return enabled && !regenerate;
}

static BOOL write_bytes(HANDLE file, char const *bytes, DWORD count) {
    while (count) {
        DWORD written = 0;
        if (!WriteFile(file, bytes, count, &written, NULL) || !written) return FALSE;
        bytes += written; count -= written;
    }
    return TRUE;
}

static BOOL vr_key(char const *line, DWORD length, char const *key) {
    DWORD index = 0;
    while (index < length && space(line[index])) index++;
    while (*key && index < length && line[index] == *key) { index++; key++; }
    while (index < length && space(line[index])) index++;
    return !*key && index < length && line[index] == '=';
}

static BOOL prepare_boot(DoorstopPaths const *paths) {
    wchar_t *stem = get_file_name(paths->app_path, FALSE);
    wchar_t *relative = stem ? calloc(strlen(stem) + 24, sizeof(wchar_t)) : NULL;
    if (!relative) { free(stem); return FALSE; }
    strcpy(relative, stem); strcat(relative, L"_Data\\boot.config"); free(stem);
    wchar_t *source = config.boot_config_override ? config.boot_config_override : join(paths->app_dir, relative);
    free(relative);
    DWORD size = 0;
    char *data = source ? read_file(source, &size) : NULL;
    if (source != config.boot_config_override) free(source);
    if (!data) return FALSE;
    wchar_t *directory = join(paths->app_dir, L"IPA\\Boot");
    wchar_t name[48];
    wsprintfW(name, L"fpfc-%lu.config", GetCurrentProcessId());
    desktop_boot = directory ? join(directory, name) : NULL;
    if (!directory || !desktop_boot) { free(directory); free(data); return FALSE; }
    CreateDirectoryW(directory, NULL); free(directory);
    HANDLE output = CreateFileW(desktop_boot, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, 0, NULL);
    BOOL ok = output != INVALID_HANDLE_VALUE;
    DWORD offset = 0;
    if (ok && size >= 3 && (unsigned char)data[0] == 0xef && (unsigned char)data[1] == 0xbb && (unsigned char)data[2] == 0xbf) {
        ok = write_bytes(output, data, 3);
        offset = 3;
    }
    for (DWORD start = offset; ok && start < size;) {
        DWORD end = start;
        while (end < size && data[end] != '\n') end++;
        if (end < size) end++;
        if (!vr_key(data + start, end - start, "vr-enabled") && !vr_key(data + start, end - start, "vr-device-list")
            && !vr_key(data + start, end - start, "xrsdk-pre-init-library")) ok = write_bytes(output, data + start, end - start);
        start = end;
    }
    static char const disabled[] = "\nvr-enabled=0\nvr-device-list=None\n";
    if (ok) ok = write_bytes(output, disabled, sizeof(disabled) - 1) && FlushFileBuffers(output);
    if (output != INVALID_HANDLE_VALUE) CloseHandle(output);
    free(data);
    if (!ok) { bsipa_cleanup_fpfc(); return FALSE; }
    config.boot_config_override = desktop_boot;
    return TRUE;
}

/* Quote each argument using the Windows backslash/quote rules. Existing
 * -vrmode values are removed so Unity cannot choose a conflicting first value. */
static wchar_t *quote(wchar_t *out, wchar_t const *value) {
    *out++ = '"';
    while (*value) {
        unsigned int slashes = 0;
        while (*value == '\\') { slashes++; value++; }
        if (*value == '"' || !*value) slashes *= 2;
        while (slashes--) *out++ = '\\';
        if (!*value) break;
        if (*value == '"') *out++ = '\\';
        *out++ = *value++;
    }
    *out++ = '"'; *out++ = ' '; return out;
}

void bsipa_configure_fpfc(DoorstopPaths const *paths, char_t **command, char **narrow_command) {
    SetEnvironmentVariableW(L"BSIPA_FPFC_NO_VR", NULL); // Do not inherit another game's mode.
    SetEnvironmentVariableW(L"BSIPA_FPFC_VR_ERROR", NULL);
    if (!config.enabled) return;
    int argc = 0;
    wchar_t **argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv) return;
    BOOL requested = FALSE, disabled = FALSE;
    for (int i = 1; i < argc; i++) {
        if (!lstrcmpW(argv[i], L"fpfc") || !lstrcmpW(argv[i], L"-fpfc") || !lstrcmpW(argv[i], L"--fpfc")) requested = TRUE;
        if (!lstrcmpW(argv[i], L"--no-fpfc")) disabled = TRUE;
    }
    if (disabled || (!requested && !configured_fpfc(paths->app_dir))) { LocalFree(argv); return; }
    wchar_t const *original = *command ? *command : GetCommandLineW();
    // Parse Doorstop's output-log-adjusted command line too, preserving those options.
    LocalFree(argv);
    argv = CommandLineToArgvW(original, &argc);
    wchar_t *result = argv ? calloc(strlen(original) * 2 + 128, sizeof(wchar_t)) : NULL;
    if (!result || !prepare_boot(paths)) {
        SetEnvironmentVariableW(L"BSIPA_FPFC_VR_ERROR", L"Native FPFC boot setup failed; check boot.config and write access to IPA/Boot.");
        free(result); if (argv) LocalFree(argv); return;
    }
    wchar_t *out = result;
    for (int i = 0; i < argc; i++) {
        if (i > 0 && !lstrcmpiW(argv[i], L"-vrmode")) { if (i + 1 < argc) i++; continue; }
        out = quote(out, argv[i]);
    }
    out = quote(out, L"-vrmode"); out = quote(out, L"None"); *out = 0;
    LocalFree(argv);
    *command = result;
    *narrow_command = narrow(result);
    SetEnvironmentVariableW(L"BSIPA_FPFC_NO_VR", L"1");
}

void bsipa_record_hook(char const *name, bool_t installed) {
    if (!installed) return;
    if (!lstrcmpA(name, "GetCommandLineW") || !lstrcmpA(name, "GetCommandLineA")) command_hook = TRUE;
    if (!lstrcmpA(name, "CreateFileW") || !lstrcmpA(name, "CreateFileA")) boot_hook = TRUE;
}

void bsipa_finish_hooks(void) {
    if (desktop_boot && (!command_hook || !boot_hook)) {
        SetEnvironmentVariableW(L"BSIPA_FPFC_NO_VR", NULL);
        SetEnvironmentVariableW(L"BSIPA_FPFC_VR_ERROR", L"Unity's command-line or boot-config hook could not be installed on this player.");
    }
}

void bsipa_cleanup_fpfc(void) {
    if (desktop_boot) { DeleteFileW(desktop_boot); free(desktop_boot); desktop_boot = NULL; }
}
