#!/bin/bash
set -e
cd "$(dirname "$0")"

mkdir -p JavaHelper/out
javac -source 1.8 -target 1.8 -d JavaHelper/out JavaHelper/SessionSwitcher.java

python3 -c '
with open("JavaHelper/out/com/lunarcookies/SessionSwitcher.class", "rb") as f:
    data = f.read()
lines = ["// Auto-generated - do not edit", "static const unsigned char kSessionSwitcherClassBytes[] = {"]
for i in range(0, len(data), 12):
    chunk = data[i:i+12]
    hex_str = ", ".join(f"0x{b:02X}" for b in chunk)
    if i + 12 < len(data):
        hex_str += ","
    lines.append("    " + hex_str)
lines.append("};")
lines.append(f"static const unsigned int kSessionSwitcherClassLen = {len(data)};\n")
with open("Bridge/session_helper_bytes.inc", "w") as f:
    f.write("\n".join(lines))
'

JAVA_INC="/usr/lib/jvm/java-21-openjdk-amd64/include"
if [ ! -d "$JAVA_INC" ]; then
    JAVA_INC=$(find /usr/lib/jvm -name "jni.h" -printf '%h\n' | head -n 1)
fi

x86_64-w64-mingw32-g++ -m64 -std=c++17 -shared -O2 -fno-exceptions -fno-rtti \
  -o Bridge/switcher.dll Bridge/bridge.cpp \
  -I"$JAVA_INC" -I"$JAVA_INC/linux" -IBridge \
  -static -static-libgcc -static-libstdc++ -lws2_32 -Wl,--kill-at

cp -f Bridge/switcher.dll LunarCookies/switcher.dll
echo "Successfully built Bridge/switcher.dll and copied to LunarCookies/switcher.dll"
