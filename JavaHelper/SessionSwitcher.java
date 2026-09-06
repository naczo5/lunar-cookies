package com.lunarcookies;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.UUID;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

/**
 * Version-adaptive Minecraft session bridge.
 *
 * Names are only fast paths. The final resolver validates class structure,
 * constructor shape and the current field value before it permits a write.
 * This covers legacy 1.8.9 Session, 1.21 Fabric/Yarn Session and the User
 * class used by official-name 26.1/26.2 clients. Server joins use Minecraft's
 * ConnectScreen / GuiConnecting path rather than Lunar's account-gated menu.
 */
public final class SessionSwitcher {
    private static Object launchSession;
    private static Object minecraftInstance;
    private static Class<?> minecraftClass;
    private static Field sessionField;
    private static Class<?> sessionClass;
    private static Method getUsername;
    private static Method getPlayerId;
    private static Field playerField;
    private static Field worldField;
    private static Field screenField;
    private static Method setScreenMethod;
    private static Method scheduleMethod;
    private static String hintedMcClass;
    private static String hintedSessionClass;
    private static String hintedConnectClass;
    private static String hintedServerAddressClass;
    private static String hintedServerDataClass;
    private static volatile boolean ready;
    private static volatile boolean cosmeticsPatched;
    private static String cosmeticsStatusDetails = "Not patched";

    public static native boolean nativeRedefineClass(Class<?> targetClass, byte[] classBytes);

    private SessionSwitcher() {}

    /** Optional dot-name hints supplied by native JVMTI discovery. */
    public static synchronized void hint(String mcClassName, String sessionClassName) {
        if (mcClassName != null && !mcClassName.isEmpty()) hintedMcClass = mcClassName;
        if (sessionClassName != null && !sessionClassName.isEmpty()) hintedSessionClass = sessionClassName;
        ready = false;
    }

    /** Optional connect-path hints supplied by native JVMTI discovery. */
    public static synchronized void hintConnect(
            String connectClassName, String serverAddressClassName, String serverDataClassName) {
        if (connectClassName != null && !connectClassName.isEmpty()) hintedConnectClass = connectClassName;
        if (serverAddressClassName != null && !serverAddressClassName.isEmpty())
            hintedServerAddressClass = serverAddressClassName;
        if (serverDataClassName != null && !serverDataClassName.isEmpty())
            hintedServerDataClass = serverDataClassName;
    }

    public static synchronized String init() {
        try {
            if (ready) return "ok";

            Class<?> mcClass = findMinecraftClass();
            if (mcClass == null) return "Minecraft class not found";

            minecraftInstance = findMinecraftInstance(mcClass);
            if (minecraftInstance == null) return "Minecraft instance is null";
            minecraftClass = mcClass;
            screenField = findScreenField(mcClass);
            setScreenMethod = findSetScreenMethod(mcClass, screenField);

            sessionClass = findSessionClass();
            sessionField = sessionClass == null ? null : findFieldOfType(mcClass, sessionClass);
            if (sessionField == null) sessionField = findNamedSessionField(mcClass);
            if (sessionField == null) sessionField = discoverSessionField(mcClass, minecraftInstance);
            if (sessionField == null) return "validated session/user field not found on Minecraft";

            sessionField.setAccessible(true);
            sessionClass = sessionField.getType();
            if (!looksLikeSessionClass(sessionClass)) {
                return "session/user field failed structural validation";
            }

            getUsername = findValueMethod(sessionClass,
                    new String[]{"getUsername", "getName", "func_111285_a", "method_1676", "c"},
                    new String[]{"user", "name"});
            getPlayerId = findValueMethod(sessionClass,
                    new String[]{"getPlayerID", "getUuid", "getUuidOrNull", "getProfileId",
                            "func_148255_b", "method_44717", "b", "a"},
                    new String[]{"uuid", "profile", "player", "id"});

            playerField = findWorldStateField(mcClass, true);
            worldField = findWorldStateField(mcClass, false);
            scheduleMethod = findScheduleMethod(mcClass);

            Object current = sessionField.get(minecraftInstance);
            if (current == null || !sessionClass.isInstance(current)) {
                return "current session/user is null or has an unexpected type";
            }
            if (launchSession == null || !sessionClass.isInstance(launchSession)) {
                launchSession = current;
            }

            ready = true;
            try { patchCosmetics(); } catch (Throwable ignored) {}
            return "ok";
        } catch (Throwable t) {
            ready = false;
            return "init failed: " + describeThrowable(t);
        }
    }

    public static synchronized String setSession(String name, String uuid, String token) {
        String initResult = init();
        if (!ready) return "error:" + initResult;
        if (isBlank(name) || isBlank(uuid) || isBlank(token)) return "error:invalid_session_data";
        if (isInWorld()) return "error:in_world";

        try {
            final Object replacement = createSession(name, uuid, token);
            if (replacement == null) return "error:unsupported_session_constructor";
            final Object previous = sessionField.get(minecraftInstance);
            final String[] failure = new String[1];

            boolean ran = runOnGameThread(() -> {
                try {
                    // Re-check after scheduling: the user may have joined a world.
                    if (isInWorld()) {
                        failure[0] = "in_world";
                        return;
                    }
                    sessionField.set(minecraftInstance, replacement);
                    if (sessionField.get(minecraftInstance) != replacement) {
                        failure[0] = "session_write_not_observed";
                    }
                } catch (Throwable t) {
                    try { sessionField.set(minecraftInstance, previous); } catch (Throwable ignored) {}
                    failure[0] = describeThrowable(t);
                }
            });

            if (!ran) return "error:game_thread_timeout";
            if (failure[0] != null) return "error:" + failure[0];
            return "ok:" + readUsername();
        } catch (Throwable t) {
            return "error:" + describeThrowable(t);
        }
    }

    public static synchronized String restoreSession() {
        String initResult = init();
        if (!ready) return "error:" + initResult;
        if (isInWorld()) return "error:in_world";
        if (launchSession == null || !sessionClass.isInstance(launchSession)) {
            return "error:no_launch_session";
        }

        final String[] failure = new String[1];
        boolean ran = runOnGameThread(() -> {
            try {
                if (isInWorld()) {
                    failure[0] = "in_world";
                    return;
                }
                sessionField.set(minecraftInstance, launchSession);
                if (sessionField.get(minecraftInstance) != launchSession) {
                    failure[0] = "session_write_not_observed";
                }
            } catch (Throwable t) {
                failure[0] = describeThrowable(t);
            }
        });
        if (!ran) return "error:game_thread_timeout";
        if (failure[0] != null) return "error:" + failure[0];
        try {
            return "ok:" + readUsername();
        } catch (Throwable t) {
            return "error:" + describeThrowable(t);
        }
    }

    public static synchronized String joinServer(String host, int port) {
        if (isBlank(host) || host.trim().length() > 256 || port < 1 || port > 65535) {
            return "error:invalid_address";
        }
        host = host.trim();
        String initResult = init();
        if (!ready) return "error:" + initResult;

        try {
            final String connectHost = host;
            final int connectPort = port;
            final String[] failure = new String[1];
            boolean ran = runOnGameThread(() -> {
                try {
                    if (!connectNow(connectHost, connectPort)) {
                        failure[0] = "connect_path_not_found";
                    }
                } catch (Throwable t) {
                    failure[0] = describeThrowable(t);
                }
            });
            if (!ran) return "error:game_thread_timeout";
            if (failure[0] != null) return "error:" + failure[0];
            return "ok:" + host + ":" + port;
        } catch (Throwable t) {
            return "error:" + describeThrowable(t);
        }
    }

    public static synchronized String getSessionInfo() {
        String initResult = init();
        if (!ready) return "error|" + initResult + "|false|false|" + (cosmeticsPatched ? "true" : "false");
        try {
            return "ok|" + readUsername() + "|" + readUuid() + "|"
                    + isInWorld() + "|true|" + (cosmeticsPatched ? "true" : "false");
        } catch (Throwable t) {
            return "error|" + describeThrowable(t) + "|false|false|" + (cosmeticsPatched ? "true" : "false");
        }
    }

    public static synchronized boolean isReady() {
        return ready || "ok".equals(init());
    }

    private static Object findMinecraftInstance(Class<?> mcClass) throws Exception {
        Field singleton = findStaticFieldOfType(mcClass, mcClass);
        if (singleton != null) {
            singleton.setAccessible(true);
            Object result = singleton.get(null);
            if (result != null) return result;
        }

        Method getter = findMinecraftGetter(mcClass);
        if (getter == null) return null;
        getter.setAccessible(true);
        return getter.invoke(null);
    }

    private static boolean isInWorld() {
        try {
            return (playerField != null && playerField.get(minecraftInstance) != null)
                    || (worldField != null && worldField.get(minecraftInstance) != null);
        } catch (Throwable ignored) {
            // Failure to read the guard must fail closed for mutation callers.
            return true;
        }
    }

    private static boolean runOnGameThread(Runnable action) {
        if (scheduleMethod == null) {
            // Legacy/custom clients without an exposed scheduler are only
            // mutated at the main menu, where this direct write is quiescent.
            action.run();
            return true;
        }

        final CountDownLatch done = new CountDownLatch(1);
        Runnable wrapped = () -> {
            try { action.run(); }
            finally { done.countDown(); }
        };
        try {
            scheduleMethod.invoke(minecraftInstance, wrapped);
            return done.await(5, TimeUnit.SECONDS);
        } catch (Throwable t) {
            return false;
        }
    }

    private static Object createSession(String name, String uuidText, String token) throws Exception {
        UUID uuid = parseUuid(uuidText);
        Constructor<?>[] constructors = sessionClass.getDeclaredConstructors();
        Arrays.sort(constructors, Comparator.comparingInt(SessionSwitcher::constructorScore).reversed());

        for (Constructor<?> ctor : constructors) {
            Class<?>[] types = ctor.getParameterTypes();
            Object[] args = buildConstructorArgs(types, name, uuidText, uuid, token);
            if (args == null) continue;
            try {
                ctor.setAccessible(true);
                return ctor.newInstance(args);
            } catch (Throwable ignored) {
                // Try the next structurally supported shape.
            }
        }
        return null;
    }

    private static int constructorScore(Constructor<?> ctor) {
        Class<?>[] types = ctor.getParameterTypes();
        int score = types.length;
        for (Class<?> type : types) {
            if (type == UUID.class) score += 20;
            else if (type == Optional.class) score += 5;
            else if (type == String.class) score += 3;
            else if (type.isEnum()) score += 2;
            else score -= 100;
        }
        return score;
    }

    private static Object[] buildConstructorArgs(
            Class<?>[] types, String name, String uuidText, UUID uuid, String token) {
        if (types.length < 3 || types.length > 7) return null;
        boolean hasUuid = false;
        int stringCount = 0;
        for (Class<?> type : types) {
            if (type == UUID.class) hasUuid = true;
            if (type == String.class) stringCount++;
        }

        Object[] args = new Object[types.length];
        int stringIndex = 0;
        for (int i = 0; i < types.length; i++) {
            Class<?> type = types[i];
            if (type == String.class) {
                if (stringIndex == 0) args[i] = name;
                else if (!hasUuid && stringIndex == 1) args[i] = uuidText;
                else if (stringCount == 4 && stringIndex == 3) args[i] = "mojang";
                else args[i] = token;
                stringIndex++;
            } else if (type == UUID.class) {
                args[i] = uuid;
            } else if (type == Optional.class) {
                args[i] = Optional.empty();
            } else if (type.isEnum()) {
                args[i] = findAccountEnum(type);
                if (args[i] == null) return null;
            } else {
                return null;
            }
        }
        return args;
    }

    private static Object findAccountEnum(Class<?> type) {
        Object[] values = type.getEnumConstants();
        if (values == null || values.length == 0) return null;
        for (String wanted : new String[]{"MSA", "MICROSOFT", "MOJANG"}) {
            for (Object value : values) {
                if (((Enum<?>) value).name().equalsIgnoreCase(wanted)) return value;
            }
        }
        return values[0];
    }

    private static UUID parseUuid(String value) {
        String clean = value == null ? "" : value.trim();
        if (clean.length() == 32) {
            clean = clean.substring(0, 8) + "-" + clean.substring(8, 12) + "-"
                    + clean.substring(12, 16) + "-" + clean.substring(16, 20)
                    + "-" + clean.substring(20);
        }
        return UUID.fromString(clean);
    }

