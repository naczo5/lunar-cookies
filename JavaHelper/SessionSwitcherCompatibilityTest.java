package com.lunarcookies;

import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.util.Optional;
import java.util.UUID;

/**
 * Dependency-free ABI smoke test for the legacy and modern session constructor
 * shapes. Run with assertions enabled after compiling beside SessionSwitcher.
 */
public final class SessionSwitcherCompatibilityTest {
    public static final class LegacySession {
        final String name;
        final String uuid;
        final String token;
        final String type;

        public LegacySession(String name, String uuid, String token, String type) {
            this.name = name;
            this.uuid = uuid;
            this.token = token;
            this.type = type;
        }

        public String getUsername() { return name; }
        public String getAccessToken() { return token; }
    }

    public static final class ModernSession {
        final String name;
        final UUID uuid;
        final String token;
        final Optional<String> xuid;
        final Optional<String> clientId;

        public ModernSession(
                String name,
                UUID uuid,
                String token,
                Optional<String> xuid,
                Optional<String> clientId) {
            this.name = name;
            this.uuid = uuid;
            this.token = token;
            this.xuid = xuid;
            this.clientId = clientId;
        }

        public String getName() { return name; }
        public String getAccessToken() { return token; }
    }

    private SessionSwitcherCompatibilityTest() {}

    public static void main(String[] args) throws Exception {
        Method create = SessionSwitcher.class.getDeclaredMethod(
                "createSession", String.class, String.class, String.class);
        create.setAccessible(true);
        Field sessionClass = SessionSwitcher.class.getDeclaredField("sessionClass");
        sessionClass.setAccessible(true);

        String compactUuid = "12345678123456781234567812345678";

        sessionClass.set(null, LegacySession.class);
        LegacySession legacy = (LegacySession) create.invoke(
                null, "LegacyUser", compactUuid, "legacy-token");
        require("LegacyUser".equals(legacy.name), "legacy username");
        require(compactUuid.equals(legacy.uuid), "legacy UUID");
        require("legacy-token".equals(legacy.token), "legacy token");
        require("mojang".equals(legacy.type), "legacy account type");

        sessionClass.set(null, ModernSession.class);
        ModernSession modern = (ModernSession) create.invoke(
                null, "ModernUser", compactUuid, "modern-token");
        require("ModernUser".equals(modern.name), "modern username");
        require("12345678-1234-5678-1234-567812345678".equals(modern.uuid.toString()),
                "modern UUID normalization");
        require("modern-token".equals(modern.token), "modern token");
        require(!modern.xuid.isPresent() && !modern.clientId.isPresent(),
                "modern optional identifiers");

        System.out.println("SessionSwitcher compatibility tests passed.");
    }

    private static void require(boolean condition, String label) {
        if (!condition) throw new AssertionError(label);
    }
}
