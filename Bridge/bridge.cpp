// switcher.dll — version-adaptive Minecraft session bridge for Lunar Cookies
// Hardened for LoadLibrary injection: no iostream in DllMain, deferred JVMTI,
// Win32-only logging, -fno-exceptions friendly.
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <jni.h>
#include <jvmti.h>
#include <string>
#include <vector>
#include <cstring>
#include <cstdio>
#include <algorithm>

#include "session_helper_bytes.inc"

static JavaVM* g_jvm = nullptr;
static volatile LONG g_running = 1;
static SOCKET g_serverSocket = INVALID_SOCKET;
static HMODULE g_selfModule = nullptr;
static char g_logPath[MAX_PATH] = "lunar_cookies_bridge.log";

static jclass g_helperClass = nullptr;
static jmethodID g_initMethod = nullptr;
static jmethodID g_setSessionMethod = nullptr;
static jmethodID g_restoreMethod = nullptr;
static jmethodID g_getInfoMethod = nullptr;
static jmethodID g_joinServerMethod = nullptr;
static jmethodID g_hintMethod = nullptr;
static jmethodID g_hintConnectMethod = nullptr;
static jobject g_gameClassLoader = nullptr;
static CRITICAL_SECTION g_logCs;
static bool g_logCsInit = false;

static void InitLogPath(HMODULE hModule) {
    char dllPath[MAX_PATH] = {};
    if (hModule && GetModuleFileNameA(hModule, dllPath, MAX_PATH)) {
        char* slash = strrchr(dllPath, '\\');
        if (!slash) slash = strrchr(dllPath, '/');
        if (slash) {
            *(slash + 1) = '\0';
            snprintf(g_logPath, MAX_PATH, "%slunar_cookies_bridge.log", dllPath);
            return;
        }
    }
    char tmp[MAX_PATH] = {};
    DWORD n = GetTempPathA(MAX_PATH, tmp);
    if (n > 0 && n < MAX_PATH)
        snprintf(g_logPath, MAX_PATH, "%slunar_cookies_bridge.log", tmp);
}

static void Log(const char* msg) {
    if (!msg) return;
    if (g_logCsInit) EnterCriticalSection(&g_logCs);
    HANDLE h = CreateFileA(g_logPath, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                           nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h != INVALID_HANDLE_VALUE) {
        SYSTEMTIME st; GetLocalTime(&st);
        char line[2048];
        int len = snprintf(line, sizeof(line), "[%02d:%02d:%02d] %s\r\n",
                           st.wHour, st.wMinute, st.wSecond, msg);
        if (len > 0) {
            DWORD written = 0;
            WriteFile(h, line, (DWORD)len, &written, nullptr);
        }
        CloseHandle(h);
    }
    OutputDebugStringA("[LunarCookies] ");
    OutputDebugStringA(msg);
    OutputDebugStringA("\n");
    if (g_logCsInit) LeaveCriticalSection(&g_logCs);
}

static void LogStr(const std::string& s) { Log(s.c_str()); }

static JNIEnv* AttachEnv() {
    if (!g_jvm) return nullptr;
    JNIEnv* env = nullptr;
    jint rc = g_jvm->GetEnv((void**)&env, JNI_VERSION_1_8);
    if (rc == JNI_EDETACHED) {
        JavaVMAttachArgs args;
        args.version = JNI_VERSION_1_8;
        args.name = (char*)"LunarCookiesBridge";
        args.group = nullptr;
        if (g_jvm->AttachCurrentThread((void**)&env, &args) != JNI_OK)
            return nullptr;
    } else if (rc != JNI_OK) {
        return nullptr;
    }
    return env;
}

static std::string JStringToUtf8(JNIEnv* env, jstring js) {
    if (!js) return "";
    const char* c = env->GetStringUTFChars(js, nullptr);
    if (!c) return "";
    std::string s(c);
    env->ReleaseStringUTFChars(js, c);
    return s;
}

