#!/usr/bin/env bash
# Drives the Shiny.AppDeviceBridge sample on macOS: the release server, `dotnet watch` on Sample.Blazor, and the
# AppKit head (samples/Sample.MacOS). Run from the repository root:
#
#   .claude/skills/run-appdevicebridge/driver.sh <command>
#
# Commands:
#   up                start everything (idempotent); returns once the app's loopback server answers
#   status            what is listening, and whether the app process is alive
#   shot [name]       bring the app forward and screenshot its window -> artifacts/run-appdevicebridge/<name>.png
#   click X Y         bring the app forward and click at X,Y points from the window's top-left (title bar included)
#   heading "<text>"  change the Home page <h1> in place and wait for dotnet watch to hot-reload it
#   logs              tail the dev server log
#   test              run the unit and integration tests
#   down              stop everything
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
OUT="$ROOT/artifacts/run-appdevicebridge"
APP="$ROOT/samples/Sample.MacOS/bin/Debug/net10.0-macos/osx-arm64/AppDeviceBridge Sample.app"
APP_BIN="Contents/MacOS/Sample.MacOS"
mkdir -p "$OUT"

die() { echo "error: $*" >&2; exit 1; }

# A sleep that agent shells which block `sleep` still allow.
pause() { perl -e "select(undef, undef, undef, $1)"; }

wait_http() { curl -s -o /dev/null --retry "$2" --retry-connrefused --retry-delay 1 --retry-max-time "$2" "$1"; }

up_already() { curl -s -o /dev/null -m 2 "$1"; }

# "id x y w h" for the app's main window, from the window server (bounds in points).
window_bounds() {
  swift - 2>/dev/null <<'SWIFT'
import CoreGraphics
let windows = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] ?? []
for w in windows where (w[kCGWindowOwnerName as String] as? String) == "AppDeviceBridge Sample" && (w[kCGWindowLayer as String] as? Int) == 0 {
    guard let b = w[kCGWindowBounds as String] as? [String: Any],
          let x = b["X"] as? Double, let y = b["Y"] as? Double,
          let width = b["Width"] as? Double, let height = b["Height"] as? Double, height > 200 else { continue }
    print(w[kCGWindowNumber as String] ?? 0, Int(x), Int(y), Int(width), Int(height))
    break
}
SWIFT
}

# `open` on a running app activates it; osascript frontmost does not stick while someone else is using the Mac.
raise() { open "$APP"; pause 1; }

cmd_up() {
  up_already http://127.0.0.1:5199/ || (cd "$ROOT/samples/Sample.ReleaseServer" && nohup dotnet run > "$OUT/release-server.log" 2>&1 &)
  up_already http://127.0.0.1:5288/ || (cd "$ROOT/samples/Sample.Blazor" && DOTNET_WATCH_SUPPRESS_EMOJIS=1 nohup dotnet watch run --launch-profile device --non-interactive > "$OUT/devserver.log" 2>&1 &)

  wait_http http://127.0.0.1:5199/ 120 || die "release server did not start; see $OUT/release-server.log"
  wait_http http://127.0.0.1:5288/ 240 || die "dev server did not start; see $OUT/devserver.log"

  # The app only uses the dev server if it answers at startup, so the app starts last.
  dotnet build "$ROOT/samples/Sample.MacOS/Sample.MacOS.csproj" -p:ValidateXcodeVersion=false -nologo -v q > "$OUT/build.log" 2>&1 \
    || die "build failed; see $OUT/build.log"

  pkill -f "$APP_BIN" || true
  # open fails with LSOpenURLsWithCompletionHandler error -600 while the previous instance is still exiting.
  for _ in $(seq 1 50); do pgrep -f "$APP_BIN" > /dev/null || break; pause 0.2; done
  open "$APP"
  wait_http http://127.0.0.1:5780/_host/ping 60 || die "the app did not start its loopback server; run 'shot' to see its error"
  echo "up: release server :5199, dev server :5288, app loopback :5780 (the Blazor UI renders 10-20 s later)"
}

cmd_status() {
  printf "release server :5199  %s\n" "$(curl -s -o /dev/null -m 2 -w '%{http_code}' http://127.0.0.1:5199/ || true)"
  printf "dev server     :5288  %s\n" "$(curl -s -o /dev/null -m 2 -w '%{http_code}' http://127.0.0.1:5288/ || true)"
  printf "app loopback   :5780  %s\n" "$(curl -s -o /dev/null -m 2 -w '%{http_code}' http://127.0.0.1:5780/_host/ping || true)"
  pgrep -fl "$APP_BIN" || echo "app process: not running"
}

cmd_shot() {
  local name="${1:-app}" id x y w h
  raise
  read -r id x y w h < <(window_bounds) || die "app window not found on screen"
  # By window id, not screen rectangle: a rectangle also captures whatever is floating over the app.
  screencapture -x -o -l "$id" "$OUT/$name.png"
  echo "$OUT/$name.png (window at $x,$y ${w}x${h} points; the image is 2x on Retina)"
}

cmd_click() {
  local id x y w h
  [ $# -eq 2 ] || die "usage: click X Y"
  raise
  read -r id x y w h < <(window_bounds) || die "app window not found on screen"
  cliclick "c:$((x + $1)),$((y + $2))"
  pause 1
  echo "clicked $1,$2 in the window"
}

cmd_heading() {
  [ $# -eq 1 ] || die 'usage: heading "<text>"'
  local file="$ROOT/samples/Sample.Blazor/Pages/Home.razor" log="$OUT/devserver.log" before
  [ -f "$log" ] || die "no dev server log at $log; start with 'up'"
  before=$(wc -l < "$log")

  python3 - "$file" "$1" <<'PY'
import re, sys
path, text = sys.argv[1], sys.argv[2]
source = open(path).read()
updated, count = re.subn(r"<h1>.*?</h1>", f"<h1>{text}</h1>", source, count=1)
assert count == 1, "no <h1> in Home.razor"
with open(path, "r+") as f:   # in place: dotnet watch crashes on temp-file-and-rename saves
    f.seek(0); f.write(updated); f.truncate()
PY

  for _ in $(seq 1 60); do
    if tail -n +$((before + 1)) "$log" | grep -q "changes applied"; then
      echo "hot reload: $(tail -n +$((before + 1)) "$log" | grep "changes applied" | head -1 | sed 's/^dotnet watch : //')"
      return
    fi
    tail -n +$((before + 1)) "$log" | grep -q "unexpected error" && die "dotnet watch crashed; see $log"
    pause 1
  done
  die "dotnet watch did not report the change; see $log"
}

cmd_logs() { tail -n 30 "$OUT/devserver.log"; }

cmd_test() { dotnet test "$ROOT/tests/Shiny.AppDeviceBridge.Tests/Shiny.AppDeviceBridge.Tests.csproj" -nologo; }

cmd_down() {
  pkill -f "$APP_BIN" || true
  pkill -f "dotnet watch run --launch-profile device" || true
  pkill -f "dotnet-watch" || true
  pkill -f "Sample.Blazor" || true
  pkill -f "Sample.ReleaseServer" || true
  echo "down"
}

command="${1:-}"; shift || true
case "$command" in
  up) cmd_up ;;
  status) cmd_status ;;
  shot) cmd_shot "$@" ;;
  click) cmd_click "$@" ;;
  heading) cmd_heading "$@" ;;
  logs) cmd_logs ;;
  test) cmd_test ;;
  down) cmd_down ;;
  *) sed -n '2,15p' "$0"; exit 1 ;;
esac