    private static String readUsername() throws Exception {
        Object session = sessionField.get(minecraftInstance);
        if (session == null) return "";
        Object value = getUsername == null ? null : getUsername.invoke(session);
        return value == null ? "" : value.toString();
    }

    private static String readUuid() throws Exception {
        Object session = sessionField.get(minecraftInstance);
        if (session == null) return "";
        Object value = getPlayerId == null ? null : getPlayerId.invoke(session);
        return value == null ? "" : value.toString();
    }

    private static Class<?> findMinecraftClass() {
        String[] names = {
                hintedMcClass,
                "net.minecraft.client.Minecraft",
                "net.minecraft.client.MinecraftClient",
                "net.minecraft.class_310",
                "ave"
        };
        for (String name : names) {
            Class<?> result = tryLoad(name);
            if (result != null) return result;
        }
        return null;
    }

    private static Class<?> findSessionClass() {
        String[] names = {
                hintedSessionClass,
                "net.minecraft.client.User",
                "net.minecraft.client.session.Session",
                "net.minecraft.util.Session",
                "net.minecraft.class_320",
                "bhl",
                "bhm"
        };
        for (String name : names) {
            Class<?> result = tryLoad(name);
            if (result != null && looksLikeSessionClass(result)) return result;
        }
        return null;
    }

    private static Class<?> tryLoad(String name) {
        if (name == null || name.isEmpty()) return null;
        try {
            return Class.forName(name, false, SessionSwitcher.class.getClassLoader());
        } catch (Throwable ignored) {}
        try {
            ClassLoader context = Thread.currentThread().getContextClassLoader();
            if (context != null) return Class.forName(name, false, context);
        } catch (Throwable ignored) {}
        return null;
    }

    private static Field findNamedSessionField(Class<?> owner) {
        for (String name : new String[]{"user", "session", "field_1726", "field_1690"}) {
            Field field = findField(owner, name);
            if (field != null && !Modifier.isStatic(field.getModifiers())
                    && looksLikeSessionClass(field.getType())) {
                return field;
            }
        }
        return null;
    }

    private static Field discoverSessionField(Class<?> owner, Object instance) {
        Field only = null;
        for (Class<?> current = owner; current != null && current != Object.class;
             current = current.getSuperclass()) {
            for (Field field : current.getDeclaredFields()) {
                if (Modifier.isStatic(field.getModifiers()) || !looksLikeSessionClass(field.getType())) continue;
                try {
                    field.setAccessible(true);
                    Object value = field.get(instance);
                    if (value == null || !field.getType().isInstance(value)) continue;
                } catch (Throwable ignored) {
                    continue;
                }
                // Ambiguity is unsafe. Never write to a guessed field.
                if (only != null) return null;
                only = field;
            }
        }
        return only;
    }

    private static boolean looksLikeSessionClass(Class<?> type) {
        if (type == null || type.isPrimitive() || type.isArray()
                || type.getName().startsWith("java.")) return false;

        boolean constructor = false;
        for (Constructor<?> ctor : type.getDeclaredConstructors()) {
            if (constructorScore(ctor) > 0) {
                constructor = true;
                break;
            }
        }
        if (!constructor) return false;

        Method name = findValueMethod(type,
                new String[]{"getUsername", "getName", "method_1676", "c"},
                new String[]{"user", "name"});
        Method token = findValueMethod(type,
                new String[]{"getToken", "getAccessToken", "method_1674", "d"},
                new String[]{"token"});
        return name != null && token != null;
    }

    private static Method findValueMethod(Class<?> type, String[] names, String[] hints) {
        for (String name : names) {
            Method method = findNoArgMethod(type, name);
            if (method != null && isReadableValue(method.getReturnType())) {
                try { method.setAccessible(true); } catch (Throwable ignored) {}
                return method;
            }
        }
        for (Method method : type.getDeclaredMethods()) {
            if (method.getParameterTypes().length != 0 || !isReadableValue(method.getReturnType())) continue;
            String lower = method.getName().toLowerCase();
            for (String hint : hints) {
                if (lower.contains(hint)) {
                    try { method.setAccessible(true); } catch (Throwable ignored) {}
                    return method;
                }
            }
        }
        return null;
    }

    private static boolean isReadableValue(Class<?> type) {
        return type == String.class || type == UUID.class;
    }

    private static Method findNoArgMethod(Class<?> type, String name) {
        for (Class<?> current = type; current != null && current != Object.class;
             current = current.getSuperclass()) {
            try {
                return current.getDeclaredMethod(name);
            } catch (NoSuchMethodException ignored) {}
        }
        return null;
    }

    private static Field findFieldOfType(Class<?> owner, Class<?> type) {
        Field found = null;
        for (Class<?> current = owner; current != null && current != Object.class;
             current = current.getSuperclass()) {
            for (Field field : current.getDeclaredFields()) {
                if (!Modifier.isStatic(field.getModifiers()) && field.getType() == type) {
                    if (found != null) return null;
                    found = field;
                }
            }
        }
        return found;
    }

    private static Field findStaticFieldOfType(Class<?> owner, Class<?> type) {
        for (Class<?> current = owner; current != null && current != Object.class;
             current = current.getSuperclass()) {
            for (Field field : current.getDeclaredFields()) {
                if (Modifier.isStatic(field.getModifiers()) && field.getType() == type) return field;
            }
        }
        return null;
    }

    private static Field findWorldStateField(Class<?> owner, boolean player) {
        String[] names = player
                ? new String[]{"thePlayer", "player", "field_71439_g", "field_1724"}
                : new String[]{"theWorld", "world", "level", "field_71441_e", "field_1687"};
        for (String name : names) {
            Field field = findField(owner, name);
            if (field != null && !Modifier.isStatic(field.getModifiers())) {
                try { field.setAccessible(true); } catch (Throwable ignored) {}
                return field;
            }
        }

        String[] typeHints = player
                ? new String[]{"EntityPlayerSP", "LocalPlayer", "ClientPlayer", "class_746"}
                : new String[]{"WorldClient", "ClientLevel", "ClientWorld", "class_638"};
        for (Class<?> current = owner; current != null && current != Object.class;
             current = current.getSuperclass()) {
            for (Field field : current.getDeclaredFields()) {
                String typeName = field.getType().getName();
                for (String hint : typeHints) {
                    if (typeName.contains(hint)) {
                        try { field.setAccessible(true); } catch (Throwable ignored) {}
                        return field;
                    }
                }
            }
        }
        return null;
    }

    private static Field findField(Class<?> owner, String name) {
        for (Class<?> current = owner; current != null && current != Object.class;
             current = current.getSuperclass()) {
            try {
                return current.getDeclaredField(name);
            } catch (NoSuchFieldException ignored) {}
        }
        return null;
    }

    private static Method findMinecraftGetter(Class<?> mcClass) {
        for (String name : new String[]{"getMinecraft", "getInstance", "func_71410_x", "method_1551"}) {
            Method method = findNoArgMethod(mcClass, name);
            if (method != null && Modifier.isStatic(method.getModifiers())
                    && mcClass.isAssignableFrom(method.getReturnType())) return method;
        }
        for (Method method : mcClass.getDeclaredMethods()) {
            if (Modifier.isStatic(method.getModifiers())
                    && method.getParameterTypes().length == 0
                    && mcClass.isAssignableFrom(method.getReturnType())) return method;
        }
        return null;
    }

    private static Method findScheduleMethod(Class<?> mcClass) {
        for (String name : new String[]{"execute", "addScheduledTask", "func_152344_a", "method_18859"}) {
            for (Class<?> current = mcClass; current != null && current != Object.class;
                 current = current.getSuperclass()) {
                try {
                    Method method = current.getDeclaredMethod(name, Runnable.class);
                    method.setAccessible(true);
                    return method;
                } catch (Throwable ignored) {}
            }
        }
        return null;
    }

    private static boolean connectNow(String host, int port) throws Exception {
        String combined = combinedAddress(host, port);
        Class<?>[] candidates = connectClassCandidates();
        for (int i = 0; i < candidates.length; i++) {
            if (tryModernConnect(candidates[i], host, port, combined)) return true;
        }
        for (int i = 0; i < candidates.length; i++) {
            if (tryLegacyConnect(candidates[i], host, port, combined)) return true;
        }
        return false;
    }

    private static String combinedAddress(String host, int port) {
        boolean ipv6 = host.indexOf(':') >= 0;
        String wrapped = ipv6 ? "[" + host + "]" : host;
        return wrapped + ":" + port;
    }

    private static Class<?>[] connectClassCandidates() {
        List<Class<?>> found = new ArrayList<Class<?>>();
        String[] names = {
                hintedConnectClass,
                "net.minecraft.client.gui.screens.ConnectScreen",
                "net.minecraft.client.gui.screen.ConnectScreen",
                "net.minecraft.class_412",
                "net.minecraft.client.multiplayer.GuiConnecting",
                "net.minecraft.client.gui.GuiConnecting",
                "awz",
                "axk"
        };
        for (int i = 0; i < names.length; i++) {
            Class<?> type = tryLoad(names[i]);
            if (type != null && !found.contains(type)) found.add(type);
        }
        return found.toArray(new Class<?>[0]);
    }

    private static boolean tryModernConnect(Class<?> connectClass, String host, int port, String combined) {
        if (connectClass == null || minecraftClass == null) return false;
        Method[] methods = connectClass.getDeclaredMethods();
        Arrays.sort(methods, Comparator.comparingInt((Method method) -> method.getParameterTypes().length).reversed());

        Object parent = currentScreen();
        for (int m = 0; m < methods.length; m++) {
            Method method = methods[m];
            if (!Modifier.isStatic(method.getModifiers()) || method.getReturnType() != void.class) continue;
            Class<?>[] types = method.getParameterTypes();
            if (types.length < 4 || types.length > 6) continue;
            if (!types[1].isAssignableFrom(minecraftClass)) continue;

            Object[] args = buildConnectArgs(types, parent, host, port, combined);
            if (args == null) continue;
            try {
                method.setAccessible(true);
                method.invoke(null, args);
                return true;
            } catch (Throwable ignored) {
                // Try the next structurally matching connect method.
            }
        }
        return false;
    }

    private static Object[] buildConnectArgs(
            Class<?>[] types, Object parent, String host, int port, String combined) {
        Object[] args = new Object[types.length];
        args[0] = parent != null && types[0].isInstance(parent) ? parent : null;
        args[1] = minecraftInstance;

        int objectIndex = 0;
        for (int i = 2; i < types.length; i++) {
            Class<?> type = types[i];
            if (type == boolean.class || type == Boolean.class) {
                args[i] = Boolean.FALSE;
            } else if (type == int.class || type == Integer.class) {
                args[i] = Integer.valueOf(port);
            } else if (type == String.class) {
                args[i] = objectIndex == 0 ? host : combined;
                objectIndex++;
            } else if (type.isPrimitive()) {
                return null;
            } else {
                Object created = null;
                if (objectIndex == 0) {
                    created = createServerAddress(type, host, port, combined);
                    if (created == null) created = createServerData(type, host, combined);
                } else if (objectIndex == 1) {
                    created = createServerData(type, host, combined);
                    if (created == null) created = createServerAddress(type, host, port, combined);
                }
                if (created == null && i == types.length - 1) {
                    args[i] = null;
                } else if (created == null) {
                    return null;
                } else {
                    args[i] = created;
                    objectIndex++;
                }
            }
        }
        return args;
    }

    private static boolean tryLegacyConnect(Class<?> connectingClass, String host, int port, String combined)
            throws Exception {
        if (connectingClass == null || minecraftClass == null) return false;
        Object parent = currentScreen();
        Object screen = createLegacyConnectingScreen(connectingClass, parent, host, port, combined);
        if (screen == null) return false;
        Method setter = setScreenMethod;
        if (setter == null) setter = findSetScreenMethod(minecraftClass, screenField);
        if (setter == null) setter = findSetScreenFor(minecraftClass, screen.getClass());
        if (setter == null) return false;
        setter.setAccessible(true);
        setter.invoke(minecraftInstance, new Object[]{screen});
        return true;
    }