static jclass LoadClassWithLoader(JNIEnv* env, jobject cl, const char* dotName) {
    if (!env || !dotName) return nullptr;
    if (!cl) {
        jclass c = env->FindClass(dotName);
        if (env->ExceptionCheck()) { env->ExceptionClear(); return nullptr; }
        return c;
    }
    jclass clClass = env->FindClass("java/lang/ClassLoader");
    if (!clClass || env->ExceptionCheck()) { env->ExceptionClear(); return nullptr; }
    jmethodID loadClass = env->GetMethodID(clClass, "loadClass", "(Ljava/lang/String;)Ljava/lang/Class;");
    env->DeleteLocalRef(clClass);
    if (!loadClass || env->ExceptionCheck()) { env->ExceptionClear(); return nullptr; }

    std::string name(dotName);
    for (char& ch : name) if (ch == '/') ch = '.';
    jstring jname = env->NewStringUTF(name.c_str());
    if (!jname) return nullptr;
    jclass result = (jclass)env->CallObjectMethod(cl, loadClass, jname);
    env->DeleteLocalRef(jname);
    if (env->ExceptionCheck()) { env->ExceptionClear(); return nullptr; }
    return result;
}

static bool DiscoverGameClassLoader(JNIEnv* env) {
    if (g_gameClassLoader) return true;
    if (!g_jvm || !env) return false;

    jvmtiEnv* jvmti = nullptr;
    if (g_jvm->GetEnv((void**)&jvmti, JVMTI_VERSION_1_2) != JNI_OK || !jvmti) {
        Log("JVMTI unavailable");
        return false;
    }

    jint count = 0;
    jclass* classes = nullptr;
    jvmtiError err = jvmti->GetLoadedClasses(&count, &classes);
    if (err != JVMTI_ERROR_NONE || !classes) {
        char buf[80];
        snprintf(buf, sizeof(buf), "GetLoadedClasses failed err=%d", (int)err);
        Log(buf);
        return false;
    }

    jobject foundLoader = nullptr;
    for (jint i = 0; i < count; i++) {
        char* sig = nullptr;
        if (jvmti->GetClassSignature(classes[i], &sig, nullptr) != JVMTI_ERROR_NONE) {
            env->DeleteLocalRef(classes[i]);
            continue;
        }
        bool interesting = false;
        if (sig) {
            if (strstr(sig, "net/minecraft/client/Minecraft")
                || strcmp(sig, "Lave;") == 0
                || strstr(sig, "net/minecraft/class_310")
                || strstr(sig, "com/moonsworth/lunar")
                || strstr(sig, "net/minecraft/util/Session")
                || strstr(sig, "net/minecraft/client/session/Session")
                || strstr(sig, "net/minecraft/client/User")
                || strstr(sig, "net/minecraft/class_320")
                || strstr(sig, "com/mojang/authlib")) {
                interesting = true;
            }
        }
        if (interesting && !foundLoader) {
            jobject loader = nullptr;
            if (jvmti->GetClassLoader(classes[i], &loader) == JVMTI_ERROR_NONE && loader) {
                foundLoader = env->NewGlobalRef(loader);
                env->DeleteLocalRef(loader);
                char buf[256];
                snprintf(buf, sizeof(buf), "Found game classloader via %s", sig ? sig : "?");
                Log(buf);
            }
        }
        if (sig) jvmti->Deallocate((unsigned char*)sig);
        env->DeleteLocalRef(classes[i]);
    }
    jvmti->Deallocate((unsigned char*)classes);

    if (!foundLoader) {
        Log("Game classloader not found yet");
        return false;
    }
    g_gameClassLoader = foundLoader;
    return true;
}

