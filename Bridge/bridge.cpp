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
static jmethodID g_patchCosmeticsMethod = nullptr;
static jmethodID g_isCosmeticsPatchedMethod = nullptr;
static jmethodID g_getCosmeticsStatusMethod = nullptr;
static jobject g_gameClassLoader = nullptr;
static std::vector<jobject> g_loaderCandidates;
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

static jvmtiEnv* GetJvmti() {
    if (!g_jvm) return nullptr;
    jvmtiEnv* jvmti = nullptr;
    if (g_jvm->GetEnv((void**)&jvmti, JVMTI_VERSION_1_2) == JNI_OK && jvmti) {
        jvmtiCapabilities caps;
        memset(&caps, 0, sizeof(caps));
        caps.can_redefine_classes = 1;
        jvmtiError err = jvmti->AddCapabilities(&caps);
        if (err != JVMTI_ERROR_NONE && err != JVMTI_ERROR_NOT_AVAILABLE) {
            char buf[80];
            snprintf(buf, sizeof(buf), "AddCapabilities(can_redefine_classes) err=%d", (int)err);
            Log(buf);
        }
        return jvmti;
    }
    return nullptr;
}

static jboolean JNICALL NativeRedefineClass(JNIEnv* env, jclass /*callerClass*/, jclass targetClass, jbyteArray classBytes) {
    if (!env || !targetClass || !classBytes) return JNI_FALSE;
    jvmtiEnv* jvmti = GetJvmti();
    if (!jvmti) {
        Log("NativeRedefineClass: jvmti unavailable");
        return JNI_FALSE;
    }

    jsize len = env->GetArrayLength(classBytes);
    if (len <= 0) return JNI_FALSE;

    jbyte* bytes = env->GetByteArrayElements(classBytes, nullptr);
    if (!bytes) return JNI_FALSE;

    jvmtiClassDefinition def;
    def.klass = targetClass;
    def.class_byte_count = (jint)len;
    def.class_bytes = (const unsigned char*)bytes;

    jvmtiError err = jvmti->RedefineClasses(1, &def);
    env->ReleaseByteArrayElements(classBytes, bytes, JNI_ABORT);

    if (err != JVMTI_ERROR_NONE) {
        char buf[128];
        snprintf(buf, sizeof(buf), "RedefineClasses failed err=%d", (int)err);
        Log(buf);
        return JNI_FALSE;
    }

    Log("RedefineClasses succeeded");
    return JNI_TRUE;
}

static const JNINativeMethod s_nativeMethods[] = {
    { (char*)"nativeRedefineClass", (char*)"(Ljava/lang/Class;[B)Z", (void*)NativeRedefineClass }
};