    private static Object createLegacyConnectingScreen(
            Class<?> connectingClass, Object parent, String host, int port, String combined) {
        Constructor<?>[] constructors = connectingClass.getDeclaredConstructors();
        Arrays.sort(constructors, Comparator.comparingInt((Constructor<?> ctor) -> ctor.getParameterTypes().length).reversed());

        for (int i = 0; i < constructors.length; i++) {
            Constructor<?> ctor = constructors[i];
            Class<?>[] types = ctor.getParameterTypes();
            if (types.length < 3 || types.length > 4) continue;
            if (!types[1].isAssignableFrom(minecraftClass)) continue;
            Object[] args = new Object[types.length];
            args[0] = parent != null && types[0].isInstance(parent) ? parent : null;
            args[1] = minecraftInstance;
            boolean built = true;
            for (int p = 2; p < types.length; p++) {
                Class<?> type = types[p];
                if (type == String.class) args[p] = host;
                else if (type == int.class || type == Integer.class) args[p] = Integer.valueOf(port);
                else if (!type.isPrimitive()) {
                    Object data = createServerData(type, host, combined);
                    if (data == null) {
                        built = false;
                        break;
                    }
                    args[p] = data;
                } else {
                    built = false;
                    break;
                }
            }
            if (!built) continue;
            try {
                ctor.setAccessible(true);
                return ctor.newInstance(args);
            } catch (Throwable ignored) {
                // Try the next structurally matching constructor.
            }
        }
        return null;
    }

    private static Object createServerAddress(Class<?> type, String host, int port, String combined) {
        Object parsed = invokeStaticParser(type, combined);
        if (parsed != null) return parsed;
        parsed = invokeStaticParser(type, host);
        if (parsed != null) return parsed;

        Constructor<?>[] constructors = type.getDeclaredConstructors();
        for (int i = 0; i < constructors.length; i++) {
            Class<?>[] types = constructors[i].getParameterTypes();
            try {
                constructors[i].setAccessible(true);
                if (types.length == 2 && types[0] == String.class
                        && (types[1] == int.class || types[1] == Integer.class)) {
                    return constructors[i].newInstance(new Object[]{host, Integer.valueOf(port)});
                }
                if (types.length == 1 && types[0] == String.class) {
                    Object created = constructors[i].newInstance(new Object[]{combined});
                    if (created != null) return created;
                }
            } catch (Throwable ignored) {}
        }
        return null;
    }

    private static Object invokeStaticParser(Class<?> type, String value) {
        String[] names = {"parseString", "parse", "method_29545", "a"};
        for (int i = 0; i < names.length; i++) {
            try {
                Method method = type.getDeclaredMethod(names[i], String.class);
                if (!Modifier.isStatic(method.getModifiers())) continue;
                if (!type.isAssignableFrom(method.getReturnType()) && method.getReturnType() != type) continue;
                method.setAccessible(true);
                Object parsed = method.invoke(null, new Object[]{value});
                if (parsed != null) return parsed;
            } catch (Throwable ignored) {}
        }
        Method[] methods = type.getDeclaredMethods();
        for (int i = 0; i < methods.length; i++) {
            Method method = methods[i];
            if (!Modifier.isStatic(method.getModifiers()) || method.getParameterTypes().length != 1) continue;
            if (method.getParameterTypes()[0] != String.class) continue;
            if (!type.isAssignableFrom(method.getReturnType()) && method.getReturnType() != type) continue;
            try {
                method.setAccessible(true);
                Object parsed = method.invoke(null, new Object[]{value});
                if (parsed != null) return parsed;
            } catch (Throwable ignored) {}
        }
        return null;
    }

    private static Object createServerData(Class<?> type, String host, String combined) {
        Constructor<?>[] constructors = type.getDeclaredConstructors();
        Arrays.sort(constructors, Comparator.comparingInt((Constructor<?> ctor) -> ctor.getParameterTypes().length));
        String name = host;
        for (int i = 0; i < constructors.length; i++) {
            Class<?>[] types = constructors[i].getParameterTypes();
            if (types.length < 2 || types.length > 3) continue;
            if (types[0] != String.class || types[1] != String.class) continue;
            Object[] args = new Object[types.length];
            args[0] = name;
            args[1] = combined;
            if (types.length == 3) {
                if (types[2] == boolean.class || types[2] == Boolean.class) {
                    args[2] = Boolean.FALSE;
                } else if (types[2].isEnum()) {
                    args[2] = findServerTypeEnum(types[2]);
                    if (args[2] == null) continue;
                } else {
                    continue;
                }
            }
            try {
                constructors[i].setAccessible(true);
                return constructors[i].newInstance(args);
            } catch (Throwable ignored) {}
        }
        if (hintedServerDataClass != null) {
            Class<?> hinted = tryLoad(hintedServerDataClass);
            if (hinted != null && hinted != type) return createServerData(hinted, host, combined);
        }
        return null;
    }

    private static Object findServerTypeEnum(Class<?> type) {
        Object[] values = type.getEnumConstants();
        if (values == null || values.length == 0) return null;
        for (int i = 0; i < values.length; i++) {
            String name = ((Enum<?>) values[i]).name();
            if ("OTHER".equalsIgnoreCase(name) || "NORMAL".equalsIgnoreCase(name)) return values[i];
        }
        return values[values.length - 1];
    }

    private static Object currentScreen() {
        if (screenField == null || minecraftInstance == null) return null;
        try {
            return screenField.get(minecraftInstance);
        } catch (Throwable ignored) {
            return null;
        }
    }

    private static Field findScreenField(Class<?> owner) {
        String[] names = {"currentScreen", "screen", "field_71462_r", "field_1752"};
        for (int i = 0; i < names.length; i++) {
            Field field = findField(owner, names[i]);
            if (field != null && !Modifier.isStatic(field.getModifiers())
                    && looksLikeScreenClass(field.getType())) {
                try { field.setAccessible(true); } catch (Throwable ignored) {}
                return field;
            }
        }
        Field only = null;
        for (Class<?> current = owner; current != null && current != Object.class;
             current = current.getSuperclass()) {
            Field[] fields = current.getDeclaredFields();
            for (int i = 0; i < fields.length; i++) {
                Field field = fields[i];
                if (Modifier.isStatic(field.getModifiers()) || !looksLikeScreenClass(field.getType())) continue;
                if (only != null) return null;
                only = field;
            }
        }
        if (only != null) {
            try { only.setAccessible(true); } catch (Throwable ignored) {}
        }
        return only;
    }

    private static boolean looksLikeScreenClass(Class<?> type) {
        if (type == null || type.isPrimitive() || type.isArray()
                || type.getName().startsWith("java.")) return false;
        String name = type.getName();
        return name.endsWith("Screen") || name.endsWith("GuiScreen")
                || name.endsWith("class_437") || name.equals("axu");
    }

    private static Method findSetScreenMethod(Class<?> mcClass, Field screen) {
        Class<?> screenType = screen == null ? null : screen.getType();
        String[] names = {"setScreen", "displayGuiScreen", "openScreen", "method_1507"};
        for (int i = 0; i < names.length; i++) {
            Method method = findOneArgMethod(mcClass, names[i], screenType);
            if (method != null) return method;
        }
        if (screenType == null) return null;
        return findSetScreenFor(mcClass, screenType);
    }

    private static Method findSetScreenFor(Class<?> mcClass, Class<?> screenType) {
        Method found = null;
        for (Class<?> current = mcClass; current != null && current != Object.class;
             current = current.getSuperclass()) {
            Method[] methods = current.getDeclaredMethods();
            for (int i = 0; i < methods.length; i++) {
                Method method = methods[i];
                if (Modifier.isStatic(method.getModifiers()) || method.getParameterTypes().length != 1) continue;
                if (method.getReturnType() != void.class) continue;
                Class<?> param = method.getParameterTypes()[0];
                if (!param.isAssignableFrom(screenType) && param != screenType) continue;
                if (found != null) return null;
                found = method;
            }
        }
        if (found != null) {
            try { found.setAccessible(true); } catch (Throwable ignored) {}
        }
        return found;
    }

    private static Method findOneArgMethod(Class<?> owner, String name, Class<?> preferredParam) {
        for (Class<?> current = owner; current != null && current != Object.class;
             current = current.getSuperclass()) {
            Method[] methods = current.getDeclaredMethods();
            for (int i = 0; i < methods.length; i++) {
                Method method = methods[i];
                if (!method.getName().equals(name) || method.getParameterTypes().length != 1) continue;
                if (preferredParam != null
                        && !method.getParameterTypes()[0].isAssignableFrom(preferredParam)
                        && method.getParameterTypes()[0] != preferredParam) continue;
                try { method.setAccessible(true); } catch (Throwable ignored) {}
                return method;
            }
        }
        return null;
    }

    private static String describeThrowable(Throwable throwable) {
        StringBuilder result = new StringBuilder();
        Throwable current = throwable;
        for (int depth = 0; current != null && depth < 5; depth++) {
            if (depth > 0) result.append(" caused by ");
            result.append(current.getClass().getSimpleName());
            String message = current.getMessage();
            if (message != null && !message.isEmpty()) result.append(": ").append(message);
            Throwable next = current.getCause();
            if (next == current) break;
            current = next;
        }
        return result.toString();
    }

    private static boolean isBlank(String value) {
        return value == null || value.trim().isEmpty();
    }

    // =========================================================
    // Lunar Client Cosmetics, Badges, Emotes & Sprays Unlocker
    // =========================================================

    public static synchronized boolean isCosmeticsPatched() {
        return cosmeticsPatched;
    }

    public static synchronized String getCosmeticsStatus() {
        return cosmeticsStatusDetails;
    }

    private static File getSavedDir() {
        // Priority 1: Check existing prometheus/saved in .minecraft
        String appdata = System.getenv("APPDATA");
        if (appdata != null && !appdata.trim().isEmpty()) {
            File mcDir = new File(new File(appdata, ".minecraft"), "prometheus" + File.separator + "saved");
            if (mcDir.exists()) return mcDir;
        }

        // Priority 2: Check current working directory prometheus/saved
        File cwdDir = new File("prometheus", "saved");
        if (cwdDir.exists()) return cwdDir;

        // Priority 3: Create %APPDATA%/.minecraft/prometheus/saved by default
        if (appdata != null && !appdata.trim().isEmpty()) {
            File mcDir = new File(new File(appdata, ".minecraft"), "prometheus" + File.separator + "saved");
            if (mcDir.mkdirs() || mcDir.exists()) return mcDir;
        }

        cwdDir.mkdirs();
        return cwdDir;
    }

    private static File getSavedFile(String name) {
        return new File(getSavedDir(), name);
    }

    private static ClassLoader findLunarClassLoader() {
        ClassLoader cl = SessionSwitcher.class.getClassLoader();
        if (canLoadCosmetics(cl)) return cl;

        ClassLoader ccl = Thread.currentThread().getContextClassLoader();
        if (canLoadCosmetics(ccl)) return ccl;

        try {
            Object wsClient = findWebSocketClient(cl != null ? cl : ccl);
            if (wsClient != null && canLoadCosmetics(wsClient.getClass().getClassLoader())) {
                return wsClient.getClass().getClassLoader();
            }
        } catch (Throwable ignored) {}

        try {
            ThreadGroup rootGroup = Thread.currentThread().getThreadGroup();
            while (rootGroup.getParent() != null) rootGroup = rootGroup.getParent();
            Thread[] threads = new Thread[rootGroup.activeCount() + 64];
            int count = rootGroup.enumerate(threads, true);
            for (int i = 0; i < count; i++) {
                Thread t = threads[i];
                if (t != null) {
                    ClassLoader tcl = t.getContextClassLoader();
                    if (canLoadCosmetics(tcl)) return tcl;
                    ClassLoader lcl = t.getClass().getClassLoader();
                    if (canLoadCosmetics(lcl)) return lcl;
                }
            }
        } catch (Throwable ignored) {}

        return cl != null ? cl : ClassLoader.getSystemClassLoader();
    }

    private static boolean canLoadCosmetics(ClassLoader cl) {
        if (cl == null) return false;
        try {
            cl.loadClass("com.lunarclient.websocket.cosmetic.v2.CosmeticService$Stub");
            return true;
        } catch (Throwable t) {
            return false;
        }
    }

    private static void ensureSavedFiles(ClassLoader cl) {
        try {
            File outfitFile = getSavedFile("outfit.bin");
            if (!outfitFile.exists() || outfitFile.length() == 0) {
                Object defaultOutfit = readSavedOutfit(cl);
                if (defaultOutfit != null) {
                    writeSavedOutfit(defaultOutfit);
                } else {
                    if (outfitFile.getParentFile() != null) outfitFile.getParentFile().mkdirs();
                    if (!outfitFile.exists()) {
                        FileOutputStream fos = new FileOutputStream(outfitFile);
                        fos.close();
                    }
                }
            }
            File badgeFile = getSavedFile("badge.bin");
            if (!badgeFile.exists() || badgeFile.length() == 0) {
                writeSavedBadge(0);
            }
            File emotesFile = getSavedFile("emotes.bin");
            if (!emotesFile.exists() || emotesFile.length() == 0) {
                writeSavedEquippedEmotes(new ArrayList<Object>());
            }
            File spraysFile = getSavedFile("sprays.bin");
            if (!spraysFile.exists() || spraysFile.length() == 0) {
                writeSavedEquippedSprays(new ArrayList<Object>());
            }
        } catch (Throwable t) {
            System.err.println("[LunarCookies] Error ensuring saved files: " + t);
        }
    }