static void DiscoverClassHints(JNIEnv* env, jvmtiEnv* jvmti) {
    if (!jvmti || !g_helperClass || !g_hintMethod) return;

    jint count = 0;
    jclass* classes = nullptr;
    if (jvmti->GetLoadedClasses(&count, &classes) != JVMTI_ERROR_NONE) return;

    std::string mcDot, sessionDot, connectDot, addressDot, dataDot;
    for (jint i = 0; i < count; i++) {
        char* sig = nullptr;
        if (jvmti->GetClassSignature(classes[i], &sig, nullptr) != JVMTI_ERROR_NONE) {
            env->DeleteLocalRef(classes[i]);
            continue;
        }
        if (sig) {
            if (strcmp(sig, "Lnet/minecraft/client/Minecraft;") == 0
                || strcmp(sig, "Lnet/minecraft/client/MinecraftClient;") == 0
                || strcmp(sig, "Lnet/minecraft/class_310;") == 0
                || strcmp(sig, "Lave;") == 0) {
                std::string s(sig + 1);
                if (!s.empty() && s.back() == ';') s.pop_back();
                for (char& ch : s) if (ch == '/') ch = '.';
                mcDot = s;
            }
            if (strcmp(sig, "Lnet/minecraft/util/Session;") == 0
                || strcmp(sig, "Lnet/minecraft/client/session/Session;") == 0
                || strcmp(sig, "Lnet/minecraft/client/User;") == 0
                || strcmp(sig, "Lnet/minecraft/class_320;") == 0
                || strcmp(sig, "Lbhl;") == 0
                || strcmp(sig, "Lbhm;") == 0) {
                std::string s(sig + 1);
                if (!s.empty() && s.back() == ';') s.pop_back();
                for (char& ch : s) if (ch == '/') ch = '.';
                sessionDot = s;
            }
            if (strstr(sig, "ConnectScreen;")
                || strstr(sig, "GuiConnecting;")
                || strcmp(sig, "Lnet/minecraft/class_412;") == 0
                || strcmp(sig, "Lawz;") == 0
                || strcmp(sig, "Laxk;") == 0) {
                std::string s(sig + 1);
                if (!s.empty() && s.back() == ';') s.pop_back();
                for (char& ch : s) if (ch == '/') ch = '.';
                connectDot = s;
            }
            if (strstr(sig, "multiplayer/resolver/ServerAddress;")
                || strstr(sig, "multiplayer/ServerAddress;")
                || strcmp(sig, "Lnet/minecraft/class_639;") == 0) {
                std::string s(sig + 1);
                if (!s.empty() && s.back() == ';') s.pop_back();
                for (char& ch : s) if (ch == '/') ch = '.';
                addressDot = s;
            }
            if (strstr(sig, "multiplayer/ServerData;")
                || strstr(sig, "network/ServerInfo;")
                || strcmp(sig, "Lnet/minecraft/class_642;") == 0) {
                std::string s(sig + 1);
                if (!s.empty() && s.back() == ';') s.pop_back();
                for (char& ch : s) if (ch == '/') ch = '.';
                dataDot = s;
            }
        }
        if (sig) jvmti->Deallocate((unsigned char*)sig);
        env->DeleteLocalRef(classes[i]);
    }
    jvmti->Deallocate((unsigned char*)classes);

    if (!mcDot.empty() || !sessionDot.empty()) {
        jstring jmc = env->NewStringUTF(mcDot.c_str());
        jstring jss = env->NewStringUTF(sessionDot.c_str());
        env->CallStaticVoidMethod(g_helperClass, g_hintMethod, jmc, jss);
        if (env->ExceptionCheck()) env->ExceptionClear();
        if (jmc) env->DeleteLocalRef(jmc);
        if (jss) env->DeleteLocalRef(jss);
        LogStr("Hints mc=" + mcDot + " session=" + sessionDot);
    }
    if (g_hintConnectMethod && (!connectDot.empty() || !addressDot.empty() || !dataDot.empty())) {
        jstring jc = env->NewStringUTF(connectDot.c_str());
        jstring ja = env->NewStringUTF(addressDot.c_str());
        jstring jd = env->NewStringUTF(dataDot.c_str());
        env->CallStaticVoidMethod(g_helperClass, g_hintConnectMethod, jc, ja, jd);
        if (env->ExceptionCheck()) env->ExceptionClear();
        if (jc) env->DeleteLocalRef(jc);
        if (ja) env->DeleteLocalRef(ja);
        if (jd) env->DeleteLocalRef(jd);
        LogStr("Hints connect=" + connectDot + " address=" + addressDot + " data=" + dataDot);
    }
}