// Lunar (especially the ichor subsystem) uses multiple nested classloaders.
// Picking the first interesting class' loader is unreliable — a Lunar-internal
// lambda can hand back a loader whose view of net.minecraft classes lacks the
// static singleton. Instead, collect every distinct candidate loader, ordered
// so loaders of the actual Minecraft class are tried first.
static bool DiscoverClassLoaderCandidates(JNIEnv* env) {
    if (!g_loaderCandidates.empty()) return true;
    if (!g_jvm || !env) return false;

    jvmtiEnv* jvmti = GetJvmti();
    if (!jvmti) {
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
    env->EnsureLocalCapacity(count > 128 ? count : 128);

    struct Candidate { jobject loader; int tier; std::string sig; };
    std::vector<Candidate> found;

    for (jint i = 0; i < count && found.size() < 16; i++) {
        char* sig = nullptr;
        if (jvmti->GetClassSignature(classes[i], &sig, nullptr) != JVMTI_ERROR_NONE) {
            env->DeleteLocalRef(classes[i]);
            continue;
        }
        int tier = -1;
        if (sig) {
            // Tier 0: the game class itself — its loader is the best bet.
            if (strcmp(sig, "Lnet/minecraft/client/Minecraft;") == 0
                || strcmp(sig, "Lnet/minecraft/client/MinecraftClient;") == 0
                || strcmp(sig, "Lnet/minecraft/class_310;") == 0
                || strcmp(sig, "Lave;") == 0) {
                tier = 0;
            }
            // Tier 1: session/user class.
            else if (strcmp(sig, "Lnet/minecraft/util/Session;") == 0
                || strcmp(sig, "Lnet/minecraft/client/session/Session;") == 0
                || strcmp(sig, "Lnet/minecraft/client/User;") == 0
                || strcmp(sig, "Lnet/minecraft/class_320;") == 0
                || strcmp(sig, "Lbhl;") == 0
                || strcmp(sig, "Lbhm;") == 0) {
                tier = 1;
            }
            // Tier 2: anything else game-adjacent (connect screens, authlib,
            // Lunar internals) — kept as last-resort candidates only.
            else if (strstr(sig, "ConnectScreen;")
                || strstr(sig, "GuiConnecting;")
                || strstr(sig, "com/mojang/authlib")
                || strstr(sig, "com/moonsworth/lunar")) {
                tier = 2;
            }
        }

        if (tier >= 0) {
            jobject loader = nullptr;
            if (jvmti->GetClassLoader(classes[i], &loader) == JVMTI_ERROR_NONE && loader) {
                bool duplicate = false;
                for (Candidate& existing : found) {
                    if (env->IsSameObject(existing.loader, loader)) {
                        if (tier < existing.tier) existing.tier = tier;
                        duplicate = true;
                        break;
                    }
                }
                if (!duplicate) {
                    found.push_back(Candidate{ env->NewGlobalRef(loader), tier, sig });
                }
                env->DeleteLocalRef(loader);
            }
        }
        if (sig) jvmti->Deallocate((unsigned char*)sig);
        env->DeleteLocalRef(classes[i]);
    }
    jvmti->Deallocate((unsigned char*)classes);

    std::stable_sort(found.begin(), found.end(),
        [](const Candidate& a, const Candidate& b) { return a.tier < b.tier; });

    for (Candidate& c : found) {
        char buf[320];
        snprintf(buf, sizeof(buf), "Candidate classloader (tier %d): %s", c.tier, c.sig.c_str());
        Log(buf);
        g_loaderCandidates.push_back(c.loader);
    }

    if (g_loaderCandidates.empty()) {
        Log("No candidate classloaders found yet");
        return false;
    }
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

struct HelperMethods {
    jmethodID init;
    jmethodID setSession;
    jmethodID restore;
    jmethodID getInfo;
    jmethodID joinServer;
    jmethodID hint;
    jmethodID hintConnect;
    jmethodID patchCosmetics;
    jmethodID isCosmeticsPatched;
    jmethodID getCosmeticsStatus;
};

static bool CommitHelper(JNIEnv* env, jclass defined, const HelperMethods& methods, jobject loader) {
    if (g_helperClass) env->DeleteGlobalRef(g_helperClass);
    if (g_gameClassLoader) { env->DeleteGlobalRef(g_gameClassLoader); g_gameClassLoader = nullptr; }
    g_helperClass = (jclass)env->NewGlobalRef(defined);
    g_gameClassLoader = env->NewGlobalRef(loader);
    g_initMethod = methods.init;
    g_setSessionMethod = methods.setSession;
    g_restoreMethod = methods.restore;
    g_getInfoMethod = methods.getInfo;
    g_joinServerMethod = methods.joinServer;
    g_hintMethod = methods.hint;
    g_hintConnectMethod = methods.hintConnect;
    g_patchCosmeticsMethod = methods.patchCosmetics;
    g_isCosmeticsPatchedMethod = methods.isCosmeticsPatched;
    g_getCosmeticsStatusMethod = methods.getCosmeticsStatus;
    return g_helperClass != nullptr && g_gameClassLoader != nullptr;
}

// Defines the helper on one classloader and runs init() to prove the loader
// can actually see the live Minecraft singleton. Returns:
//   1 = defined, methods resolved, and init returned "ok"
//   0 = defined and methods resolved, but init not ready yet
//  -1 = define or method resolution failed on this loader
static int TryLoader(JNIEnv* env, jobject cl, std::string& initResult) {
    if (!env || !cl) return -1;

    jclass clClass = env->FindClass("java/lang/ClassLoader");
    if (!clClass || env->ExceptionCheck()) { env->ExceptionClear(); return -1; }
    jmethodID defineClass = env->GetMethodID(clClass, "defineClass",
        "(Ljava/lang/String;[BII)Ljava/lang/Class;");
    if (env->ExceptionCheck()) { env->ExceptionClear(); defineClass = nullptr; }
    env->DeleteLocalRef(clClass);
    if (!defineClass) return -1;

    jbyteArray ba = env->NewByteArray((jint)kSessionSwitcherClassLen);
    if (!ba) { env->ExceptionClear(); return -1; }
    env->SetByteArrayRegion(ba, 0, (jint)kSessionSwitcherClassLen,
                            reinterpret_cast<const jbyte*>(kSessionSwitcherClassBytes));

    jstring jname = env->NewStringUTF("com.lunarcookies.SessionSwitcher");
    jclass defined = (jclass)env->CallObjectMethod(cl, defineClass, jname, ba, (jint)0, (jint)kSessionSwitcherClassLen);
    if (env->ExceptionCheck()) {
        env->ExceptionClear(); // never ExceptionDescribe in injected process
        defined = nullptr;
    }
    env->DeleteLocalRef(jname);
    env->DeleteLocalRef(ba);

    if (!defined)
        defined = LoadClassWithLoader(env, cl, "com.lunarcookies.SessionSwitcher");
    if (!defined) return -1;

    env->RegisterNatives(defined, s_nativeMethods, (jint)(sizeof(s_nativeMethods) / sizeof(s_nativeMethods[0])));
    if (env->ExceptionCheck()) env->ExceptionClear();

    HelperMethods m;
    m.init = env->GetStaticMethodID(defined, "init", "()Ljava/lang/String;");
    m.setSession = env->GetStaticMethodID(defined, "setSession",
        "(Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;)Ljava/lang/String;");
    m.restore = env->GetStaticMethodID(defined, "restoreSession", "()Ljava/lang/String;");
    m.getInfo = env->GetStaticMethodID(defined, "getSessionInfo", "()Ljava/lang/String;");
    m.joinServer = env->GetStaticMethodID(defined, "joinServer",
        "(Ljava/lang/String;I)Ljava/lang/String;");
    m.hint = env->GetStaticMethodID(defined, "hint", "(Ljava/lang/String;Ljava/lang/String;)V");
    m.hintConnect = env->GetStaticMethodID(defined, "hintConnect",
        "(Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;)V");
    m.patchCosmetics = env->GetStaticMethodID(defined, "patchCosmetics", "()Ljava/lang/String;");
    m.isCosmeticsPatched = env->GetStaticMethodID(defined, "isCosmeticsPatched", "()Z");
    m.getCosmeticsStatus = env->GetStaticMethodID(defined, "getCosmeticsStatus", "()Ljava/lang/String;");

    bool resolved = !env->ExceptionCheck() && m.init && m.setSession && m.restore
        && m.getInfo && m.joinServer && m.hint && m.hintConnect;
    if (env->ExceptionCheck()) env->ExceptionClear();
    if (!resolved) {
        env->DeleteLocalRef(defined);
        return -1;
    }

    jstring r = (jstring)env->CallStaticObjectMethod(defined, m.init);
    if (env->ExceptionCheck()) {
        env->ExceptionClear();
        initResult = "init threw";
    } else {
        initResult = JStringToUtf8(env, r);
        if (r) env->DeleteLocalRef(r);
    }

    int status = (initResult == "ok") ? 1 : 0;
    if (status == 1) {
        if (!CommitHelper(env, defined, m, cl)) status = -1;
    }
    env->DeleteLocalRef(defined);
    return status;
}

static bool DefineHelper(JNIEnv* env) {
    if (g_helperClass) return true;
    if (!env) return false;
    env->EnsureLocalCapacity(64);
    if (!DiscoverClassLoaderCandidates(env)) return false;

    jclass clProbe = env->FindClass("java/lang/ClassLoader");
    env->DeleteLocalRef(clProbe); // availability probe only
    if (env->ExceptionCheck()) { env->ExceptionClear(); return false; }

    // First pass: only accept a loader whose init() reports full readiness.
    std::string result;
    for (size_t i = 0; i < g_loaderCandidates.size(); i++) {
        result.clear();
        int status = TryLoader(env, g_loaderCandidates[i], result);
        char buf[160];
        snprintf(buf, sizeof(buf), "Loader %zu init => %s", i,
                 result.empty() ? "<no result>" : result.c_str());
        Log(buf);
        if (status == 1) break;
        if (i + 1 == g_loaderCandidates.size()) {
            // No loader was ready yet. Fall back to the first loader that at
            // least accepted the class definition so later retries (setSession
            // re-runs init()) behave like before.
            Log("No loader ready yet; committing first definable loader as fallback");
            for (size_t j = 0; j < g_loaderCandidates.size(); j++) {
                jclass clClass = env->FindClass("java/lang/ClassLoader");
                if (!clClass || env->ExceptionCheck()) { env->ExceptionClear(); break; }
                jmethodID defineClass = env->GetMethodID(clClass, "defineClass",
                    "(Ljava/lang/String;[BII)Ljava/lang/Class;");
                if (env->ExceptionCheck()) { env->ExceptionClear(); defineClass = nullptr; }
                env->DeleteLocalRef(clClass);
                if (!defineClass) break;

                jbyteArray ba = env->NewByteArray((jint)kSessionSwitcherClassLen);
                if (!ba) { env->ExceptionClear(); break; }
                env->SetByteArrayRegion(ba, 0, (jint)kSessionSwitcherClassLen,
                                        reinterpret_cast<const jbyte*>(kSessionSwitcherClassBytes));
                jstring jname = env->NewStringUTF("com.lunarcookies.SessionSwitcher");
                jclass defined = (jclass)env->CallObjectMethod(
                    g_loaderCandidates[j], defineClass, jname, ba, (jint)0, (jint)kSessionSwitcherClassLen);
                if (env->ExceptionCheck()) { env->ExceptionClear(); defined = nullptr; }
                env->DeleteLocalRef(jname);
                env->DeleteLocalRef(ba);
                if (!defined)
                    defined = LoadClassWithLoader(env, g_loaderCandidates[j], "com.lunarcookies.SessionSwitcher");
                if (!defined) continue;

                env->RegisterNatives(defined, s_nativeMethods, (jint)(sizeof(s_nativeMethods) / sizeof(s_nativeMethods[0])));
                if (env->ExceptionCheck()) env->ExceptionClear();

                HelperMethods m;
                m.init = env->GetStaticMethodID(defined, "init", "()Ljava/lang/String;");
                m.setSession = env->GetStaticMethodID(defined, "setSession",
                    "(Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;)Ljava/lang/String;");
                m.restore = env->GetStaticMethodID(defined, "restoreSession", "()Ljava/lang/String;");
                m.getInfo = env->GetStaticMethodID(defined, "getSessionInfo", "()Ljava/lang/String;");
                m.joinServer = env->GetStaticMethodID(defined, "joinServer",
                    "(Ljava/lang/String;I)Ljava/lang/String;");
                m.hint = env->GetStaticMethodID(defined, "hint", "(Ljava/lang/String;Ljava/lang/String;)V");
                m.hintConnect = env->GetStaticMethodID(defined, "hintConnect",
                    "(Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;)V");
                m.patchCosmetics = env->GetStaticMethodID(defined, "patchCosmetics", "()Ljava/lang/String;");
                m.isCosmeticsPatched = env->GetStaticMethodID(defined, "isCosmeticsPatched", "()Z");
                m.getCosmeticsStatus = env->GetStaticMethodID(defined, "getCosmeticsStatus", "()Ljava/lang/String;");
                bool okMethods = !env->ExceptionCheck() && m.init && m.setSession && m.restore
                    && m.getInfo && m.joinServer && m.hint && m.hintConnect;
                if (env->ExceptionCheck()) env->ExceptionClear();
                if (!okMethods) { env->DeleteLocalRef(defined); continue; }

                bool committed = CommitHelper(env, defined, m, g_loaderCandidates[j]);
                env->DeleteLocalRef(defined);
                if (committed) {
                    LogStr("Fallback loader committed: index " + std::to_string(j));
                    jvmtiEnv* jvmti = nullptr;
                    if (g_jvm->GetEnv((void**)&jvmti, JVMTI_VERSION_1_2) == JNI_OK && jvmti)
                        DiscoverClassHints(env, jvmti);
                    return true;
                }
            }
            return false;
        }
    }

    if (!g_helperClass) {
        Log("DefineClass(SessionSwitcher) failed on all loaders");
        return false;
    }

    jvmtiEnv* jvmti = nullptr;
    if (g_jvm->GetEnv((void**)&jvmti, JVMTI_VERSION_1_2) == JNI_OK && jvmti)
        DiscoverClassHints(env, jvmti);
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
        bool cosmeticsPatched = (parts.size() > 5 && parts[5] == "true");
        return "{\"ok\":true,\"username\":\"" + JsonEscape(parts[1])
            + "\",\"uuid\":\"" + JsonEscape(parts[2])
            + "\",\"inWorld\":" + (inWorld ? "true" : "false")
            + ",\"ready\":true"
            + ",\"cosmeticsPatched\":" + (cosmeticsPatched ? "true" : "false")
            + "}\n";
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

    if (op == "patchCosmetics") {
        if (!g_patchCosmeticsMethod) {
            return "{\"ok\":false,\"error\":\"Cosmetics patcher method not found\"}\n";
        }
        jstring r = (jstring)env->CallStaticObjectMethod(g_helperClass, g_patchCosmeticsMethod);
        if (env->ExceptionCheck()) {
            env->ExceptionClear();
            return "{\"ok\":false,\"error\":\"patchCosmetics exception\"}\n";
        }
        std::string result = JStringToUtf8(env, r);
        if (r) env->DeleteLocalRef(r);
        if (result.rfind("ok:", 0) == 0) {
            return "{\"ok\":true,\"message\":\"" + JsonEscape(result.substr(3)) + "\",\"cosmeticsPatched\":true}\n";
        }
        std::string err = result.rfind("error:", 0) == 0 ? result.substr(6) : result;
        return "{\"ok\":false,\"error\":\"" + JsonEscape(err) + "\",\"cosmeticsPatched\":false}\n";
    }

    if (op == "getCosmeticsStatus") {
        bool patched = false;
        std::string details = "Not patched";
        if (g_isCosmeticsPatchedMethod) {
            patched = env->CallStaticBooleanMethod(g_helperClass, g_isCosmeticsPatchedMethod) == JNI_TRUE;
            if (env->ExceptionCheck()) env->ExceptionClear();
        }
        if (g_getCosmeticsStatusMethod) {
            jstring r = (jstring)env->CallStaticObjectMethod(g_helperClass, g_getCosmeticsStatusMethod);
            if (env->ExceptionCheck()) env->ExceptionClear();
            else if (r) {
                details = JStringToUtf8(env, r);
                env->DeleteLocalRef(r);
            }
        }
        return "{\"ok\":true,\"cosmeticsPatched\":" + std::string(patched ? "true" : "false")
            + ",\"details\":\"" + JsonEscape(details) + "\"}\n";
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