    public static synchronized String patchCosmetics() {
        try {
            ClassLoader cl = findLunarClassLoader();
            System.out.println("[LunarCookies] Using classloader for cosmetics: " + cl);

            // Ensure initial default save files exist in prometheus/saved/
            ensureSavedFiles(cl);

            int patchedCount = 0;
            List<String> patchedNames = new ArrayList<String>();

            // 1. CosmeticService$Stub
            try {
                Class<?> cosmeticStubClass = cl.loadClass("com.lunarclient.websocket.cosmetic.v2.CosmeticService$Stub");
                byte[] rawBytes = loadClassBytes(cl, "com/lunarclient/websocket/cosmetic/v2/CosmeticService$Stub.class");
                if (rawBytes != null) {
                    byte[] patchedBytes = patchStubClassBytes(rawBytes, "com/lunarcookies/SessionSwitcher", new String[][]{
                        {"login", "(Lcom/google/protobuf/RpcController;Lcom/lunarclient/websocket/cosmetic/v2/LoginRequest;Lcom/google/protobuf/RpcCallback;)V", "handleCosmeticLogin"},
                        {"updateOutfit", "(Lcom/google/protobuf/RpcController;Lcom/lunarclient/websocket/cosmetic/v2/UpdateOutfitRequest;Lcom/google/protobuf/RpcCallback;)V", "handleUpdateOutfit"}
                    });
                    boolean ok = patchedBytes != null && nativeRedefineClass(cosmeticStubClass, patchedBytes);
                    System.out.println("[LunarCookies] CosmeticService$Stub redefinition result: " + ok);
                    if (ok) {
                        patchedCount++;
                        patchedNames.add("Cosmetics");
                    }
                } else {
                    System.err.println("[LunarCookies] loadClassBytes failed for CosmeticService$Stub");
                }
            } catch (Throwable t) {
                System.err.println("[LunarCookies] Error patching CosmeticService$Stub: " + t);
            }

            // 2. BadgeService$Stub
            try {
                Class<?> badgeStubClass = cl.loadClass("com.lunarclient.websocket.badge.v1.BadgeService$Stub");
                byte[] rawBytes = loadClassBytes(cl, "com/lunarclient/websocket/badge/v1/BadgeService$Stub.class");
                if (rawBytes != null) {
                    byte[] patchedBytes = patchStubClassBytes(rawBytes, "com/lunarcookies/SessionSwitcher", new String[][]{
                        {"login", "(Lcom/google/protobuf/RpcController;Lcom/lunarclient/websocket/badge/v1/LoginRequest;Lcom/google/protobuf/RpcCallback;)V", "handleBadgeLogin"},
                        {"equipBadge", "(Lcom/google/protobuf/RpcController;Lcom/lunarclient/websocket/badge/v1/EquipBadgeRequest;Lcom/google/protobuf/RpcCallback;)V", "handleEquipBadge"}
                    });
                    boolean ok = patchedBytes != null && nativeRedefineClass(badgeStubClass, patchedBytes);
                    System.out.println("[LunarCookies] BadgeService$Stub redefinition result: " + ok);
                    if (ok) {
                        patchedCount++;
                        patchedNames.add("Badges");
                    }
                } else {
                    System.err.println("[LunarCookies] loadClassBytes failed for BadgeService$Stub");
                }
            } catch (Throwable t) {
                System.err.println("[LunarCookies] Error patching BadgeService$Stub: " + t);
            }

            // 3. EmoteService$Stub
            try {
                Class<?> emoteStubClass = cl.loadClass("com.lunarclient.websocket.emote.v1.EmoteService$Stub");
                byte[] rawBytes = loadClassBytes(cl, "com/lunarclient/websocket/emote/v1/EmoteService$Stub.class");
                if (rawBytes != null) {
                    byte[] patchedBytes = patchStubClassBytes(rawBytes, "com/lunarcookies/SessionSwitcher", new String[][]{
                        {"login", "(Lcom/google/protobuf/RpcController;Lcom/lunarclient/websocket/emote/v1/LoginRequest;Lcom/google/protobuf/RpcCallback;)V", "handleEmoteLogin"},
                        {"useEmote", "(Lcom/google/protobuf/RpcController;Lcom/lunarclient/websocket/emote/v1/UseEmoteRequest;Lcom/google/protobuf/RpcCallback;)V", "handleUseEmote"},
                        {"updateEquippedEmotes", "(Lcom/google/protobuf/RpcController;Lcom/lunarclient/websocket/emote/v1/UpdateEquippedEmotesRequest;Lcom/google/protobuf/RpcCallback;)V", "handleUpdateEquippedEmotes"}
                    });
                    boolean ok = patchedBytes != null && nativeRedefineClass(emoteStubClass, patchedBytes);
                    System.out.println("[LunarCookies] EmoteService$Stub redefinition result: " + ok);
                    if (ok) {
                        patchedCount++;
                        patchedNames.add("Emotes");
                    }
                } else {
                    System.err.println("[LunarCookies] loadClassBytes failed for EmoteService$Stub");
                }
            } catch (Throwable t) {
                System.err.println("[LunarCookies] Error patching EmoteService$Stub: " + t);
            }

            // 4. SprayService$Stub
            try {
                Class<?> sprayStubClass = cl.loadClass("com.lunarclient.websocket.spray.v1.SprayService$Stub");
                byte[] rawBytes = loadClassBytes(cl, "com/lunarclient/websocket/spray/v1/SprayService$Stub.class");
                if (rawBytes != null) {
                    byte[] patchedBytes = patchStubClassBytes(rawBytes, "com/lunarcookies/SessionSwitcher", new String[][]{
                        {"login", "(Lcom/google/protobuf/RpcController;Lcom/lunarclient/websocket/spray/v1/LoginRequest;Lcom/google/protobuf/RpcCallback;)V", "handleSprayLogin"},
                        {"useSpray", "(Lcom/google/protobuf/RpcController;Lcom/lunarclient/websocket/spray/v1/UseSprayRequest;Lcom/google/protobuf/RpcCallback;)V", "handleUseSpray"},
                        {"updateEquippedSprays", "(Lcom/google/protobuf/RpcController;Lcom/lunarclient/websocket/spray/v1/UpdateEquippedSpraysRequest;Lcom/google/protobuf/RpcCallback;)V", "handleUpdateEquippedSprays"}
                    });
                    boolean ok = patchedBytes != null && nativeRedefineClass(sprayStubClass, patchedBytes);
                    System.out.println("[LunarCookies] SprayService$Stub redefinition result: " + ok);
                    if (ok) {
                        patchedCount++;
                        patchedNames.add("Sprays");
                    }
                } else {
                    System.err.println("[LunarCookies] loadClassBytes failed for SprayService$Stub");
                }
            } catch (Throwable t) {
                System.err.println("[LunarCookies] Error patching SprayService$Stub: " + t);
            }

            if (patchedCount > 0) {
                cosmeticsPatched = true;
                StringBuilder sb = new StringBuilder("Unlocked: ");
                for (int i = 0; i < patchedNames.size(); i++) {
                    if (i > 0) sb.append(", ");
                    sb.append(patchedNames.get(i));
                }
                cosmeticsStatusDetails = sb.toString();

                ensureSavedFiles(cl);

                // Trigger in-game refresh so Lunar's live in-memory caches populate immediately
                try {
                    triggerLunarRefresh(cl);
                } catch (Throwable t) {
                    System.err.println("[LunarCookies] Error triggering Lunar refresh: " + t);
                }

                try {
                    final ClassLoader fcl = cl;
                    runOnGameThread(new Runnable() {
                        @Override
                        public void run() {
                            try {
                                triggerLunarRefresh(fcl);
                            } catch (Throwable t) {
                                System.err.println("[LunarCookies] Game thread refresh error: " + t);
                            }
                        }
                    });
                } catch (Throwable ignored) {}

                return "ok:" + cosmeticsStatusDetails;
            } else {
                return "error:Lunar services not found or redefinition failed";
            }
        } catch (Throwable t) {
            return "error:" + describeThrowable(t);
        }
    }

    // ---------------------------------------------------------
    // In-Game Live Refresh Triggering
    // ---------------------------------------------------------

    public static void triggerLunarRefresh(ClassLoader cl) {
        if (cl == null) {
            cl = SessionSwitcher.class.getClassLoader();
            if (cl == null) cl = Thread.currentThread().getContextClassLoader();
        }

        try {
            Object wsClient = findWebSocketClient(cl);
            if (wsClient == null) {
                System.out.println("[LunarCookies] Asset WebSocketClient instance not found for refresh trigger.");
            } else {
                System.out.println("[LunarCookies] Found Asset WebSocketClient: " + wsClient.getClass().getName());

                // 1. Dispatch Push Messages to trigger standard Lunar websocket event handlers
                try {
                    Class<?> pushClass = cl.loadClass("com.lunarclient.websocket.cosmetic.v2.RefreshCosmeticsPush");
                    Object push = pushClass.getMethod("getDefaultInstance").invoke(null);
                    boolean pushed = invokePushMethod(wsClient, pushClass, push);
                    System.out.println("[LunarCookies] Pushed RefreshCosmeticsPush: " + pushed);
                } catch (Throwable t) {
                    System.err.println("[LunarCookies] Push RefreshCosmeticsPush failed: " + t);
                }

                try {
                    Class<?> pushClass = cl.loadClass("com.lunarclient.websocket.badge.v1.RefreshBadgesPush");
                    Object push = pushClass.getMethod("getDefaultInstance").invoke(null);
                    boolean pushed = invokePushMethod(wsClient, pushClass, push);
                    System.out.println("[LunarCookies] Pushed RefreshBadgesPush: " + pushed);
                } catch (Throwable t) {
                    System.err.println("[LunarCookies] Push RefreshBadgesPush failed: " + t);
                }

                try {
                    Class<?> pushClass = cl.loadClass("com.lunarclient.websocket.emote.v1.RefreshEmotesPush");
                    Object push = pushClass.getMethod("getDefaultInstance").invoke(null);
                    boolean pushed = invokePushMethod(wsClient, pushClass, push);
                    System.out.println("[LunarCookies] Pushed RefreshEmotesPush: " + pushed);
                } catch (Throwable t) {
                    System.err.println("[LunarCookies] Push RefreshEmotesPush failed: " + t);
                }

                // 2. Invoke zero-arg login dispatchers if present on wsClient
                invokeZeroArgMethod(wsClient, "HROIRCOOIHCIICOHOICRRCORRHHHOH"); // cosmetic login
                invokeZeroArgMethod(wsClient, "HOOORIRCHIHRRCCCCCRCHIIHOOCIHC"); // badge login
                invokeZeroArgMethod(wsClient, "CCOOIIHCCCOORRCIHICHHCHICHCOOI"); // emote login
                invokeZeroArgMethod(wsClient, "ICIOCCRCCHRHCOIRCIHHIIRIHRIOOO"); // spray login

                // 3. Direct LoginResponse feed into wsClient's response receiver methods
                // (Methods like RCRROIORHICCOHOIIIRROHIORIIIHC(cosmetic.LoginResponse) immediately update in-memory models)
                try {
                    Object cosmeticResp = buildCosmeticLoginResponse(cl);
                    if (cosmeticResp != null) {
                        boolean fed = feedLoginResponse(wsClient, cosmeticResp);
                        System.out.println("[LunarCookies] Directly fed cosmetic LoginResponse to wsClient: " + fed);
                    }
                } catch (Throwable t) {
                    System.err.println("[LunarCookies] Direct feed cosmetic to wsClient failed: " + t);
                }

                try {
                    Object badgeResp = buildBadgeLoginResponse(cl);
                    if (badgeResp != null) {
                        boolean fed = feedLoginResponse(wsClient, badgeResp);
                        System.out.println("[LunarCookies] Directly fed badge LoginResponse to wsClient: " + fed);
                    }
                } catch (Throwable t) {
                    System.err.println("[LunarCookies] Direct feed badge to wsClient failed: " + t);
                }

                try {
                    Object emoteResp = buildEmoteLoginResponse(cl);
                    if (emoteResp != null) {
                        boolean fed = feedLoginResponse(wsClient, emoteResp);
                        System.out.println("[LunarCookies] Directly fed emote LoginResponse to wsClient: " + fed);
                    }
                } catch (Throwable t) {
                    System.err.println("[LunarCookies] Direct feed emote to wsClient failed: " + t);
                }

                try {
                    Object sprayResp = buildSprayLoginResponse(cl);
                    if (sprayResp != null) {
                        boolean fed = feedLoginResponse(wsClient, sprayResp);
                        System.out.println("[LunarCookies] Directly fed spray LoginResponse to wsClient: " + fed);
                    }
                } catch (Throwable t) {
                    System.err.println("[LunarCookies] Direct feed spray to wsClient failed: " + t);
                }

                // 4. Reconnect WebSocket client
                try {
                    Method reconnect = wsClient.getClass().getMethod("reconnect");
                    reconnect.invoke(wsClient);
                    System.out.println("[LunarCookies] Triggered wsClient.reconnect()");
                } catch (Throwable t) {
                    System.err.println("[LunarCookies] wsClient.reconnect() error: " + t);
                }
            }

            // 5. Also feed LoginResponses directly into LunarClient's internal models (e.g. CosmeticModel)
            try {
                feedLunarManagers(cl);
            } catch (Throwable t) {
                System.err.println("[LunarCookies] Error feeding Lunar managers: " + t);
            }

            // 6. Force cosmeticState to READY
            try {
                setCosmeticStateReady(wsClient, cl);
            } catch (Throwable t) {
                System.err.println("[LunarCookies] Error setting cosmeticState: " + t);
            }

        } catch (Throwable t) {
            System.err.println("[LunarCookies] Error in triggerLunarRefresh: " + t);
        }
    }