static bool DefineHelper(JNIEnv* env) {
    if (g_helperClass) return true;
    if (!env) return false;
    if (!DiscoverGameClassLoader(env)) return false;

    jclass clClass = env->FindClass("java/lang/ClassLoader");
    if (!clClass || env->ExceptionCheck()) { env->ExceptionClear(); return false; }

    jmethodID defineClass = env->GetMethodID(clClass, "defineClass",
        "(Ljava/lang/String;[BII)Ljava/lang/Class;");
    if (env->ExceptionCheck()) { env->ExceptionClear(); defineClass = nullptr; }
    env->DeleteLocalRef(clClass);
    if (!defineClass) return false;

    jbyteArray ba = env->NewByteArray((jint)kSessionSwitcherClassLen);
    if (!ba) { env->ExceptionClear(); return false; }
    env->SetByteArrayRegion(ba, 0, (jint)kSessionSwitcherClassLen,
                            reinterpret_cast<const jbyte*>(kSessionSwitcherClassBytes));

    jstring jname = env->NewStringUTF("com.lunarcookies.SessionSwitcher");
    jclass defined = (jclass)env->CallObjectMethod(
        g_gameClassLoader, defineClass, jname, ba, (jint)0, (jint)kSessionSwitcherClassLen);
    if (env->ExceptionCheck()) {
        env->ExceptionClear(); // never ExceptionDescribe in injected process
        defined = nullptr;
    }
    env->DeleteLocalRef(jname);
    env->DeleteLocalRef(ba);

    if (!defined)
        defined = LoadClassWithLoader(env, g_gameClassLoader, "com.lunarcookies.SessionSwitcher");
    if (!defined) {
        Log("DefineClass(SessionSwitcher) failed");
        return false;
    }

    g_helperClass = (jclass)env->NewGlobalRef(defined);
    env->DeleteLocalRef(defined);

    g_initMethod = env->GetStaticMethodID(g_helperClass, "init", "()Ljava/lang/String;");
    g_setSessionMethod = env->GetStaticMethodID(g_helperClass, "setSession",
        "(Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;)Ljava/lang/String;");
    g_restoreMethod = env->GetStaticMethodID(g_helperClass, "restoreSession", "()Ljava/lang/String;");
    g_getInfoMethod = env->GetStaticMethodID(g_helperClass, "getSessionInfo", "()Ljava/lang/String;");
    g_joinServerMethod = env->GetStaticMethodID(g_helperClass, "joinServer",
        "(Ljava/lang/String;I)Ljava/lang/String;");
    g_hintMethod = env->GetStaticMethodID(g_helperClass, "hint",
        "(Ljava/lang/String;Ljava/lang/String;)V");
    g_hintConnectMethod = env->GetStaticMethodID(g_helperClass, "hintConnect",
        "(Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;)V");

    if (env->ExceptionCheck() || !g_initMethod || !g_setSessionMethod || !g_restoreMethod
        || !g_getInfoMethod || !g_joinServerMethod || !g_hintMethod || !g_hintConnectMethod) {
        env->ExceptionClear();
        Log("Failed to resolve SessionSwitcher methods");
        return false;
    }

    jvmtiEnv* jvmti = nullptr;
    if (g_jvm->GetEnv((void**)&jvmti, JVMTI_VERSION_1_2) == JNI_OK && jvmti)
        DiscoverClassHints(env, jvmti);

    jstring r = (jstring)env->CallStaticObjectMethod(g_helperClass, g_initMethod);
    if (env->ExceptionCheck()) {
        env->ExceptionClear();
        Log("SessionSwitcher.init threw");
    } else {
        LogStr(std::string("SessionSwitcher.init => ") + JStringToUtf8(env, r));
        if (r) env->DeleteLocalRef(r);
    }
    return true;
}

static std::string JsonEscape(const std::string& s) {
    std::string o;
    o.reserve(s.size() + 8);
    for (char c : s) {
        switch (c) {
            case '\\': o += "\\\\"; break;
            case '"': o += "\\\""; break;
            case '\n': o += "\\n"; break;
            case '\r': o += "\\r"; break;
            case '\t': o += "\\t"; break;
            default: o += c; break;
        }
    }
    return o;
}

