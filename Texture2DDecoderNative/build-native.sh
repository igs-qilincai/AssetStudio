#!/bin/sh
# Builds libTexture2DDecoderNative for macOS (.dylib) or Linux (.so) with clang/gcc.
# Usage: ./build-native.sh [output-dir]   (default: ./bin/native)
set -e
cd "$(dirname "$0")"
OUT="${1:-bin/native}"
mkdir -p "$OUT"

case "$(uname -s)" in
  Darwin) LIB="libTexture2DDecoderNative.dylib"; EXTRA="-arch arm64 -arch x86_64" ;;
  *)      LIB="libTexture2DDecoderNative.so";    EXTRA="" ;;
esac

${CXX:-c++} -std=c++17 -O2 -shared -fPIC -fvisibility=hidden -Wno-c++11-narrowing -I. -D_T2D_DLL $EXTRA \
  astc.cpp atc.cpp bcn.cpp crunch.cpp dllmain.cpp etc.cpp pvrtc.cpp unitycrunch.cpp \
  -o "$OUT/$LIB"

echo "Built $OUT/$LIB"