    private static void setCosmeticStateReady(Object wsClient, ClassLoader cl) {
        if (wsClient == null && cl != null) {
            wsClient = findWebSocketClient(cl);
        }
        if (wsClient == null) return;
        try {
            Class<?> stateEnumClass = null;
            Object readyVal = "ready";
            try {
                stateEnumClass = cl.loadClass("com.moonsworth.lunar.client.HRCORCCCHOCRCICCRHOHHICOIIICII.RCIORCRRIROROHROCCOIIOHCHIICRC");
                if (stateEnumClass != null) {
                    Object readyEnum = Enum.valueOf((Class<Enum>) stateEnumClass, "READY");
                    Method getId = stateEnumClass.getMethod("getId");
                    readyVal = getId.invoke(readyEnum);
                }
            } catch (Throwable ignored) {}

            for (Class<?> cur = wsClient.getClass(); cur != null && cur != Object.class; cur = cur.getSuperclass()) {
                for (Field f : cur.getDeclaredFields()) {
                    if (!Modifier.isStatic(f.getModifiers())) {
                        f.setAccessible(true);
                        Object val = f.get(wsClient);
                        if (val != null) {
                            for (Method m : val.getClass().getDeclaredMethods()) {
                                if (m.getParameterTypes().length == 2 && m.getParameterTypes()[0] == String.class) {
                                    try {
                                        m.setAccessible(true);
                                        m.invoke(val, "cosmeticState", readyVal);
                                        System.out.println("[LunarCookies] Set cosmeticState to " + readyVal + " on " + val.getClass().getSimpleName());
                                    } catch (Throwable ignored) {}
                                }
                            }
                        }
                    }
                }
            }
        } catch (Throwable t) {
            System.err.println("[LunarCookies] setCosmeticStateReady error: " + t);
        }
    }

    private static void feedLunarManagers(ClassLoader cl) {
        try {
            Object lcInstance = null;
            for (String cn : new String[]{"com.moonsworth.lunar.client.RCRROIORHICCOHOIIIRROHIORIIIHC", "com.moonsworth.lunar.client.LunarClient"}) {
                try {
                    Class<?> lcClass = cl.loadClass(cn);
                    for (Method m : lcClass.getDeclaredMethods()) {
                        if (Modifier.isStatic(m.getModifiers()) && m.getParameterTypes().length == 0 && m.getReturnType() == lcClass) {
                            m.setAccessible(true);
                            lcInstance = m.invoke(null);
                            if (lcInstance != null) break;
                        }
                    }
                    if (lcInstance != null) break;
                    for (Field f : lcClass.getDeclaredFields()) {
                        if (Modifier.isStatic(f.getModifiers()) && f.getType() == lcClass) {
                            f.setAccessible(true);
                            lcInstance = f.get(null);
                            if (lcInstance != null) break;
                        }
                    }
                    if (lcInstance != null) break;
                } catch (Throwable ignored) {}
            }
            if (lcInstance == null) return;

            Object cosmeticResp = buildCosmeticLoginResponse(cl);
            if (cosmeticResp == null) return;

            for (Class<?> cur = lcInstance.getClass(); cur != null && cur != Object.class; cur = cur.getSuperclass()) {
                for (Field f : cur.getDeclaredFields()) {
                    if (!Modifier.isStatic(f.getModifiers())) {
                        try {
                            f.setAccessible(true);
                            Object val = f.get(lcInstance);
                            if (val != null) {
                                feedLoginResponse(val, cosmeticResp);
                            }
                        } catch (Throwable ignored) {}
                    }
                }
            }
        } catch (Throwable ignored) {}
    }

    private static boolean feedLoginResponse(Object target, Object response) {
        if (target == null || response == null) return false;
        Class<?> respClass = response.getClass();
        boolean success = false;
        Class<?> targetClass = (target instanceof Class) ? (Class<?>) target : target.getClass();
        for (Class<?> cur = targetClass; cur != null && cur != Object.class; cur = cur.getSuperclass()) {
            for (Method m : cur.getDeclaredMethods()) {
                if (m.getParameterTypes().length == 1 && m.getParameterTypes()[0].isAssignableFrom(respClass)) {
                    String mName = m.getName();
                    if (mName.equals("compareTo") || mName.equals("add") || mName.equals("remove") || mName.equals("equals")) {
                        continue;
                    }
                    try {
                        m.setAccessible(true);
                        if (Modifier.isStatic(m.getModifiers())) {
                            m.invoke(null, response);
                        } else if (!(target instanceof Class)) {
                            m.invoke(target, response);
                        }
                        success = true;
                    } catch (Throwable t) {
                        Throwable cause = (t instanceof InvocationTargetException && t.getCause() != null) ? t.getCause() : t;
                        System.err.println("[LunarCookies] feedLoginResponse error on " + m.getName() + ": " + cause);
                    }
                }
            }
        }
        return success;
    }

    private static Object findWebSocketClient(ClassLoader cl) {
        // Strategy 1: Ref singleton (com.moonsworth.lunar.client.util.CHRRRIOROCCOCHHROHCHORROOROHCR)
        try {
            Class<?> refClass = cl.loadClass("com.moonsworth.lunar.client.util.CHRRRIOROCCOCHHROHCHORROOROHCR");
            for (Method m : refClass.getDeclaredMethods()) {
                if (Modifier.isStatic(m.getModifiers()) && m.getParameterTypes().length == 0) {
                    if (m.getReturnType().getName().equals("java.util.Optional")) {
                        m.setAccessible(true);
                        Object opt = m.invoke(null);
                        if (opt != null) {
                            Method isPresent = opt.getClass().getMethod("isPresent");
                            if (Boolean.TRUE.equals(isPresent.invoke(opt))) {
                                Object val = opt.getClass().getMethod("get").invoke(opt);
                                if (val != null && isAssetWebSocketClient(val.getClass())) {
                                    return val;
                                }
                            }
                        }
                    } else if (isAssetWebSocketClient(m.getReturnType())) {
                        m.setAccessible(true);
                        Object val = m.invoke(null);
                        if (val != null) return val;
                    }
                }
            }
        } catch (Throwable t) {
            System.err.println("[LunarCookies] Strategy 1 (Ref) error: " + t);
        }

        // Strategy 2: LunarClient singleton
        try {
            for (String cn : new String[]{"com.moonsworth.lunar.client.RCRROIORHICCOHOIIIRROHIORIIIHC", "com.moonsworth.lunar.client.LunarClient"}) {
                try {
                    Class<?> lcClass = cl.loadClass(cn);
                    Object lcInstance = null;
                    for (Method m : lcClass.getDeclaredMethods()) {
                        if (Modifier.isStatic(m.getModifiers()) && m.getParameterTypes().length == 0 && m.getReturnType() == lcClass) {
                            m.setAccessible(true);
                            lcInstance = m.invoke(null);
                            if (lcInstance != null) break;
                        }
                    }
                    if (lcInstance == null) {
                        for (Field f : lcClass.getDeclaredFields()) {
                            if (Modifier.isStatic(f.getModifiers()) && f.getType() == lcClass) {
                                f.setAccessible(true);
                                lcInstance = f.get(null);
                                if (lcInstance != null) break;
                            }
                        }
                    }
                    if (lcInstance != null) {
                        for (Method m : lcClass.getDeclaredMethods()) {
                            if (!Modifier.isStatic(m.getModifiers()) && m.getParameterTypes().length == 0) {
                                if (m.getReturnType().getName().equals("java.util.Optional")) {
                                    m.setAccessible(true);
                                    Object opt = m.invoke(lcInstance);
                                    if (opt != null) {
                                        Method isPresent = opt.getClass().getMethod("isPresent");
                                        if (Boolean.TRUE.equals(isPresent.invoke(opt))) {
                                            Object val = opt.getClass().getMethod("get").invoke(opt);
                                            if (val != null && isAssetWebSocketClient(val.getClass())) {
                                                return val;
                                            }
                                        }
                                    }
                                } else if (isAssetWebSocketClient(m.getReturnType())) {
                                    m.setAccessible(true);
                                    Object ws = m.invoke(lcInstance);
                                    if (ws != null) return ws;
                                }
                            }
                        }
                        for (Field f : lcClass.getDeclaredFields()) {
                            if (!Modifier.isStatic(f.getModifiers())) {
                                f.setAccessible(true);
                                Object val = f.get(lcInstance);
                                if (val != null && isAssetWebSocketClient(val.getClass())) {
                                    return val;
                                }
                            }
                        }
                    }
                } catch (Throwable ignored) {}
            }
        } catch (Throwable t) {
            System.err.println("[LunarCookies] Strategy 2 (LunarClient) error: " + t);
        }

        // Strategy 3: Thread inspection
        try {
            ThreadGroup rootGroup = Thread.currentThread().getThreadGroup();
            while (rootGroup.getParent() != null) rootGroup = rootGroup.getParent();
            Thread[] threads = new Thread[rootGroup.activeCount() + 64];
            int count = rootGroup.enumerate(threads, true);
            for (int i = 0; i < count; i++) {
                Thread t = threads[i];
                if (t == null) continue;
                if (isAssetWebSocketClient(t.getClass())) return t;
                for (Class<?> cur = t.getClass(); cur != null && cur != Object.class; cur = cur.getSuperclass()) {
                    for (Field f : cur.getDeclaredFields()) {
                        try {
                            f.setAccessible(true);
                            Object val = f.get(t);
                            if (val != null && isAssetWebSocketClient(val.getClass())) return val;
                        } catch (Throwable ignored) {}
                    }
                }
            }
        } catch (Throwable t) {
            System.err.println("[LunarCookies] Strategy 3 (Threads) error: " + t);
        }

        return null;
    }

    private static boolean isAssetWebSocketClient(Class<?> clazz) {
        if (clazz == null) return false;
        boolean extendsWs = false;
        for (Class<?> cur = clazz; cur != null && cur != Object.class; cur = cur.getSuperclass()) {
            if (cur.getName().contains("WebSocketClient")) {
                extendsWs = true;
                break;
            }
        }
        if (!extendsWs) return false;

        // Must NOT be the launcher IPC client
        if (clazz.getName().contains("gameipc")) return false;

        // Must have cosmetic reference in fields or methods
        for (Field f : clazz.getDeclaredFields()) {
            String typeName = f.getType().getName();
            if (typeName.contains("cosmetic") || typeName.contains("CosmeticService")) return true;
        }
        for (Method m : clazz.getDeclaredMethods()) {
            if (m.getReturnType().getName().contains("cosmetic")) return true;
            for (Class<?> p : m.getParameterTypes()) {
                if (p.getName().contains("cosmetic") || p.getName().contains("CosmeticService")) return true;
            }
        }
        return false;
    }