static std::string ExtractJsonString(const std::string& json, const char* key) {
    std::string needle = std::string("\"") + key + "\"";
    size_t p = json.find(needle);
    if (p == std::string::npos) return "";
    p = json.find(':', p);
    if (p == std::string::npos) return "";
    p = json.find('"', p);
    if (p == std::string::npos) return "";
    size_t start = p + 1;
    std::string out;
    for (size_t i = start; i < json.size(); i++) {
        if (json[i] == '\\' && i + 1 < json.size()) {
            out += json[i + 1];
            i++;
            continue;
        }
        if (json[i] == '"') break;
        out += json[i];
    }
    return out;
}

static int ExtractJsonInt(const std::string& json, const char* key, int fallback) {
    std::string needle = std::string("\"") + key + "\"";
    size_t p = json.find(needle);
    if (p == std::string::npos) return fallback;
    p = json.find(':', p);
    if (p == std::string::npos) return fallback;
    p++;
    while (p < json.size() && (json[p] == ' ' || json[p] == '\t')) p++;
    int sign = 1;
    if (p < json.size() && json[p] == '-') {
        sign = -1;
        p++;
    }
    int value = 0;
    bool any = false;
    while (p < json.size() && json[p] >= '0' && json[p] <= '9') {
        any = true;
        if (value > 100000000) return fallback;
        value = value * 10 + (json[p] - '0');
        p++;
    }
    return any ? sign * value : fallback;
}

