#!/usr/bin/env bash
# Ядро для стенда: то же, что собирает core/build.ps1 (версия sing-box, правки из core/patches, теги сборки,
# версия Go), только для Linux. Всё нужное читается из core/build.ps1, чтобы сборки не разошлись.
#   build-core.sh <папка>   положит туда sing-box; пересобирает, только если изменились build.ps1 или правки
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
CORE="$(cd "$HERE/../../core" && pwd)"
OUT="$1"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"

value() { # $Имя = '...' из build.ps1
  sed -n "s/^\$$1 = '\(.*\)'\r\?$/\1/p" "$CORE/build.ps1" | head -1
}
TAG="$(value Tag)"; VERSION="$(value Version)"; TAGS="$(value Tags)"
TOOLCHAIN="$(sed -n "s/^\$env:GOTOOLCHAIN = '\(.*\)'\r\?$/\1/p" "$CORE/build.ps1" | head -1)"
# Модули с правками: строки вида 'wireguard-go' = 'github.com/sagernet/wireguard-go'
MODULES="$(sed -n "s/^ *'\([a-z0-9-]*\)' *= *'\(github\.com[^']*\)'\r\?$/\1=\2/p" "$CORE/build.ps1")"
for name in TAG VERSION TAGS TOOLCHAIN MODULES; do
  [ -n "${!name}" ] || { echo "Не удалось прочитать $name из core/build.ps1" >&2; exit 1; }
done

# Отпечаток входов: если он не изменился и ядро уже собрано, сборка не нужна
STAMP="$( (cat "$CORE/build.ps1"; find "$CORE/patches" -name '*.patch' | sort | xargs cat) | tr -d '\r' | sha256sum | cut -c1-16)"
if [ -x "$OUT/sing-box" ] && [ "$(cat "$OUT/inputs" 2>/dev/null)" = "$STAMP" ]; then
  echo "Ядро $VERSION уже собрано: $OUT/sing-box"
  exit 0
fi

export GOTOOLCHAIN="$TOOLCHAIN" CGO_ENABLED=0 GOFLAGS=
SRC="$OUT/src"; MODS="$OUT/modules"
rm -rf "$SRC" "$MODS" "$OUT/sing-box" "$OUT/inputs"
mkdir -p "$MODS"
git clone -q --depth 1 --branch "$TAG" https://github.com/SagerNet/sing-box.git "$SRC"

apply() { # правки из папки $1 к исходникам в $2, по порядку имён; концы строк как в Git
  for patch in $(find "$1" -maxdepth 1 -name '*.patch' | sort); do
    tr -d '\r' < "$patch" > "$OUT/current.patch"
    git -C "$2" apply "$OUT/current.patch"
    echo "Применена $(basename "$patch")"
  done
}

apply "$CORE/patches" "$SRC"
cd "$SRC"
for line in $MODULES; do
  name="${line%%=*}"; path="${line#*=}"
  dir="$(go mod download -json "$path" | python3 -c 'import json,sys; print(json.load(sys.stdin)["Dir"])')"
  cp -r "$dir" "$MODS/$name"
  chmod -R u+w "$MODS/$name"
  git init -q "$MODS/$name"
  echo "$path:"
  apply "$CORE/patches/$name" "$MODS/$name"
  go mod edit "-replace=$path=$MODS/$name"
done
go build -trimpath -tags "$TAGS" \
  -ldflags "-X github.com/sagernet/sing-box/constant.Version=$VERSION -X runtime.godebugDefault=multipathtcp=0,tlssha1=1 -checklinkname=0 -s -w -buildid=" \
  -o "$OUT/sing-box" ./cmd/sing-box
echo "$STAMP" > "$OUT/inputs"
"$OUT/sing-box" version | head -1