    private static boolean invokePushMethod(Object wsClient, Class<?> paramType, Object paramVal) {
        if (wsClient == null || paramType == null || paramVal == null) return false;
        for (Class<?> cur = wsClient.getClass(); cur != null && cur != Object.class; cur = cur.getSuperclass()) {
            for (Method m : cur.getDeclaredMethods()) {
                if (m.getParameterTypes().length == 1 && m.getParameterTypes()[0].isAssignableFrom(paramType)) {
                    try {
                        m.setAccessible(true);
                        m.invoke(wsClient, paramVal);
                        return true;
                    } catch (Throwable ignored) {}
                }
            }
        }
        return false;
    }

    private static boolean invokeZeroArgMethod(Object target, String methodName) {
        if (target == null || methodName == null) return false;
        for (Class<?> cur = target.getClass(); cur != null && cur != Object.class; cur = cur.getSuperclass()) {
            try {
                Method m = cur.getDeclaredMethod(methodName);
                if (m.getParameterTypes().length == 0) {
                    m.setAccessible(true);
                    m.invoke(target);
                    return true;
                }
            } catch (Throwable ignored) {}
        }
        return false;
    }

    // ---------------------------------------------------------
    // RPC Handlers called directly from patched bytecode
    // ---------------------------------------------------------

    private static ClassLoader resolveClassLoader(Object obj) {
        ClassLoader cl = obj != null ? obj.getClass().getClassLoader() : null;
        if (cl == null) cl = SessionSwitcher.class.getClassLoader();
        if (cl == null) cl = Thread.currentThread().getContextClassLoader();
        return cl;
    }

    public static Object buildCosmeticLoginResponse(ClassLoader cl) {
        try {
            Class<?> loginResponseClass = cl.loadClass("com.lunarclient.websocket.cosmetic.v2.LoginResponse");
            Class<?> outfitClass = cl.loadClass("com.lunarclient.websocket.cosmetic.v2.Outfit");
            Class<?> outfitTreeClass = cl.loadClass("com.lunarclient.websocket.cosmetic.v2.OutfitTree");

            Object outfit = readSavedOutfit(cl);

            Object builder = loginResponseClass.getMethod("newBuilder").invoke(null);
            builder.getClass().getMethod("setHasAllCosmeticsFlag", boolean.class).invoke(builder, true);
            builder.getClass().getMethod("setArtistTools", boolean.class).invoke(builder, true);

            if (outfit != null) {
                builder.getClass().getMethod("addOutfits", outfitClass).invoke(builder, outfit);
            }

            Object treeBuilder = outfitTreeClass.getMethod("newBuilder").invoke(null);
            if (outfit != null) {
                Object outfitId = outfitClass.getMethod("getId").invoke(outfit);
                for (Method m : treeBuilder.getClass().getMethods()) {
                    if (m.getName().equals("setDefaultOutfitId") && m.getParameterTypes().length == 1
                            && (outfitId == null || m.getParameterTypes()[0].isInstance(outfitId))) {
                        m.invoke(treeBuilder, outfitId);
                        break;
                    }
                }
            }
            Object outfitTree = treeBuilder.getClass().getMethod("build").invoke(treeBuilder);
            builder.getClass().getMethod("setOutfitTree", outfitTreeClass).invoke(builder, outfitTree);

            try {
                Class<?> visEnum = cl.loadClass("com.lunarclient.websocket.cosmetic.v2.CosmeticOwnershipVisibility");
                Object visEveryone = Enum.valueOf((Class<Enum>) visEnum, "COSMETIC_OWNERSHIP_VISIBILITY_EVERYONE");
                for (Method m : builder.getClass().getMethods()) {
                    if (m.getName().equals("setCosmeticOwnershipVisibility") && m.getParameterTypes().length == 1
                            && m.getParameterTypes()[0].isAssignableFrom(visEnum)) {
                        m.invoke(builder, visEveryone);
                        break;
                    }
                }
            } catch (Throwable ignored) {}

            try {
                builder.getClass().getMethod("setRankName", String.class).invoke(builder, "Lunar+");
            } catch (Throwable ignored) {}

            try {
                Class<?> colorClass = cl.loadClass("com.lunarclient.common.v1.Color");
                Object colorBuilder = colorClass.getMethod("newBuilder").invoke(null);
                colorBuilder.getClass().getMethod("setColor", int.class).invoke(colorBuilder, 0);
                Object zeroColor = colorBuilder.getClass().getMethod("build").invoke(colorBuilder);
                for (Method m : builder.getClass().getMethods()) {
                    if (m.getParameterTypes().length == 1 && m.getParameterTypes()[0].isAssignableFrom(colorClass)) {
                        if (m.getName().equals("setPlusColor") || m.getName().equals("setLogoColor")) {
                            m.invoke(builder, zeroColor);
                        }
                    }
                }
            } catch (Throwable ignored) {}

            try {
                builder.getClass().getMethod("setLogoAlwaysShow", boolean.class).invoke(builder, false);
            } catch (Throwable ignored) {}

            return builder.getClass().getMethod("build").invoke(builder);
        } catch (Throwable t) {
            System.err.println("[LunarCookies] Error building cosmetic LoginResponse: " + t);
            t.printStackTrace();
            return null;
        }
    }

    public static void handleCosmeticLogin(Object controller, Object request, Object callback) {
        System.out.println("[LunarCookies] handleCosmeticLogin called!");
        try {
            ClassLoader cl = resolveClassLoader(callback);
            Object response = buildCosmeticLoginResponse(cl);
            runCallback(callback, response);
            System.out.println("[LunarCookies] Delivered cosmetic LoginResponse to callback!");
            setCosmeticStateReady(null, cl);
        } catch (Throwable t) {
            Throwable cause = (t instanceof InvocationTargetException && t.getCause() != null) ? t.getCause() : t;
            System.err.println("[LunarCookies] Error handling cosmetic login: " + cause);
            cause.printStackTrace();
        }
    }

    public static void handleUpdateOutfit(Object controller, Object request, Object callback) {
        System.out.println("[LunarCookies] handleUpdateOutfit called!");
        try {
            Method getOutfit = request.getClass().getMethod("getOutfit");
            Object outfit = getOutfit.invoke(request);
            writeSavedOutfit(outfit);
            System.out.println("[LunarCookies] Saved outfit to disk!");

            if (callback != null) {
                ClassLoader cl = resolveClassLoader(callback);
                try {
                    Class<?> respClass = cl.loadClass("com.lunarclient.websocket.cosmetic.v2.UpdateOutfitResponse");
                    Object defResp = respClass.getMethod("getDefaultInstance").invoke(null);
                    runCallback(callback, defResp);
                } catch (Throwable ignored) {
                    runCallback(callback, request);
                }
            }
        } catch (Throwable t) {
            System.err.println("[LunarCookies] Error handling updateOutfit: " + t);
        }
    }

    public static Object buildBadgeLoginResponse(ClassLoader cl) {
        try {
            Class<?> respClass = cl.loadClass("com.lunarclient.websocket.badge.v1.LoginResponse");
            int badgeId = readSavedBadge();

            Object builder = respClass.getMethod("newBuilder").invoke(null);
            builder.getClass().getMethod("setHasAllBadgesFlag", boolean.class).invoke(builder, true);
            builder.getClass().getMethod("setEquippedBadgeId", int.class).invoke(builder, badgeId);
            return builder.getClass().getMethod("build").invoke(builder);
        } catch (Throwable t) {
            System.err.println("[LunarCookies] Error building badge LoginResponse: " + t);
            return null;
        }
    }

    public static void handleBadgeLogin(Object controller, Object request, Object callback) {
        System.out.println("[LunarCookies] handleBadgeLogin called!");
        try {
            ClassLoader cl = resolveClassLoader(callback);
            Object response = buildBadgeLoginResponse(cl);
            runCallback(callback, response);
            System.out.println("[LunarCookies] Delivered badge LoginResponse to callback!");
        } catch (Throwable t) {
            Throwable cause = (t instanceof InvocationTargetException && t.getCause() != null) ? t.getCause() : t;
            System.err.println("[LunarCookies] Error handling badge login: " + cause);
            cause.printStackTrace();
        }
    }

    public static void handleEquipBadge(Object controller, Object request, Object callback) {
        System.out.println("[LunarCookies] handleEquipBadge called!");
        try {
            Method getBadgeId = request.getClass().getMethod("getBadgeId");
            int badgeId = (Integer) getBadgeId.invoke(request);
            writeSavedBadge(badgeId);
            System.out.println("[LunarCookies] Saved badge to disk: " + badgeId);

            if (callback != null) {
                ClassLoader cl = resolveClassLoader(callback);
                Class<?> respClass = cl.loadClass("com.lunarclient.websocket.badge.v1.EquipBadgeResponse");
                Object defResp = respClass.getMethod("getDefaultInstance").invoke(null);
                runCallback(callback, defResp);
            }
        } catch (Throwable t) {
            Throwable cause = (t instanceof InvocationTargetException && t.getCause() != null) ? t.getCause() : t;
            System.err.println("[LunarCookies] Error handling equipBadge: " + cause);
            cause.printStackTrace();
        }
    }

    public static Object buildEmoteLoginResponse(ClassLoader cl) {
        try {
            Class<?> respClass = cl.loadClass("com.lunarclient.websocket.emote.v1.LoginResponse");
            List<?> equipped = readSavedEquippedEmotes(cl);

            Object builder = respClass.getMethod("newBuilder").invoke(null);
            builder.getClass().getMethod("setHasAllEmotesFlag", boolean.class).invoke(builder, true);
            for (Method m : builder.getClass().getMethods()) {
                if (m.getName().equals("addAllEquippedEmotes") && m.getParameterTypes().length == 1) {
                    m.invoke(builder, equipped);
                    break;
                }
            }
            return builder.getClass().getMethod("build").invoke(builder);
        } catch (Throwable t) {
            System.err.println("[LunarCookies] Error building emote LoginResponse: " + t);
            return null;
        }
    }

    public static void handleEmoteLogin(Object controller, Object request, Object callback) {
        System.out.println("[LunarCookies] handleEmoteLogin called!");
        try {
            ClassLoader cl = resolveClassLoader(callback);
            Object response = buildEmoteLoginResponse(cl);
            runCallback(callback, response);
            System.out.println("[LunarCookies] Delivered emote LoginResponse to callback!");
        } catch (Throwable t) {
            Throwable cause = (t instanceof InvocationTargetException && t.getCause() != null) ? t.getCause() : t;
            System.err.println("[LunarCookies] Error handling emote login: " + cause);
            cause.printStackTrace();
        }
    }

    public static void handleUseEmote(Object controller, Object request, Object callback) {
        try {
            ClassLoader cl = resolveClassLoader(callback);
            Class<?> respClass = cl.loadClass("com.lunarclient.websocket.emote.v1.UseEmoteResponse");
            Class<?> statusEnum = cl.loadClass("com.lunarclient.websocket.emote.v1.UseEmoteResponse$Status");
            Object statusOk = Enum.valueOf((Class<Enum>) statusEnum, "STATUS_OK");

            Object builder = respClass.getMethod("newBuilder").invoke(null);
            builder.getClass().getMethod("setStatus", statusEnum).invoke(builder, statusOk);
            Object response = builder.getClass().getMethod("build").invoke(builder);

            runCallback(callback, response);
        } catch (Throwable t) {
            Throwable cause = (t instanceof InvocationTargetException && t.getCause() != null) ? t.getCause() : t;
            System.err.println("[LunarCookies] Error handling useEmote: " + cause);
            cause.printStackTrace();
        }
    }

    public static void handleUpdateEquippedEmotes(Object controller, Object request, Object callback) {
        System.out.println("[LunarCookies] handleUpdateEquippedEmotes called!");
        try {
            Method getList = request.getClass().getMethod("getEquippedEmotesList");
            List<?> emotes = (List<?>) getList.invoke(request);
            writeSavedEquippedEmotes(emotes);
            System.out.println("[LunarCookies] Saved equipped emotes to disk!");

            if (callback != null) {
                ClassLoader cl = resolveClassLoader(callback);
                Class<?> respClass = cl.loadClass("com.lunarclient.websocket.emote.v1.UpdateEquippedEmotesResponse");
                Object defResp = respClass.getMethod("getDefaultInstance").invoke(null);
                runCallback(callback, defResp);
            }
        } catch (Throwable t) {
            Throwable cause = (t instanceof InvocationTargetException && t.getCause() != null) ? t.getCause() : t;
            System.err.println("[LunarCookies] Error handling updateEquippedEmotes: " + cause);
            cause.printStackTrace();
        }
    }