static std::string HandleCommand(JNIEnv* env, const std::string& line) {
    std::string op = ExtractJsonString(line, "op");
    if (op.empty() && line.find("ping") != std::string::npos) op = "ping";

    if (op == "ping") {
        // Do NOT force helper load on ping — keep inject stable on menus.
        char pong[160];
        snprintf(pong, sizeof(pong),
            "{\"ok\":true,\"ready\":false,\"protocol\":2,\"processId\":%lu}\n",
            (unsigned long)GetCurrentProcessId());
        return std::string(pong);
    }

    if (!DefineHelper(env)) {
        return "{\"ok\":false,\"error\":\"Session helper not ready (wait on main menu, then retry)\"}\n";
    }

    if (op == "getSession") {
        jstring r = (jstring)env->CallStaticObjectMethod(g_helperClass, g_getInfoMethod);
        if (env->ExceptionCheck()) {
            env->ExceptionClear();
            return "{\"ok\":false,\"error\":\"getSession exception\"}\n";
        }
        std::string info = JStringToUtf8(env, r);
        if (r) env->DeleteLocalRef(r);
        std::vector<std::string> parts;
        {
            size_t start = 0;
            while (start <= info.size()) {
                size_t bar = info.find('|', start);
                if (bar == std::string::npos) {
                    parts.push_back(info.substr(start));
                    break;
                }
                parts.push_back(info.substr(start, bar - start));
                start = bar + 1;
            }
        }
        if (parts.size() < 5 || parts[0] != "ok") {
            std::string err = parts.size() > 1 ? parts[1] : info;
            return "{\"ok\":false,\"error\":\"" + JsonEscape(err) + "\"}\n";
        }
        bool inWorld = parts[3] == "true";
        return "{\"ok\":true,\"username\":\"" + JsonEscape(parts[1])
            + "\",\"uuid\":\"" + JsonEscape(parts[2])
            + "\",\"inWorld\":" + (inWorld ? "true" : "false")
            + ",\"ready\":true}\n";
    }

    if (op == "setSession") {
        std::string name = ExtractJsonString(line, "name");
        std::string uuid = ExtractJsonString(line, "uuid");
        std::string token = ExtractJsonString(line, "token");
        jstring jn = env->NewStringUTF(name.c_str());
        jstring ju = env->NewStringUTF(uuid.c_str());
        jstring jt = env->NewStringUTF(token.c_str());
        jstring r = (jstring)env->CallStaticObjectMethod(g_helperClass, g_setSessionMethod, jn, ju, jt);
        if (jn) env->DeleteLocalRef(jn);
        if (ju) env->DeleteLocalRef(ju);
        if (jt) env->DeleteLocalRef(jt);
        if (env->ExceptionCheck()) {
            env->ExceptionClear();
            return "{\"ok\":false,\"error\":\"setSession exception\"}\n";
        }
        std::string result = JStringToUtf8(env, r);
        if (r) env->DeleteLocalRef(r);
        if (result.rfind("ok:", 0) == 0) {
            return "{\"ok\":true,\"username\":\"" + JsonEscape(result.substr(3))
                + "\",\"uuid\":\"" + JsonEscape(uuid) + "\",\"inWorld\":false,\"ready\":true}\n";
        }
        std::string err = result.rfind("error:", 0) == 0 ? result.substr(6) : result;
        if (err == "in_world")
            return "{\"ok\":false,\"error\":\"Disconnect from the world before switching\",\"inWorld\":true}\n";
        return "{\"ok\":false,\"error\":\"" + JsonEscape(err) + "\"}\n";
    }

    if (op == "restoreSession") {
        jstring r = (jstring)env->CallStaticObjectMethod(g_helperClass, g_restoreMethod);
        if (env->ExceptionCheck()) {
            env->ExceptionClear();
            return "{\"ok\":false,\"error\":\"restoreSession exception\"}\n";
        }
        std::string result = JStringToUtf8(env, r);
        if (r) env->DeleteLocalRef(r);
        if (result.rfind("ok:", 0) == 0) {
            return "{\"ok\":true,\"username\":\"" + JsonEscape(result.substr(3))
                + "\",\"inWorld\":false,\"ready\":true}\n";
        }
        std::string err = result.rfind("error:", 0) == 0 ? result.substr(6) : result;
        if (err == "in_world")
            return "{\"ok\":false,\"error\":\"Disconnect from the world before restoring\",\"inWorld\":true}\n";
        return "{\"ok\":false,\"error\":\"" + JsonEscape(err) + "\"}\n";
    }

    if (op == "joinServer") {
        std::string host = ExtractJsonString(line, "host");
        int port = ExtractJsonInt(line, "port", 25565);
        jstring jh = env->NewStringUTF(host.c_str());
        jstring r = (jstring)env->CallStaticObjectMethod(
            g_helperClass, g_joinServerMethod, jh, (jint)port);
        if (jh) env->DeleteLocalRef(jh);
        if (env->ExceptionCheck()) {
            env->ExceptionClear();
            return "{\"ok\":false,\"error\":\"joinServer exception\"}\n";
        }
        std::string result = JStringToUtf8(env, r);
        if (r) env->DeleteLocalRef(r);
        if (result.rfind("ok:", 0) == 0) {
            return "{\"ok\":true,\"address\":\"" + JsonEscape(result.substr(3)) + "\",\"ready\":true}\n";
        }
        std::string err = result.rfind("error:", 0) == 0 ? result.substr(6) : result;
        return "{\"ok\":false,\"error\":\"" + JsonEscape(err) + "\"}\n";
    }

    return "{\"ok\":false,\"error\":\"unknown op\"}\n";
}

