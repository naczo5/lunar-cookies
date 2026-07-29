package com.lunarcookies;

import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.util.Arrays;
import java.util.Comparator;
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
 * class used by official-name 26.1/26.2 clients.
 */
public final class SessionSwitcher {
    private static Object launchSession;
    private static Object minecraftInstance;
    private static Field sessionField;
    private static Class<?> sessionClass;
    private static Method getUsername;
    private static Method getPlayerId;
    private static Field playerField;
    private static Field worldField;
    private static Method scheduleMethod;
    private static String hintedMcClass;
    private static String hintedSessionClass;
    private static volatile boolean ready;

    private SessionSwitcher() {}

    /** Optional dot-name hints supplied by native JVMTI discovery. */
    public static synchronized void hint(String mcClassName, String sessionClassName) {
        if (mcClassName != null && !mcClassName.isEmpty()) hintedMcClass = mcClassName;
        if (sessionClassName != null && !sessionClassName.isEmpty()) hintedSessionClass = sessionClassName;
        ready = false;
    }

    public static synchronized String init() {
        try {
            if (ready) return "ok";

            Class<?> mcClass = findMinecraftClass();
            if (mcClass == null) return "Minecraft class not found";

            minecraftInstance = findMinecraftInstance(mcClass);
            if (minecraftInstance == null) return "Minecraft instance is null";

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

    public static synchronized String getSessionInfo() {
        String initResult = init();
        if (!ready) return "error|" + initResult + "|false|false";
        try {
            return "ok|" + readUsername() + "|" + readUuid() + "|"
                    + isInWorld() + "|true";
        } catch (Throwable t) {
            return "error|" + describeThrowable(t) + "|false|false";
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
}