    public static Object buildSprayLoginResponse(ClassLoader cl) {
        try {
            Class<?> respClass = cl.loadClass("com.lunarclient.websocket.spray.v1.LoginResponse");
            List<?> equipped = readSavedEquippedSprays(cl);

            Object builder = respClass.getMethod("newBuilder").invoke(null);
            builder.getClass().getMethod("setHasAllSpraysFlag", boolean.class).invoke(builder, true);
            for (Method m : builder.getClass().getMethods()) {
                if (m.getName().equals("addAllEquippedSprays") && m.getParameterTypes().length == 1) {
                    m.invoke(builder, equipped);
                    break;
                }
            }
            return builder.getClass().getMethod("build").invoke(builder);
        } catch (Throwable t) {
            System.err.println("[LunarCookies] Error building spray LoginResponse: " + t);
            return null;
        }
    }

    public static void handleSprayLogin(Object controller, Object request, Object callback) {
        System.out.println("[LunarCookies] handleSprayLogin called!");
        try {
            ClassLoader cl = resolveClassLoader(callback);
            Object response = buildSprayLoginResponse(cl);
            runCallback(callback, response);
            System.out.println("[LunarCookies] Delivered spray LoginResponse to callback!");
        } catch (Throwable t) {
            Throwable cause = (t instanceof InvocationTargetException && t.getCause() != null) ? t.getCause() : t;
            System.err.println("[LunarCookies] Error handling spray login: " + cause);
            cause.printStackTrace();
        }
    }

    public static void handleUseSpray(Object controller, Object request, Object callback) {
        try {
            ClassLoader cl = resolveClassLoader(callback);
            Class<?> respClass = cl.loadClass("com.lunarclient.websocket.spray.v1.UseSprayResponse");
            Class<?> statusEnum = cl.loadClass("com.lunarclient.websocket.spray.v1.UseSprayResponse$Status");
            Object statusOk = Enum.valueOf((Class<Enum>) statusEnum, "STATUS_OK");

            Object builder = respClass.getMethod("newBuilder").invoke(null);
            builder.getClass().getMethod("setStatus", statusEnum).invoke(builder, statusOk);
            Object response = builder.getClass().getMethod("build").invoke(builder);

            runCallback(callback, response);
        } catch (Throwable t) {
            Throwable cause = (t instanceof InvocationTargetException && t.getCause() != null) ? t.getCause() : t;
            System.err.println("[LunarCookies] Error handling useSpray: " + cause);
            cause.printStackTrace();
        }
    }

    public static void handleUpdateEquippedSprays(Object controller, Object request, Object callback) {
        System.out.println("[LunarCookies] handleUpdateEquippedSprays called!");
        try {
            Method getList = request.getClass().getMethod("getEquippedSpraysList");
            List<?> sprays = (List<?>) getList.invoke(request);
            writeSavedEquippedSprays(sprays);
            System.out.println("[LunarCookies] Saved equipped sprays to disk!");

            if (callback != null) {
                ClassLoader cl = resolveClassLoader(callback);
                Class<?> respClass = cl.loadClass("com.lunarclient.websocket.spray.v1.UpdateEquippedSpraysResponse");
                Object defResp = respClass.getMethod("getDefaultInstance").invoke(null);
                runCallback(callback, defResp);
            }
        } catch (Throwable t) {
            Throwable cause = (t instanceof InvocationTargetException && t.getCause() != null) ? t.getCause() : t;
            System.err.println("[LunarCookies] Error handling updateEquippedSprays: " + cause);
            cause.printStackTrace();
        }
    }

    private static void runCallback(Object callback, Object response) throws Exception {
        if (callback == null) return;
        try {
            for (Method m : callback.getClass().getMethods()) {
                if (m.getName().equals("run") && m.getParameterTypes().length == 1) {
                    m.setAccessible(true);
                    m.invoke(callback, response);
                    return;
                }
            }
            Method runMethod = callback.getClass().getMethod("run", Object.class);
            runMethod.invoke(callback, response);
        } catch (Throwable t) {
            Throwable cause = (t instanceof InvocationTargetException && t.getCause() != null) ? t.getCause() : t;
            System.err.println("[LunarCookies] runCallback error: " + cause);
            cause.printStackTrace();
            throw (cause instanceof Exception) ? (Exception) cause : new RuntimeException(cause);
        }
    }

    // ---------------------------------------------------------
    // Persistence Helpers (Compatible with Prometheus files)
    // ---------------------------------------------------------

    private static Object readSavedOutfit(ClassLoader cl) {
        try {
            Class<?> outfitClass = cl.loadClass("com.lunarclient.websocket.cosmetic.v2.Outfit");
            Class<?> uuidClass = cl.loadClass("com.lunarclient.common.v1.Uuid");

            Object builder = outfitClass.getMethod("newBuilder").invoke(null);
            builder.getClass().getMethod("setName", String.class).invoke(builder, "Prometheus");
            builder.getClass().getMethod("setFavorite", boolean.class).invoke(builder, true);

            Object defaultUuid = null;
            try {
                Object uuidBuilder = uuidClass.getMethod("newBuilder").invoke(null);
                UUID randId = UUID.randomUUID();
                try {
                    uuidBuilder.getClass().getMethod("setHigh64", long.class).invoke(uuidBuilder, randId.getMostSignificantBits());
                    uuidBuilder.getClass().getMethod("setLow64", long.class).invoke(uuidBuilder, randId.getLeastSignificantBits());
                } catch (Throwable t1) {
                    for (Method m : uuidBuilder.getClass().getMethods()) {
                        if (m.getParameterTypes().length == 1 && m.getParameterTypes()[0] == long.class) {
                            if (m.getName().toLowerCase().contains("high")) m.invoke(uuidBuilder, randId.getMostSignificantBits());
                            if (m.getName().toLowerCase().contains("low")) m.invoke(uuidBuilder, randId.getLeastSignificantBits());
                        }
                    }
                }
                defaultUuid = uuidBuilder.getClass().getMethod("build").invoke(uuidBuilder);
                for (Method m : builder.getClass().getMethods()) {
                    if (m.getName().equals("setId") && m.getParameterTypes().length == 1
                            && m.getParameterTypes()[0].isAssignableFrom(uuidClass)) {
                        m.invoke(builder, defaultUuid);
                        break;
                    }
                }
            } catch (Throwable tUuid) {
                System.err.println("[LunarCookies] Error creating default outfit UUID: " + tUuid);
            }

            File f = getSavedFile("outfit.bin");
            if (f.exists() && f.length() > 0) {
                FileInputStream fis = new FileInputStream(f);
                try {
                    Method parseFrom = outfitClass.getMethod("parseFrom", InputStream.class);
                    Object parsed = parseFrom.invoke(null, fis);
                    if (parsed != null) {
                        for (Method m : builder.getClass().getMethods()) {
                            if (m.getName().equals("mergeFrom") && m.getParameterTypes().length == 1
                                    && m.getParameterTypes()[0].isAssignableFrom(outfitClass)) {
                                m.invoke(builder, parsed);
                                break;
                            }
                        }
                        // Ensure outfit has an ID if parsed lacked one
                        try {
                            Method hasId = parsed.getClass().getMethod("hasId");
                            if (!Boolean.TRUE.equals(hasId.invoke(parsed)) && defaultUuid != null) {
                                for (Method m : builder.getClass().getMethods()) {
                                    if (m.getName().equals("setId") && m.getParameterTypes().length == 1
                                            && m.getParameterTypes()[0].isAssignableFrom(uuidClass)) {
                                        m.invoke(builder, defaultUuid);
                                        break;
                                    }
                                }
                            }
                        } catch (Throwable ignored) {}
                    }
                } finally {
                    fis.close();
                }
            }
            return builder.getClass().getMethod("build").invoke(builder);
        } catch (Throwable t) {
            System.err.println("[LunarCookies] Error in readSavedOutfit: " + t);
            t.printStackTrace();
            return null;
        }
    }

    private static void writeSavedOutfit(Object outfit) {
        if (outfit == null) return;
        try {
            File f = getSavedFile("outfit.bin");
            if (f.getParentFile() != null) f.getParentFile().mkdirs();
            FileOutputStream fos = new FileOutputStream(f);
            try {
                Method writeTo = outfit.getClass().getMethod("writeTo", OutputStream.class);
                writeTo.invoke(outfit, fos);
            } finally {
                fos.close();
            }

            // Also mirror to %APPDATA%/.minecraft/prometheus/saved/outfit.bin if different
            try {
                String appdata = System.getenv("APPDATA");
                if (appdata != null && !appdata.trim().isEmpty()) {
                    File mcFile = new File(new File(appdata, ".minecraft"), "prometheus" + File.separator + "saved" + File.separator + "outfit.bin");
                    if (!mcFile.getCanonicalPath().equalsIgnoreCase(f.getCanonicalPath())) {
                        if (mcFile.getParentFile() != null) mcFile.getParentFile().mkdirs();
                        FileOutputStream fos2 = new FileOutputStream(mcFile);
                        try {
                            Method writeTo = outfit.getClass().getMethod("writeTo", OutputStream.class);
                            writeTo.invoke(outfit, fos2);
                        } finally {
                            fos2.close();
                        }
                    }
                }
            } catch (Throwable ignored) {}
        } catch (Throwable t) {
            System.err.println("[LunarCookies] Error writing saved outfit: " + t);
        }
    }

    private static int readSavedBadge() {
        File f = getSavedFile("badge.bin");
        if (f.exists() && f.length() > 0) {
            try {
                FileInputStream fis = new FileInputStream(f);
                try {
                    int b = fis.read();
                    return b >= 0 ? b : 0;
                } finally {
                    fis.close();
                }
            } catch (Throwable ignored) {}
        }
        return 0;
    }

    private static void writeSavedBadge(int badgeId) {
        try {
            File f = getSavedFile("badge.bin");
            if (f.getParentFile() != null) f.getParentFile().mkdirs();
            FileOutputStream fos = new FileOutputStream(f);
            try {
                fos.write(badgeId);
            } finally {
                fos.close();
            }

            try {
                String appdata = System.getenv("APPDATA");
                if (appdata != null && !appdata.trim().isEmpty()) {
                    File mcFile = new File(new File(appdata, ".minecraft"), "prometheus" + File.separator + "saved" + File.separator + "badge.bin");
                    if (!mcFile.getCanonicalPath().equalsIgnoreCase(f.getCanonicalPath())) {
                        if (mcFile.getParentFile() != null) mcFile.getParentFile().mkdirs();
                        FileOutputStream fos2 = new FileOutputStream(mcFile);
                        try {
                            fos2.write(badgeId);
                        } finally {
                            fos2.close();
                        }
                    }
                }
            } catch (Throwable ignored) {}
        } catch (Throwable ignored) {}
    }

    private static List<?> readSavedEquippedEmotes(ClassLoader cl) {
        List<Object> list = new ArrayList<Object>();
        File f = getSavedFile("emotes.bin");
        if (f.exists() && f.length() > 0) {
            try {
                Class<?> emoteClass = cl.loadClass("com.lunarclient.websocket.emote.v1.EquippedEmote");
                Method parseDelimited = emoteClass.getMethod("parseDelimitedFrom", InputStream.class);
                FileInputStream fis = new FileInputStream(f);
                try {
                    while (fis.available() > 0) {
                        Object emote = parseDelimited.invoke(null, fis);
                        if (emote != null) {
                            list.add(emote);
                        } else {
                            break;
                        }
                    }
                } finally {
                    fis.close();
                }
            } catch (Throwable ignored) {}
        }
        return list;
    }

    private static void writeSavedEquippedEmotes(List<?> emotes) {
        if (emotes == null) return;
        try {
            File f = getSavedFile("emotes.bin");
            if (f.getParentFile() != null) f.getParentFile().mkdirs();
            FileOutputStream fos = new FileOutputStream(f);
            try {
                for (Object emote : emotes) {
                    if (emote == null) continue;
                    Method writeDelimited = emote.getClass().getMethod("writeDelimitedTo", OutputStream.class);
                    writeDelimited.invoke(emote, fos);
                }
            } finally {
                fos.close();
            }

            try {
                String appdata = System.getenv("APPDATA");
                if (appdata != null && !appdata.trim().isEmpty()) {
                    File mcFile = new File(new File(appdata, ".minecraft"), "prometheus" + File.separator + "saved" + File.separator + "emotes.bin");
                    if (!mcFile.getCanonicalPath().equalsIgnoreCase(f.getCanonicalPath())) {
                        if (mcFile.getParentFile() != null) mcFile.getParentFile().mkdirs();
                        FileOutputStream fos2 = new FileOutputStream(mcFile);
                        try {
                            for (Object emote : emotes) {
                                if (emote == null) continue;
                                Method writeDelimited = emote.getClass().getMethod("writeDelimitedTo", OutputStream.class);
                                writeDelimited.invoke(emote, fos2);
                            }
                        } finally {
                            fos2.close();
                        }
                    }
                }
            } catch (Throwable ignored) {}
        } catch (Throwable ignored) {}
    }