static void ServerLoop() {
    WSADATA wsa;
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) {
        Log("WSAStartup failed");
        return;
    }

    g_serverSocket = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (g_serverSocket == INVALID_SOCKET) {
        Log("socket() failed");
        WSACleanup();
        return;
    }

    int opt = 1;
    setsockopt(g_serverSocket, SOL_SOCKET, SO_REUSEADDR, (const char*)&opt, sizeof(opt));

    sockaddr_in addr;
    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    addr.sin_port = htons(25591);
    if (bind(g_serverSocket, (sockaddr*)&addr, sizeof(addr)) != 0) {
        char buf[64];
        snprintf(buf, sizeof(buf), "bind 25591 failed: %d", WSAGetLastError());
        Log(buf);
        closesocket(g_serverSocket);
        g_serverSocket = INVALID_SOCKET;
        WSACleanup();
        return;
    }
    if (listen(g_serverSocket, 2) != 0) {
        Log("listen failed");
        closesocket(g_serverSocket);
        g_serverSocket = INVALID_SOCKET;
        WSACleanup();
        return;
    }
    Log("TCP listening on 127.0.0.1:25591");

    while (InterlockedCompareExchange(&g_running, 1, 1) == 1) {
        SOCKET client = accept(g_serverSocket, nullptr, nullptr);
        if (client == INVALID_SOCKET) {
            if (InterlockedCompareExchange(&g_running, 1, 1) != 1) break;
            continue;
        }
        Log("Client connected");

        JNIEnv* env = AttachEnv();
        std::string buf;
        char tmp[4096];
        const size_t kMaxCommandBytes = 64 * 1024;
        while (InterlockedCompareExchange(&g_running, 1, 1) == 1) {
            int n = recv(client, tmp, sizeof(tmp), 0);
            if (n <= 0) break;
            buf.append(tmp, tmp + n);
            if (buf.size() > kMaxCommandBytes) {
                Log("Client command exceeded 64 KiB; connection closed");
                break;
            }
            size_t pos;
            while ((pos = buf.find('\n')) != std::string::npos) {
                std::string line = buf.substr(0, pos);
                buf.erase(0, pos + 1);
                if (!line.empty() && line.back() == '\r') line.pop_back();
                if (line.empty()) continue;
                std::string op = ExtractJsonString(line, "op");
                LogStr(std::string("CMD ") + (op.empty() ? "unknown" : op));
                env = AttachEnv();
                std::string resp = env ? HandleCommand(env, line)
                                       : "{\"ok\":false,\"error\":\"no JNI\"}\n";
                send(client, resp.c_str(), (int)resp.size(), 0);
            }
        }
        closesocket(client);
        Log("Client disconnected");
    }

    if (g_serverSocket != INVALID_SOCKET) {
        closesocket(g_serverSocket);
        g_serverSocket = INVALID_SOCKET;
    }
    WSACleanup();
}

static DWORD WINAPI MainThread(LPVOID) {
    // Let LoadLibraryA finish and the loader lock release before touching the JVM.
    Sleep(250);
    Log("MainThread start");

    for (int i = 0; i < 400 && InterlockedCompareExchange(&g_running, 1, 1) == 1; i++) {
        HMODULE jvmMod = GetModuleHandleA("jvm.dll");
        if (jvmMod) {
            typedef jint (JNICALL *GetCreatedJavaVMs_t)(JavaVM**, jsize, jsize*);
            auto getVMs = (GetCreatedJavaVMs_t)GetProcAddress(jvmMod, "JNI_GetCreatedJavaVMs");
            if (getVMs) {
                jsize n = 0;
                JavaVM* vm = nullptr;
                if (getVMs(&vm, 1, &n) == JNI_OK && n > 0 && vm) {
                    g_jvm = vm;
                    Log("Attached to existing JavaVM");
                    break;
                }
            }
        }
        Sleep(50);
    }
    if (!g_jvm) {
        Log("JavaVM not found");
        return 1;
    }

    ServerLoop();
    g_jvm->DetachCurrentThread();
    Log("MainThread exit");
    return 0;
}

extern "C" __declspec(dllexport) void LunarCookiesBridgeDummy() {}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_selfModule = hModule;
        DisableThreadLibraryCalls(hModule);
        InitializeCriticalSection(&g_logCs);
        g_logCsInit = true;
        InitLogPath(hModule);
        // Minimal work under loader lock — only spawn worker thread.
        HANDLE t = CreateThread(nullptr, 0, MainThread, nullptr, 0, nullptr);
        if (t) CloseHandle(t);
        else Log("CreateThread failed");
    } else if (reason == DLL_PROCESS_DETACH) {
        InterlockedExchange(&g_running, 0);
        if (g_serverSocket != INVALID_SOCKET) {
            closesocket(g_serverSocket);
            g_serverSocket = INVALID_SOCKET;
        }
        // Do not delete synchronization primitives under the loader lock. The
        // worker may be unwinding after closesocket; the OS reclaims them when
        // the process exits, and this DLL is intentionally not hot-unloaded.
    }
    return TRUE;
}
