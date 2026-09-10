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

    public static final class ForgeSrgSession {
        final String name;
        final String uuid;
        final String token;
        final String type;

        public ForgeSrgSession(String name, String uuid, String token, String type) {
            this.name = name;
            this.uuid = uuid;
            this.token = token;
            this.type = type;
        }

        public String func_111285_a() { return name; }
        public String func_148254_d() { return token; }
        public String func_148255_b() { return uuid; }
    }

    public static final class Notch189Session {
        final String name;
        final String uuid;
        final String token;
        final String type;

        public Notch189Session(String name, String uuid, String token, String type) {
            this.name = name;
            this.uuid = uuid;
            this.token = token;
            this.type = type;
        }

        public String c() { return name; }
        public String d() { return token; }
        public String b() { return uuid; }
    }

    public static final class MockForgeMinecraft {
        private ForgeSrgSession field_71449_j;
    }

    public static final class MockNotchMinecraft {
        private Notch189Session ae;
    }

    private SessionSwitcherCompatibilityTest() {}

    public static void main(String[] args) throws Exception {
        Method create = SessionSwitcher.class.getDeclaredMethod(
                "createSession", String.class, String.class, String.class);
        create.setAccessible(true);
        Field sessionClass = SessionSwitcher.class.getDeclaredField("sessionClass");
        sessionClass.setAccessible(true);
        Method looksLikeSession = SessionSwitcher.class.getDeclaredMethod(
                "looksLikeSessionClass", Class.class);
        looksLikeSession.setAccessible(true);
        Method findNamedSession = SessionSwitcher.class.getDeclaredMethod(
                "findNamedSessionField", Class.class);
        findNamedSession.setAccessible(true);

        String compactUuid = "12345678123456781234567812345678";

        // Validate Forge SRG session
        require(Boolean.TRUE.equals(looksLikeSession.invoke(null, ForgeSrgSession.class)),
                "Forge SRG session structural validation");
        sessionClass.set(null, ForgeSrgSession.class);
        ForgeSrgSession srg = (ForgeSrgSession) create.invoke(
                null, "SrgUser", compactUuid, "srg-token");
        require("SrgUser".equals(srg.name), "Forge SRG username");
        require(compactUuid.equals(srg.uuid), "Forge SRG UUID");
        require("srg-token".equals(srg.token), "Forge SRG token");
        require("mojang".equals(srg.type), "Forge SRG account type");

        // Validate 1.8.9 Notch session
        require(Boolean.TRUE.equals(looksLikeSession.invoke(null, Notch189Session.class)),
                "1.8.9 Notch session structural validation");
        sessionClass.set(null, Notch189Session.class);
        Notch189Session notch = (Notch189Session) create.invoke(
                null, "NotchUser", compactUuid, "notch-token");
        require("NotchUser".equals(notch.name), "Notch username");
        require(compactUuid.equals(notch.uuid), "Notch UUID");
        require("notch-token".equals(notch.token), "Notch token");
        require("mojang".equals(notch.type), "Notch account type");

        // Validate named field discovery on Forge & Notch Minecraft owners
        Field srgField = (Field) findNamedSession.invoke(null, MockForgeMinecraft.class);
        require(srgField != null && "field_71449_j".equals(srgField.getName()),
                "find field_71449_j on mock Forge Minecraft");

        Field notchField = (Field) findNamedSession.invoke(null, MockNotchMinecraft.class);
        require(notchField != null && "ae".equals(notchField.getName()),
                "find ae on mock Notch Minecraft");

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

        String invalidHost = SessionSwitcher.joinServer(" ", 25565);
        require(invalidHost.startsWith("error:invalid_address"), "blank host rejected");
        String invalidPort = SessionSwitcher.joinServer("localhost", 0);
        require(invalidPort.startsWith("error:invalid_address"), "port 0 rejected");
        String missingClient = SessionSwitcher.joinServer("localhost", 25565);
        require(missingClient.startsWith("error:"), "join without Minecraft fails closed");
        require(!missingClient.startsWith("error:invalid_address"),
                "uninitialized client is not reported as a bad address");

        System.out.println("SessionSwitcher compatibility tests passed.");
    }

    private static void require(boolean condition, String label) {
        if (!condition) throw new AssertionError(label);
    }
}