    private static List<?> readSavedEquippedSprays(ClassLoader cl) {
        List<Object> list = new ArrayList<Object>();
        File f = getSavedFile("sprays.bin");
        if (f.exists() && f.length() > 0) {
            try {
                Class<?> sprayClass = cl.loadClass("com.lunarclient.websocket.spray.v1.EquippedSpray");
                Method parseDelimited = sprayClass.getMethod("parseDelimitedFrom", InputStream.class);
                FileInputStream fis = new FileInputStream(f);
                try {
                    while (fis.available() > 0) {
                        Object spray = parseDelimited.invoke(null, fis);
                        if (spray != null) {
                            list.add(spray);
                        } else {
                            break;
                        }
                    }
                } finally {
                    fis.close();
                }
            } catch (Throwable ignored) {}
        }
        return list;
    }

    private static void writeSavedEquippedSprays(List<?> sprays) {
        if (sprays == null) return;
        try {
            File f = getSavedFile("sprays.bin");
            if (f.getParentFile() != null) f.getParentFile().mkdirs();
            FileOutputStream fos = new FileOutputStream(f);
            try {
                for (Object spray : sprays) {
                    if (spray == null) continue;
                    Method writeDelimited = spray.getClass().getMethod("writeDelimitedTo", OutputStream.class);
                    writeDelimited.invoke(spray, fos);
                }
            } finally {
                fos.close();
            }

            try {
                String appdata = System.getenv("APPDATA");
                if (appdata != null && !appdata.trim().isEmpty()) {
                    File mcFile = new File(new File(appdata, ".minecraft"), "prometheus" + File.separator + "saved" + File.separator + "sprays.bin");
                    if (!mcFile.getCanonicalPath().equalsIgnoreCase(f.getCanonicalPath())) {
                        if (mcFile.getParentFile() != null) mcFile.getParentFile().mkdirs();
                        FileOutputStream fos2 = new FileOutputStream(mcFile);
                        try {
                            for (Object spray : sprays) {
                                if (spray == null) continue;
                                Method writeDelimited = spray.getClass().getMethod("writeDelimitedTo", OutputStream.class);
                                writeDelimited.invoke(spray, fos2);
                            }
                        } finally {
                            fos2.close();
                        }
                    }
                }
            } catch (Throwable ignored) {}
        } catch (Throwable ignored) {}
    }

    // ---------------------------------------------------------
    // Bytecode Class Patcher (pure Java, zero external dependencies)
    // ---------------------------------------------------------

    private static byte[] loadClassBytes(ClassLoader cl, String resourcePath) {
        try {
            InputStream is = cl.getResourceAsStream(resourcePath);
            if (is == null) is = cl.getResourceAsStream("/" + resourcePath);
            if (is == null) is = ClassLoader.getSystemResourceAsStream(resourcePath);
            if (is == null) {
                try {
                    String className = resourcePath.replace('/', '.');
                    if (className.endsWith(".class")) className = className.substring(0, className.length() - 6);
                    Class<?> loaded = cl.loadClass(className);
                    if (loaded != null && loaded.getProtectionDomain() != null && loaded.getProtectionDomain().getCodeSource() != null) {
                        java.net.URL url = loaded.getProtectionDomain().getCodeSource().getLocation();
                        if (url != null) {
                            java.util.jar.JarFile zf = new java.util.jar.JarFile(new File(url.toURI()));
                            try {
                                java.util.zip.ZipEntry ze = zf.getEntry(resourcePath);
                                if (ze != null) {
                                    InputStream zis = zf.getInputStream(ze);
                                    ByteArrayOutputStream bos = new ByteArrayOutputStream();
                                    byte[] buf = new byte[4096];
                                    int n;
                                    while ((n = zis.read(buf)) > 0) bos.write(buf, 0, n);
                                    zis.close();
                                    return bos.toByteArray();
                                }
                            } finally {
                                zf.close();
                            }
                        }
                    }
                } catch (Throwable ignored) {}
            }
            if (is == null) return null;
            ByteArrayOutputStream bos = new ByteArrayOutputStream();
            byte[] buf = new byte[4096];
            int n;
            while ((n = is.read(buf)) > 0) {
                bos.write(buf, 0, n);
            }
            is.close();
            return bos.toByteArray();
        } catch (Throwable t) {
            return null;
        }
    }

    private static byte[] patchStubClassBytes(byte[] data, String targetClassName, String[][] methodPatches) {
        try {
            int magic = readU4(data, 0);
            if (magic != 0xCAFEBABE) return null;

            int cpCount = readU2(data, 8);
            int offset = 10;

            String[] utf8Strings = new String[cpCount + methodPatches.length * 6 + 10];
            int i = 1;
            while (i < cpCount) {
                int tag = data[offset++] & 0xFF;
                if (tag == 1) { // Utf8
                    int len = readU2(data, offset);
                    offset += 2;
                    utf8Strings[i] = new String(data, offset, len, "UTF-8");
                    offset += len;
                } else if (tag == 3 || tag == 4) { // Int, Float
                    offset += 4;
                } else if (tag == 5 || tag == 6) { // Long, Double
                    offset += 8;
                    i++;
                } else if (tag == 7 || tag == 8) { // Class, String
                    offset += 2;
                } else if (tag == 9 || tag == 10 || tag == 11) { // Fieldref, Methodref, InterfaceMethodref
                    offset += 4;
                } else if (tag == 12) { // NameAndType
                    offset += 4;
                } else if (tag == 15) { // MethodHandle
                    offset += 3;
                } else if (tag == 16 || tag == 18) { // MethodType, InvokeDynamic
                    offset += 4;
                } else {
                    return null;
                }
                i++;
            }

            int cpEndOffset = offset;

            ByteArrayOutputStream newCpOut = new ByteArrayOutputStream();
            int curCp = cpCount;

            int helperUtf8Idx = curCp++;
            writeUtf8(newCpOut, targetClassName);

            int helperClassIdx = curCp++;
            writeClass(newCpOut, helperUtf8Idx);

            Map<String, Integer> methodRefMap = new HashMap<String, Integer>();
            for (int p = 0; p < methodPatches.length; p++) {
                String mName = methodPatches[p][0];
                String mDesc = methodPatches[p][1];
                String helperMethodName = methodPatches[p][2];

                int nameIdx = curCp++;
                writeUtf8(newCpOut, helperMethodName);

                int descIdx = curCp++;
                String helperDesc = (methodPatches[p].length > 3) ? methodPatches[p][3] : "(Ljava/lang/Object;Ljava/lang/Object;Ljava/lang/Object;)V";
                writeUtf8(newCpOut, helperDesc);

                int ntIdx = curCp++;
                writeNameAndType(newCpOut, nameIdx, descIdx);

                int mrefIdx = curCp++;
                writeMethodref(newCpOut, helperClassIdx, ntIdx);

                methodRefMap.put(mName + "|" + mDesc, mrefIdx);
            }

            ByteArrayOutputStream result = new ByteArrayOutputStream(data.length + 1024);
            // Header
            result.write(data, 0, 8);
            writeU2(result, curCp);
            // Original CP
            result.write(data, 10, cpEndOffset - 10);
            // Appended CP
            byte[] newCpBytes = newCpOut.toByteArray();
            result.write(newCpBytes, 0, newCpBytes.length);

            // Rest of class
            int rOff = cpEndOffset;
            int accessFlags = readU2(data, rOff); rOff += 2;
            int thisClass = readU2(data, rOff); rOff += 2;
            int superClass = readU2(data, rOff); rOff += 2;
            int ifCount = readU2(data, rOff); rOff += 2;
            rOff += ifCount * 2;

            int fieldsCount = readU2(data, rOff); rOff += 2;
            for (int f = 0; f < fieldsCount; f++) {
                rOff += 6;
                int fAttrCnt = readU2(data, rOff); rOff += 2;
                for (int a = 0; a < fAttrCnt; a++) {
                    rOff += 2;
                    int aLen = readU4(data, rOff); rOff += 4 + aLen;
                }
            }

            int methodsCount = readU2(data, rOff); rOff += 2;

            // Copy up to methods count
            result.write(data, cpEndOffset, rOff - cpEndOffset);

            for (int m = 0; m < methodsCount; m++) {
                int mStart = rOff;
                int mAcc = readU2(data, rOff); rOff += 2;
                int mName = readU2(data, rOff); rOff += 2;
                int mDesc = readU2(data, rOff); rOff += 2;
                int mAttrCnt = readU2(data, rOff); rOff += 2;

                String nameStr = utf8Strings[mName];
                String descStr = utf8Strings[mDesc];
                String key = nameStr + "|" + descStr;

                if (methodRefMap.containsKey(key)) {
                    int targetMref = methodRefMap.get(key);
                    writeU2(result, mAcc);
                    writeU2(result, mName);
                    writeU2(result, mDesc);
                    writeU2(result, mAttrCnt);

                    for (int a = 0; a < mAttrCnt; a++) {
                        int aStart = rOff;
                        int aName = readU2(data, rOff); rOff += 2;
                        int aLen = readU4(data, rOff); rOff += 4;
                        String attrName = utf8Strings[aName];
                        if ("Code".equals(attrName)) {
                            byte[] newCode = new byte[] {
                                0x2B, 0x2C, 0x2D,
                                (byte) 0xB8, (byte) ((targetMref >> 8) & 0xFF), (byte) (targetMref & 0xFF),
                                (byte) 0xB1
                            };
                            writeU2(result, aName);
                            writeU4(result, 12 + newCode.length);
                            writeU2(result, 3);
                            writeU2(result, 4);
                            writeU4(result, newCode.length);
                            result.write(newCode, 0, newCode.length);
                            writeU2(result, 0);
                            writeU2(result, 0);
                            rOff += aLen;
                        } else {
                            result.write(data, aStart, 6 + aLen);
                            rOff += aLen;
                        }
                    }
                } else {
                    for (int a = 0; a < mAttrCnt; a++) {
                        rOff += 2;
                        int aLen = readU4(data, rOff); rOff += 4 + aLen;
                    }
                    result.write(data, mStart, rOff - mStart);
                }
            }

            result.write(data, rOff, data.length - rOff);
            return result.toByteArray();
        } catch (Throwable t) {
            return null;
        }
    }

    private static int readU2(byte[] b, int off) {
        return ((b[off] & 0xFF) << 8) | (b[off + 1] & 0xFF);
    }

    private static int readU4(byte[] b, int off) {
        return ((b[off] & 0xFF) << 24) | ((b[off + 1] & 0xFF) << 16) | ((b[off + 2] & 0xFF) << 8) | (b[off + 3] & 0xFF);
    }

    private static void writeU2(ByteArrayOutputStream out, int val) {
        out.write((val >> 8) & 0xFF);
        out.write(val & 0xFF);
    }

    private static void writeU4(ByteArrayOutputStream out, int val) {
        out.write((val >> 24) & 0xFF);
        out.write((val >> 16) & 0xFF);
        out.write((val >> 8) & 0xFF);
        out.write(val & 0xFF);
    }

    private static void writeUtf8(ByteArrayOutputStream out, String s) throws Exception {
        byte[] bytes = s.getBytes("UTF-8");
        out.write(1);
        writeU2(out, bytes.length);
        out.write(bytes, 0, bytes.length);
    }

    private static void writeClass(ByteArrayOutputStream out, int nameIdx) {
        out.write(7);
        writeU2(out, nameIdx);
    }

    private static void writeNameAndType(ByteArrayOutputStream out, int nameIdx, int descIdx) {
        out.write(12);
        writeU2(out, nameIdx);
        writeU2(out, descIdx);
    }

    private static void writeMethodref(ByteArrayOutputStream out, int classIdx, int ntIdx) {
        out.write(10);
        writeU2(out, classIdx);
        writeU2(out, ntIdx);
    }
}
