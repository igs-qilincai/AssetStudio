#!/bin/sh
# Builds the native helpers used by AssetStudioWeb as one WebAssembly ES module:
#   - Texture2DDecoderNative (DXT/BC, ETC/EAC, ASTC, PVRTC, ATC, crunch)
#   - LZ4 / LZMA bundle decompression (wasm/compression.c, LZMA SDK in wasm/lzma)
# Requires Emscripten (em++/emcc on PATH): https://emscripten.org/docs/getting_started/downloads.html
# Output: AssetStudioWeb/wwwroot/native/assetstudio-native.{js,wasm}
set -e
cd "$(dirname "$0")"
OUT="../AssetStudioWeb/wwwroot/native"
OBJ="bin/wasm-obj"
mkdir -p "$OUT" "$OBJ"

emcc -O3 -c wasm/compression.c -o "$OBJ/compression.o"
emcc -O3 -c wasm/lzma/LzmaDec.c -o "$OBJ/LzmaDec.o"

em++ -std=c++17 -O3 -I. -Wno-c++11-narrowing -Wno-everything \
  astc.cpp atc.cpp bcn.cpp crunch.cpp etc.cpp pvrtc.cpp unitycrunch.cpp wasm/wasm_api.cpp \
  "$OBJ/compression.o" "$OBJ/LzmaDec.o" \
  -sMODULARIZE=1 -sEXPORT_ES6=1 -sEXPORT_NAME=createNativeModule \
  -sENVIRONMENT=web,worker -sALLOW_MEMORY_GROWTH=1 -sMAXIMUM_MEMORY=4GB -sINITIAL_MEMORY=32MB \
  -sEXPORTED_RUNTIME_METHODS=HEAPU8,HEAPU32 -sFILESYSTEM=0 -sASSERTIONS=0 \
  -o "$OUT/assetstudio-native.js"

echo "Built $OUT/assetstudio-native.js"
