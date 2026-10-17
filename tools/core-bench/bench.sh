#!/usr/bin/env bash
# Стенд ядра VPN лаунчера на Linux. Описание: README.md рядом.
#   sudo bash tools/core-bench/bench.sh                  все шаги
#   sudo bash tools/core-bench/bench.sh mixed hide-dns   только эти шаги
#   bash tools/core-bench/bench.sh --list                список шагов
# Рабочая папка: $CORE_BENCH_WORK, по умолчанию /tmp/core-bench. В репозитории появляются только папки bin и obj
# сборки .NET (они в .gitignore).
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
export CORE_BENCH_WORK="${CORE_BENCH_WORK:-/tmp/core-bench}"
WORK="$CORE_BENCH_WORK"

if [ "${1:-}" = "--list" ]; then
  exec python3 -I "$HERE/run.py" --list
fi
[ "$(id -u)" = 0 ] || { echo "Нужны права root: стенд создаёт сетевые пространства и адаптер TUN." >&2; exit 1; }
for tool in git go ip python3; do
  command -v "$tool" >/dev/null || { echo "Не найдено: $tool (см. README.md, «Что нужно»)." >&2; exit 1; }
done
mkdir -p "$WORK/bin"

# Ядро из core/ репозитория
bash "$HERE/build-core.sh" "$WORK/core"

# Тестовые серверы AmneziaWG и WireGuard: свой amneziawg-go каждой версии, исходник общий
for dir in "$HERE"/awgserver/v*/; do
  version="$(basename "$dir")"
  bin="$WORK/bin/awgserver-$version"
  stamp="$(cat "$HERE/awgserver/main.go" "$dir/go.mod" "$dir/go.sum" | sha256sum | cut -c1-16)"
  [ -x "$bin" ] && [ "$(cat "$bin.inputs" 2>/dev/null)" = "$stamp" ] && continue
  build="$WORK/awgserver-$version"
  rm -rf "$build"; mkdir -p "$build"
  cp "$dir/go.mod" "$dir/go.sum" "$build/"
  module="$(sed -n 's/^require \(github\.com\/amnezia-vpn\/amneziawg-go[^ ]*\) .*/\1/p' "$dir/go.mod")"
  sed "s#github.com/amnezia-vpn/amneziawg-go/#$module/#" "$HERE/awgserver/main.go" > "$build/main.go"
  (cd "$build" && GOTOOLCHAIN="$(sed -n 's/^go \(.*\)/go\1/p' go.mod | sed 's/^go\(1\.[0-9]*\)$/go\1.0/')" CGO_ENABLED=0 GOFLAGS= go build -o "$bin" .)
  echo "$stamp" > "$bin.inputs"
  echo "Собран сервер amneziawg-go $version"
done

# Программы на .NET (проверка подключения как в лаунчере, настройки ядра от самой службы): без .NET 8 SDK
# их шаги пропускаются
if command -v dotnet >/dev/null; then
  for project in probe cfgcheck; do
    dotnet build "$HERE/$project" -c Release -o "$WORK/$project" -p:EnableWindowsTargeting=true -nologo -v q >/dev/null || { echo "Не собран $project" >&2; exit 1; }
  done
else
  echo "dotnet не найден: шаги check-via-core-* и service-config будут пропущены."
fi

exec python3 -I "$HERE/run.py" "$WORK/core/sing-box" "$@"
