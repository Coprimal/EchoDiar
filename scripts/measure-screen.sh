#!/usr/bin/env bash
#
# measure-screen.sh - Phase 1 hard gate of the screen recording plan (docs/plans/2026-11-23-screen-recording):
# drives the DEBUG `--measure-screen` harness (EchoDiar/ScreenHarness.swift) on this Mac, one recorder at a time,
# on the built-in display only, and prints one table plus one verdict line.
#
# Usage:
#   scripts/measure-screen.sh preflight
#       Build Debug, then run the harness preflight (grants, built-in 2x display, stage, a 2 s capture, a 3 s engine
#       probe whose beep must reach the system channel). Output: ~/EchoDiarQA/screen-2026-11-23/preflight/.
#   scripts/measure-screen.sh gate
#       Build Debug, delete every stored gate result, measure G1 to G6 and D1 to D3 (about 40 minutes, about 8 of
#       them with the stage covering the built-in display) and print the table and the verdict.
#   scripts/measure-screen.sh gate --only <ids>
#       Re-measure only the named IDs (G1..G6, D1..D3; comma or space separated), keep the stored result.json of the
#       others, and print the merged verdict over all IDs. Shared runs belong to one owner and are re-taken only when
#       that owner is named: the 15 fps window runs (and the 0.60/0.85 quality rows) belong to G1, the Screenshot
#       baselines of meeting and video to G2, the pause run to D1 and D2. G2 and G6 are always recomputed from the
#       stored files. The non-gating rows (static and scroll at 30 fps with their Screenshot baselines, meeting at
#       60 fps) are taken by the full gate only. Exit 5 or 6 in any run is a D1 miss: `--only D1` re-takes an ng-*
#       run, and (unless G1 is named too, which re-takes them anyway) a 0.60/0.85 quality row, whose stored result is
#       a harness exit 5 or 6.
#
#   Results: ~/EchoDiarQA/screen-2026-11-23/gate/<run>/result.json and gate/table.md. Every video, audio and image
#   file and every <out>/stage/ copy is deleted at the end of the run that made it, and again on any exit
#   (failure, Ctrl-C): only the result.json files and the table are kept.
#
#   Exit codes: 0 GATE: PASS, 1 GATE: FAIL, 2 GATE: INVALID, 3 GATE: CHECK (the first kind on the verdict line
#   decides), 4 user stop (another EchoDiar runs, no built-in display or lid closed, a missing grant: fix it, re-run),
#   5 script error (no verdict was reached). `preflight`: 0 ok, 4 user stop, 1 another preflight failure.
#
set -euo pipefail

# ---------------------------------------------------------------------------------------------------------------
# Fixed by the plan (README "Gate"): never change a threshold, the preset, a scenario, the clip or the run length.

RUN_SECONDS=60              # every recording run
PRESET_FPS=15
PRESET_QUALITY=0.75
G1_MAX_MB_PER_MIN=15        # meeting, window and display mode, each <=
G2_MIN_RATIO=10             # Screenshot MB/min / ours, meeting and video, each >=
BASELINE_MIN_SECONDS=55     # a Screenshot baseline shorter than this is INVALID (G2, G6)
G3_MIN_SSIM=0.999           # SSIM All of the static frame at 25 s against the lossless PNG
G3_AT_SECONDS=25
G3_FRAME_WINDOW=1           # INVALID when no frame within this many seconds after 25 s
G4_MAX_ADDED_CPU=20         # median added CPU over audio-only, points of one core, <=
G4_MAX_DELTA_SPREAD=10      # INVALID when the three deltas differ by more than this
G4_MAX_OTHER_MOVE=20        # INVALID when a process outside the list moved by more than this within a pair
G4_INFO_MOVE=2              # other processes that moved by at least this are listed (information only)
G4_TOP_SAMPLES=62           # top -l 62 -s 1
G4_ENCODER_DELAY=5          # VTEncoderXPCService is re-resolved this many seconds into the run
G4_PAIRS=3
G5_KILL_AT=35               # kill -9 this many seconds after the video file appeared
G5_MIN_DURATION=20          # ffprobe duration of the killed file, >=
D1_PAUSE_AT=20
D1_RESUME_AT=30
D2_MAX_DIFF=0.1             # |videoDuration - audioDuration| <=
D3_FPS=60
D3_MAX_OFFSET=0.117         # 0.1 s plus one frame interval at 60 fps
SILENCE_DB=-40              # system channel max_volume must be above this (D3); silencedetect noise floor
SILENCE_END_IGNORE=0.5      # ffmpeg prints a silence_end at end of file even for silence

# Instrument choices of this script (not from the plan; changing one is an instrument fix, never a threshold change).

D1_SIDE_MARGIN=1            # frames must be at least this far before and after the pause point (file time)
G4_EDGE_SAMPLES=2           # G4 coverage: the samples with the harness pid present (top's first sample excluded,
                            # it has no CPU interval) times top's own sample period, taken from its per-sample
                            # date lines, must reach RUN_SECONDS minus this many periods minus G4_STAMP_RESOLUTION.
                            # The 2 are top's first sample, at the run's start, and the sample in which the harness
                            # exits. A sample takes about 1.25 s here (1 s sleep plus the time top needs to read
                            # every process), so the 62 samples of the pinned command span about 77 s and cover a
                            # 60 s run with about 47 (top starts when the session folder appears, as before).
G4_STAMP_RESOLUTION=1       # seconds: top's date lines have 1 s resolution, so the measured period (and the coverage)
                            # can be off by up to this much over the run
SILENCE_MIN_SECONDS=0.05    # silencedetect d=
AREA_TARGET="area:120,120,1200,720"   # G1 area run: points, top-left, inside the built-in display
BASELINE_STAGE_SECONDS=75   # a --stage-only window outlives the 60 s Screenshot recording
CLIP_CHECK_SECONDS=4        # meeting and video: the harness checks the clip for 3 s after WINDOWID
START_TIMEOUT=60            # seconds to wait for WINDOWID / the session / the video file

ROOT="$HOME/EchoDiarQA/screen-2026-11-23"
GATE_DIR="$ROOT/gate"
PREFLIGHT_DIR="$ROOT/preflight"
TABLE="$GATE_DIR/table.md"
ALL_IDS="G1 G2 G3 G4 G5 G6 D1 D2 D3"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
STAGE="$REPO_ROOT/scripts/fixtures/screen-stage.html"
CLIP="$REPO_ROOT/docs/showcase/echodiar-intro.mp4"
cd "$REPO_ROOT"

EXIT_USER_STOP=4
EXIT_SCRIPT_ERROR=5

usage() { awk 'NR > 2 && /^#/ { sub(/^# ?/, ""); print; next } NR > 2 { exit }' "${BASH_SOURCE[0]}"; }
die() { echo "error: $*" >&2; exit "$EXIT_SCRIPT_ERROR"; }

# ---------------------------------------------------------------------------------------------------------------
# Background processes, cleanup

HPID="" TPID="" SPID="" NPID=""   # harness, top, screencapture, nap
VERDICT_DONE=0            # 1 once the GATE line (or the preflight result) was printed
USER_STOP=0

# scrub <dir>: delete everything in a run folder except result.json (videos, audio, images, the stage copy, the
# session folder, the harness's isolated settings, logs).
scrub() {
  [[ -n "$1" && -d "$1" ]] || return 0
  find "$1" -mindepth 1 -maxdepth 1 ! -name result.json -exec rm -rf {} + 2>/dev/null || true
}

scrub_all() {
  local dir
  if [[ -d "$GATE_DIR" ]]; then
    for dir in "$GATE_DIR"/*/; do [[ -d "$dir" ]] && scrub "${dir%/}"; done
  fi
  scrub "$PREFLIGHT_DIR"
  # Safety net: no media file may survive anywhere under the script's folders.
  find "$GATE_DIR" "$PREFLIGHT_DIR" -type f \( -name '*.mp4' -o -name '*.mov' -o -name '*.caf' -o -name '*.m4a' \
    -o -name '*.wav' -o -name '*.png' \) -delete 2>/dev/null || true
}

stop_children() {
  local pid
  for pid in $HPID $TPID $SPID $NPID; do kill -TERM "$pid" 2>/dev/null || true; done
  for pid in $HPID $TPID $SPID $NPID; do
    local i=0
    while kill -0 "$pid" 2>/dev/null && ((i < 20)); do sleep 0.1; i=$((i + 1)); done
    kill -KILL "$pid" 2>/dev/null || true
  done
  HPID="" TPID="" SPID="" NPID=""
}

on_exit() {
  local status=$?
  trap - EXIT INT TERM
  stop_children
  scrub_all
  if ((VERDICT_DONE == 0 && USER_STOP == 0 && status != 130 && status != 143)); then
    echo "error: no verdict: the script stopped early (status $status)" >&2
    status=$EXIT_SCRIPT_ERROR
  fi
  exit "$status"
}

user_stop() {
  USER_STOP=1
  echo >&2
  echo "STOP (user action needed): $*" >&2
  exit "$EXIT_USER_STOP"
}

# ---------------------------------------------------------------------------------------------------------------
# Build and harness runs

# Same commands as scripts/measure-transcribe.sh (default DerivedData). Sets APP and BIN.
build_debug() {
  echo "==> Building (Debug)..."
  xcodegen generate
  xcodebuild build -project EchoDiar.xcodeproj -scheme EchoDiar -configuration Debug -quiet
  local products
  products="$(xcodebuild -project EchoDiar.xcodeproj -scheme EchoDiar -configuration Debug \
    -showBuildSettings 2>/dev/null \
    | awk -F' = ' '!found && /^ *BUILT_PRODUCTS_DIR = /{print $2; found=1}')"
  APP="$products/EchoDiar.app"
  BIN="$APP/Contents/MacOS/EchoDiar"
  [[ -x "$BIN" ]] || die "built app not found at $APP"
}

require_tools() {
  [[ -f "$STAGE" ]] || die "missing stage page $STAGE"
  [[ -f "$CLIP" ]] || die "missing clip $CLIP"
  local tool
  for tool in xcodegen xcodebuild ffmpeg ffprobe jq screencapture top pgrep; do
    command -v "$tool" >/dev/null 2>&1 || die "$tool is not installed"
  done
  [[ -x /usr/bin/python3 ]] || die "/usr/bin/python3 is missing"
}

# launch <dir> <harness args...>: a fresh run folder, the harness in the background (HPID), output in harness.log.
# The preset fps and quality are passed explicitly unless the run sets its own.
launch() {
  local dir="$1" preset=()
  shift
  [[ " $* " == *" --fps "* ]] || preset+=(--fps "$PRESET_FPS")
  [[ " $* " == *" --quality "* ]] || preset+=(--quality "$PRESET_QUALITY")
  rm -rf "$dir"
  mkdir -p "$dir"
  "$BIN" --background --measure-screen "$@" ${preset[@]+"${preset[@]}"} --out "$dir" --stage "$STAGE" --clip "$CLIP" \
    >"$dir/harness.log" 2>&1 &
  HPID=$!
}

# collect <dir>: waits for the harness, sets STATUS, prints its output; exits 4 on a user stop.
collect() {
  local dir="$1"
  STATUS=0
  wait "$HPID" || STATUS=$?
  HPID=""
  sed 's/^/    /' "$dir/harness.log" 2>/dev/null || true
  echo "    exit $STATUS"
  case "$STATUS" in
    2) user_stop "another EchoDiar is running: quit it (never killed by this script), then re-run" ;;
    3) user_stop "no built-in Retina display (lid closed?) or the stage is not on it: open the lid, then re-run" ;;
  esac
}

# record_status <dir> [json]: result.json gets .measure.status (the real exit status) and the extra fields; a run
# that wrote no result.json (killed, crashed) gets a minimal one.
record_status() {
  local dir="$1" extra="${2:-}" file="$1/result.json"
  [[ -n "$extra" ]] || extra='{}'
  if [[ ! -s "$file" ]]; then
    jq -n --argjson s "$STATUS" '{exitCode: $s, error: "no result.json from the harness"}' >"$file"
  fi
  jq --argjson s "$STATUS" --argjson m "$extra" '.measure = ((.measure // {}) + {status: $s} + $m)' "$file" \
    >"$file.tmp" && mv "$file.tmp" "$file"
}

# wait_for <seconds> <command...>: polls every 0.2 s while the harness lives; 0 once the command succeeds.
wait_for() {
  local limit=$(($1 * 5)) i=0
  shift
  while ((i < limit)); do
    "$@" && return 0
    kill -0 "$HPID" 2>/dev/null || return 1
    sleep 0.2
    i=$((i + 1))
  done
  return 1
}

# nap <seconds>: a sleep that a TERM to this script interrupts at once (a foreground sleep would delay the trap).
nap() {
  sleep "$1" &
  NPID=$!
  wait "$NPID" || true
  NPID=""
}

window_id() { awk '/^WINDOWID [0-9]+/ { print $2; exit }' "$1/harness.log" 2>/dev/null; }
has_window_id() { [[ -n "$(window_id "$1")" ]]; }
# The engine's session folder (depth 2, never the stage copy).
session_file() { find "$1" -mindepth 2 -maxdepth 2 -name "$2" -not -path "$1/stage/*" 2>/dev/null | head -n 1; }
has_session() { [[ -n "$(session_file "$1" session.json)" ]]; }
has_video() { [[ -n "$(session_file "$1" video.mp4)$(session_file "$1" video.mov)" ]]; }

ff_duration() { ffprobe -v error -show_entries format=duration -of csv=p=0 "$1" 2>/dev/null | head -n 1; }
file_bytes() { stat -f %z "$1" 2>/dev/null || echo 0; }
json_num() { if [[ "$1" =~ ^-?[0-9]+([.][0-9]+)?$ ]]; then echo "$1"; else echo null; fi; }

# run_plain <name> <scenario> <target> [flags...]: a 60 s recording, nothing measured outside result.json.
run_plain() {
  local name="$1" scenario="$2" target="$3" dir="$GATE_DIR/$1"
  shift 3
  echo "==> $name: $scenario --target $target --seconds $RUN_SECONDS $*"
  launch "$dir" "$scenario" --target "$target" --seconds "$RUN_SECONDS" "$@"
  collect "$dir"
  record_status "$dir"
  scrub "$dir"
}

# run_baseline <name> <scenario>: macOS Screenshot's window recording of a --stage-only window (never -R).
run_baseline() {
  local name="$1" scenario="$2" dir="$GATE_DIR/$1" wid file sc_status=-1 duration bytes
  echo "==> $name: Screenshot baseline, $scenario --stage-only, screencapture -v -V $RUN_SECONDS -x -l<WID>"
  launch "$dir" "$scenario" --target window --seconds "$BASELINE_STAGE_SECONDS" --stage-only
  file="$dir/baseline.mov"
  if wait_for "$START_TIMEOUT" has_window_id "$dir"; then
    wid="$(window_id "$dir")"
    if [[ "$scenario" == meeting || "$scenario" == video ]]; then nap "$CLIP_CHECK_SECONDS"; fi
    if kill -0 "$HPID" 2>/dev/null; then
      sc_status=0
      screencapture -v -V "$RUN_SECONDS" -x -l"$wid" "$file" >/dev/null 2>&1 &
      SPID=$!
      wait "$SPID" || sc_status=$?
      SPID=""
    fi
  fi
  collect "$dir"
  duration="$(json_num "$(ff_duration "$file")")"
  bytes="$(file_bytes "$file")"
  echo "    screencapture exit $sc_status, $bytes bytes, ffprobe duration $duration s"
  record_status "$dir" "$(jq -n --argjson sc "$sc_status" --argjson d "$duration" --argjson b "$bytes" \
    '{screencaptureStatus: $sc, baselineDuration: $d, baselineBytes: $b,
      baselineMbPerMin: (if ($d != null and $d > 0) then $b / 1000000 / ($d / 60) else null end)}')"
  scrub "$dir"
}

# ---------------------------------------------------------------------------------------------------------------
# Python helpers (/usr/bin/python3 -I: system Python, no site packages, nothing read from the current directory)

# top_py <top output> <harness pid> <listed pids, comma separated> <encoder pids, comma separated>
top_py() {
  /usr/bin/python3 -I - "$@" <<'PY'
import json, re, sys
from datetime import datetime

path, harness, listed, encoders = sys.argv[1], int(sys.argv[2]), sys.argv[3], sys.argv[4]
listed = {int(p) for p in listed.split(",") if p}
encoders = sorted({int(p) for p in encoders.split(",") if p})
listed.add(harness)
row = re.compile(r"^\s*(\d+)\s+(.*\S)\s+([\d.]+)\s+([\d.]+)\s+(\S+)\s*$")
stamp = re.compile(r"^\d{4}/\d\d/\d\d \d\d:\d\d:\d\d\s*$")  # one date line per sample, 1 s resolution
samples, current, times = [], None, []
with open(path, errors="replace") as f:
    for line in f:
        if stamp.match(line):
            times.append(datetime.strptime(line.strip(), "%Y/%m/%d %H:%M:%S").timestamp())
            continue
        if line.startswith("PID") and "COMMAND" in line:
            current = {}
            samples.append(current)
            continue
        m = row.match(line) if current is not None else None
        if m:
            current[int(m.group(1))] = (m.group(2), float(m.group(3)))
samples = samples[1:]  # top's first sample has no CPU interval
used = [s for s in samples if harness in s]
# Seconds per sample, from top's own clock over all its samples; the used samples cover len(used) of them.
period = (times[-1] - times[0]) / (len(times) - 1) if len(times) >= 2 and times[-1] > times[0] else None
out = {"topSamples": len(samples), "samples": len(used), "harnessPid": harness, "encoderPids": encoders,
       "samplePeriod": round(period, 3) if period else None,
       "covered": round(len(used) * period, 2) if period else None,
       "listed": {}, "others": {}, "cpuSum": None}
if used:
    n = len(used)
    for pid in sorted(listed):
        cpu = sum(s[pid][1] for s in used if pid in s) / n
        name = next((s[pid][0] for s in used if pid in s), None)
        out["listed"][str(pid)] = {"command": name, "cpu": round(cpu, 2)}
    out["cpuSum"] = round(sum(v["cpu"] for v in out["listed"].values()), 2)
    others = {}
    for s in used:
        for pid, (name, cpu) in s.items():
            if pid not in listed:
                others[name] = others.get(name, 0.0) + cpu
    out["others"] = {k: round(v / n, 2) for k, v in sorted(others.items()) if v / n >= 0.1}
print(json.dumps(out))
PY
}

# d3_py <yavg log> <silencedetect log> <audio duration> <first frame file time> <max_volume or null>
d3_py() {
  /usr/bin/python3 -I - "$@" <<'PY'
import json, re, statistics, sys

yavg_path, silence_path, audio_duration, first_file_time, max_volume = sys.argv[1:6]
audio_duration = float(audio_duration) if audio_duration not in ("", "null") else None
first_file_time = float(first_file_time) if first_file_time not in ("", "null") else None
max_volume = float(max_volume) if max_volume not in ("", "null") else None
FRACTION, IGNORE = 1 / 3, float(sys.argv[6])

frames, pts = [], None
with open(yavg_path, errors="replace") as f:
    for line in f:
        m = re.search(r"pts_time:\s*(-?[\d.]+)", line)
        if m:
            pts = float(m.group(1))
            continue
        m = re.search(r"lavfi\.signalstats\.YAVG=([\d.]+)", line)
        if m and pts is not None:
            frames.append((pts, float(m.group(1))))
            pts = None
flashes = []
median = None
if frames and first_file_time is not None:
    median = statistics.median(y for _, y in frames)
    base = frames[0][0]  # ffmpeg rebases the first frame to 0: map back onto the file clock
    previous_dark = False
    for t, y in frames:
        dark = y < median * FRACTION
        if dark and not previous_dark:
            flashes.append(round(first_file_time + (t - base), 4))
        previous_dark = dark
beeps = []
with open(silence_path, errors="replace") as f:
    for line in f:
        m = re.search(r"silence_end:\s*(-?[\d.]+)", line)
        if m:
            t = float(m.group(1))
            if audio_duration is None or t < audio_duration - IGNORE:
                beeps.append(round(t, 4))
offsets = []
if beeps:
    for t in flashes:
        nearest = min(beeps, key=lambda b: abs(b - t))
        offsets.append(round(t - nearest, 4))
print(json.dumps({
    "frames": len(frames), "medianYavg": median, "flashes": flashes, "beeps": beeps, "offsets": offsets,
    "maxOffset": max((abs(o) for o in offsets), default=None), "maxVolume": max_volume,
}))
PY
}

# verdict_py <gate dir> <table file>: the table and the verdict line from the stored result.json files.
verdict_py() {
  G1_MAX_MB_PER_MIN="$G1_MAX_MB_PER_MIN" G2_MIN_RATIO="$G2_MIN_RATIO" BASELINE_MIN_SECONDS="$BASELINE_MIN_SECONDS" \
  G3_MIN_SSIM="$G3_MIN_SSIM" G3_FRAME_WINDOW="$G3_FRAME_WINDOW" G4_MAX_ADDED_CPU="$G4_MAX_ADDED_CPU" \
  G4_MAX_DELTA_SPREAD="$G4_MAX_DELTA_SPREAD" G4_MAX_OTHER_MOVE="$G4_MAX_OTHER_MOVE" G4_INFO_MOVE="$G4_INFO_MOVE" \
  G4_EDGE_SAMPLES="$G4_EDGE_SAMPLES" G4_PAIRS="$G4_PAIRS" G5_MIN_DURATION="$G5_MIN_DURATION" RUN_SECONDS="$RUN_SECONDS" \
  G4_STAMP_RESOLUTION="$G4_STAMP_RESOLUTION" \
  D1_SIDE_MARGIN="$D1_SIDE_MARGIN" D2_MAX_DIFF="$D2_MAX_DIFF" D3_MAX_OFFSET="$D3_MAX_OFFSET" \
  SILENCE_DB="$SILENCE_DB" PRESET_FPS="$PRESET_FPS" D3_FPS="$D3_FPS" \
  /usr/bin/python3 -I - "$@" <<'PY'
import json, os, statistics, sys, traceback

# An uncaught error must never look like a verdict (exit 1 = FAIL): exit 9 instead.
sys.excepthook = lambda *e: (traceback.print_exception(*e), os._exit(9))
gate, table_path = sys.argv[1], sys.argv[2]
E = os.environ
G1_MAX, G2_MIN, BASE_MIN = float(E["G1_MAX_MB_PER_MIN"]), float(E["G2_MIN_RATIO"]), float(E["BASELINE_MIN_SECONDS"])
G3_MIN, G3_WINDOW = float(E["G3_MIN_SSIM"]), float(E["G3_FRAME_WINDOW"])
G4_MAX, G4_SPREAD, G4_OTHER, G4_INFO = (float(E[k]) for k in
                                        ("G4_MAX_ADDED_CPU", "G4_MAX_DELTA_SPREAD", "G4_MAX_OTHER_MOVE", "G4_INFO_MOVE"))
G4_EDGE, G4_PAIRS, RUN_SECONDS = int(E["G4_EDGE_SAMPLES"]), int(E["G4_PAIRS"]), float(E["RUN_SECONDS"])
G4_STAMP = float(E["G4_STAMP_RESOLUTION"])
G5_MIN, D1_MARGIN, D2_MAX, D3_MAX = (float(E[k]) for k in ("G5_MIN_DURATION", "D1_SIDE_MARGIN", "D2_MAX_DIFF",
                                                            "D3_MAX_OFFSET"))
SILENCE_DB = float(E["SILENCE_DB"])
FPS, D3_FPS = E["PRESET_FPS"], E["D3_FPS"]
IDS = ["G1", "G2", "G3", "G4", "G5", "G6", "D1", "D2", "D3"]

G1_RUNS = ["g1-window-static", "g1-window-slides", "g1-window-scroll", "g1-window-meeting", "g1-window-video",
           "g1-display-meeting", "g1-area-static"]
G1_GATED = ["g1-window-meeting", "g1-display-meeting"]
QUALITY_RUNS = ["q060-window-meeting", "q085-window-meeting"]
G6_RUNS = [("g6-window-meeting-20", "base-meeting"), ("g6-window-meeting-30", "base-meeting"),
           ("g6-window-video-20", "base-video"), ("g6-window-video-30", "base-video")]
NG_RUNS = [("ng-window-static-30", "base-static"), ("ng-window-scroll-30", "base-scroll"),
           ("ng-window-meeting-60", None)]
G4_RUNS = [(f"g4-{i}-audio", f"g4-{i}-video") for i in range(1, G4_PAIRS + 1)]
G5_RUNS = ["g5-kill-mp4", "g5-kill-mov"]
ALL_RUNS = (G1_RUNS + QUALITY_RUNS + ["base-meeting", "base-video"] + [r for r, _ in G6_RUNS]
            + ["ng-window-static-30", "ng-window-scroll-30", "base-static", "base-scroll", "ng-window-meeting-60"]
            + ["g3-ref", "g3-window-static"] + [r for pair in G4_RUNS for r in pair] + G5_RUNS
            + ["d12-pause-scroll", "d3-sync-meeting"])


def load(name):
    try:
        with open(os.path.join(gate, name, "result.json")) as f:
            return json.load(f)
    except (OSError, ValueError):
        return None


R = {name: load(name) for name in ALL_RUNS}


def m(r):
    return (r or {}).get("measure") or {}


def status(r):
    if r is None:
        return None
    s = m(r).get("status", r.get("exitCode"))
    return s if isinstance(s, int) else None


def num(value):
    return value if isinstance(value, (int, float)) and not isinstance(value, bool) else None


def ok(r):
    return status(r) == 0


def why(name, r):
    if r is None:
        return f"{name}: not measured"
    error = r.get("error") or f"exit {status(r)}"
    return f"{name}: {error}"


def fmt(value, digits=2):
    if value is None:
        return "-"
    if isinstance(value, float):
        return f"{value:.{digits}f}"
    return str(value)


rows = []


def row(id_, label, r=None, value=None, limit="", result="", note="", digits=2, mb=None, video=None):
    frames = "-"
    if r and num(r.get("frames")) is not None:
        frames = f"{r['frames']}/{fmt(num(r.get('expectedFrames')), 0)}"
    rows.append([id_, label, fmt(status(r)) if r is not None else "-", frames,
                 fmt(num((r or {}).get("dropped"))),
                 fmt(mb if mb is not None else num((r or {}).get("mbPerMin"))),
                 fmt(video if video is not None else num((r or {}).get("videoDuration"))),
                 fmt(num((r or {}).get("audioDuration"))),
                 fmt(value, digits) if not isinstance(value, str) else value, limit, result, note])


verdict = {}

# D1 part 1: any run that ended with harness exit 5 or 6.
frame_misses = [name for name in ALL_RUNS if status(R[name]) in (5, 6)]


def run_valid(name):
    return ok(R[name])


# G1 -------------------------------------------------------------------------------------------------------------
g1_invalid = [why(n, R[n]) for n in G1_RUNS if not run_valid(n) or (n in G1_GATED and num(R[n].get("mbPerMin")) is None)]
g1_fail = False
for name in G1_RUNS:
    r = R[name]
    if name in G1_GATED:
        value = num((r or {}).get("mbPerMin")) if ok(r) else None
        res = "INVALID" if value is None else ("PASS" if value <= G1_MAX else "FAIL")
        g1_fail |= res == "FAIL"
        row("G1", f"{name} ({FPS} fps)", r, value, f"<= {G1_MAX:g}", res, "" if ok(r) else why(name, r))
    else:
        row("G1", f"{name} ({FPS} fps)", r, None, "", "info", "" if ok(r) else why(name, r))
for name in QUALITY_RUNS:
    row("info", f"{name} (task 6.6)", R[name], None, "", "info", "" if ok(R[name]) else why(name, R[name]))
# A threshold missed on a valid run is a FAIL even when another run of the ID is INVALID.
verdict["G1"] = "FAIL" if g1_fail else ("INVALID" if g1_invalid else "PASS")


# G2 -------------------------------------------------------------------------------------------------------------
def baseline_problem(name):
    r = R[name]
    if r is None:
        return f"{name}: not measured"
    if not ok(r):
        return why(name, r)
    x = m(r)
    if x.get("screencaptureStatus") != 0:
        return f"{name}: screencapture exit {x.get('screencaptureStatus')}"
    d = num(x.get("baselineDuration"))
    if d is None or not num(x.get("baselineBytes")):
        return f"{name}: baseline missing or unreadable"
    if d < BASE_MIN:
        return f"{name}: baseline {d:.1f} s < {BASE_MIN:g} s"
    return None


def baseline_row(id_, name):
    r = R[name]
    problem = baseline_problem(name)
    row(id_, f"{name} (Screenshot, window)", r, None, f">= {BASE_MIN:g} s", "INVALID" if problem else "ok",
        problem or "", mb=num(m(r).get("baselineMbPerMin")), video=num(m(r).get("baselineDuration")))
    return problem


g2_invalid, g2_fail = [], False
for scenario in ("meeting", "video"):
    base, ours = f"base-{scenario}", f"g1-window-{scenario}"
    problem = baseline_row("G2", base)
    ratio = None
    ours_mb = num((R[ours] or {}).get("mbPerMin")) if ok(R[ours]) else None
    if problem:
        g2_invalid.append(problem)
    if ours_mb is None or ours_mb <= 0:
        g2_invalid.append(why(ours, R[ours]) if not ok(R[ours]) else f"{ours}: no MB/min")
    elif not problem:
        ratio = num(m(R[base]).get("baselineMbPerMin")) / ours_mb
    res = "INVALID" if ratio is None else ("PASS" if ratio >= G2_MIN else "FAIL")
    g2_fail |= res == "FAIL"
    row("G2", f"{scenario}: Screenshot / {ours}", None, ratio, f">= {G2_MIN:g}", res, "", digits=1)
verdict["G2"] = "FAIL" if g2_fail else ("INVALID" if g2_invalid else "PASS")

# G6 -------------------------------------------------------------------------------------------------------------
g6_invalid, g6_fail = [], False
for name, base in G6_RUNS:
    r = R[name]
    problem = baseline_problem(base)
    base_mb = None if problem else num(m(R[base]).get("baselineMbPerMin"))
    value = num((r or {}).get("mbPerMin")) if ok(r) else None
    if problem:
        g6_invalid.append(problem)
    if value is None:
        g6_invalid.append(why(name, r) if not ok(r) else f"{name}: no MB/min")
    res = "INVALID" if value is None or base_mb is None else ("PASS" if value < base_mb else "FAIL")
    g6_fail |= res == "FAIL"
    row("G6", name, r, value, f"< {fmt(base_mb)} ({base})", res, "" if ok(r) else why(name, r))
verdict["G6"] = "FAIL" if g6_fail else ("INVALID" if g6_invalid else "PASS")

# Non-gating rows --------------------------------------------------------------------------------------------------
for name, base in NG_RUNS:
    note = "" if ok(R[name]) else why(name, R[name])
    if base:
        problem = baseline_row("info", base)
        base_mb = None if problem else num(m(R[base]).get("baselineMbPerMin"))
        row("info", name, R[name], None, f"vs {fmt(base_mb)} ({base})", "info", note)
    else:
        row("info", f"{name} (60 fps warning, task 6.6)", R[name], None, "", "info", note)

# G3 -------------------------------------------------------------------------------------------------------------
ref, rec = R["g3-ref"], R["g3-window-static"]
g3_problems = []
if ref is None or not ok(ref):
    g3_problems.append(why("g3-ref", ref))
elif m(ref).get("refStatus") != 0 or not num(m(ref).get("refWidth")):
    g3_problems.append(f"g3-ref: screencapture exit {m(ref).get('refStatus')}, no readable ref.png")
row("G3", "g3-ref (screencapture -o -l, lossless)", ref, f"{fmt(num(m(ref).get('refWidth')))}x"
    f"{fmt(num(m(ref).get('refHeight')))}", "", "", "")
if rec is None or not ok(rec):
    g3_problems.append(why("g3-window-static", rec))
gap = num(m(rec).get("frameGap"))
ssim = num(m(rec).get("ssim"))
if rec is not None and ok(rec):
    if gap is None or gap > G3_WINDOW:
        g3_problems.append(f"g3-window-static: no frame within {G3_WINDOW:g} s of 25 s (gap {fmt(gap)})")
    if ssim is None:
        g3_problems.append("g3-window-static: no SSIM line")
res = "INVALID" if g3_problems else ("PASS" if ssim >= G3_MIN else "FAIL")
row("G3", f"g3-window-static ({FPS} fps, --no-cursor), SSIM All at 25 s", rec, ssim, f">= {G3_MIN:g}", res,
    "; ".join(g3_problems) or f"frame {fmt(num(m(rec).get('frameAt')), 3)} s", digits=5)
verdict["G3"] = res

# G4 -------------------------------------------------------------------------------------------------------------
g4_problems, deltas, moved_info, encoder_in_process = [], [], [], False
for audio, video in G4_RUNS:
    pair_ok = True
    for name in (audio, video):
        r = R[name]
        x = m(r)
        note, valid = "", False
        if r is None or not ok(r):
            g4_problems.append(why(name, r))
            pair_ok = False
        elif num(x.get("cpuSum")) is None or num(x.get("samplePeriod")) is None or num(x.get("covered")) is None:
            g4_problems.append(f"{name}: top output unparsable ({x.get('samples')} samples with the harness, "
                               f"sample period {x.get('samplePeriod')})")
            pair_ok = False
        elif x["covered"] < RUN_SECONDS - G4_EDGE * x["samplePeriod"] - G4_STAMP:
            g4_problems.append(f"{name}: top covered {x['covered']:g} s ({x.get('samples')} samples x "
                               f"{x['samplePeriod']:g} s), needs >= {RUN_SECONDS:g} - {G4_EDGE} x "
                               f"{x['samplePeriod']:g} - {G4_STAMP:g} s")
            pair_ok = False
        else:
            valid = True
        listed = x.get("listed") or {}
        note = ", ".join(f"{v.get('command') or p} {fmt(num(v.get('cpu')), 1)}" for p, v in listed.items())
        if name == video and valid and not x.get("encoderPids"):
            note += "; encoder in process"
            encoder_in_process = True
        row("G4", f"{name} (display, meeting{', --no-video' if name == audio else ''})", r, num(x.get("cpuSum")),
            "", "", note, digits=1)
    if not pair_ok:
        continue
    a, v = m(R[audio]), m(R[video])
    delta = v["cpuSum"] - a["cpuSum"]
    deltas.append(delta)
    oa, ov = a.get("others") or {}, v.get("others") or {}
    big = []
    for name in sorted(set(oa) | set(ov)):
        move = ov.get(name, 0.0) - oa.get(name, 0.0)
        if abs(move) > G4_OTHER:
            big.append(f"{name} {move:+.1f}")
        elif abs(move) >= G4_INFO:
            moved_info.append(f"{audio[:4]} {name} {move:+.1f}")
    if big:
        g4_problems.append(f"{audio[:4]}: other processes moved > {G4_OTHER:g}: " + ", ".join(big))
    row("G4", f"{audio[:4]} delta (video - audio)", None, delta, "", "", ", ".join(big), digits=1)
median = statistics.median(deltas) if len(deltas) == G4_PAIRS else None
if median is not None and max(deltas) - min(deltas) > G4_SPREAD:
    g4_problems.append(f"deltas differ by {max(deltas) - min(deltas):.1f} > {G4_SPREAD:g}")
res = "INVALID" if g4_problems or median is None else ("PASS" if median <= G4_MAX else "FAIL")
note = "; ".join(g4_problems)
if encoder_in_process:
    note = (note + "; " if note else "") + "encoder in process"
if moved_info:
    note = (note + "; " if note else "") + "moved >= 2 (info): " + ", ".join(moved_info)
row("G4", "median added CPU (points of one core)", None, median, f"<= {G4_MAX:g}", res, note, digits=1)
verdict["G4"] = res

# G5 -------------------------------------------------------------------------------------------------------------
g5 = []
for name in G5_RUNS:
    r = R[name]
    d = num(m(r).get("ffprobeDuration"))
    if r is None or status(r) != 137:
        res, note = "INVALID", why(name, r) if r is not None else f"{name}: not measured"
    else:
        res, note = ("PASS" if d is not None and d >= G5_MIN else "FAIL"), ("" if d is not None else "unreadable")
    g5.append(res)
    row("G5", f"{name} (kill -9 at {fmt(num(m(r).get('killedAt')), 0)} s)", r, d, f">= {G5_MIN:g} s", res, note)
verdict["G5"] = "PASS" if "PASS" in g5 else ("INVALID" if "INVALID" in g5 else "FAIL")

# D1, D2 ---------------------------------------------------------------------------------------------------------
p = R["d12-pause-scroll"]
s = status(p)
first, last = num((p or {}).get("firstFrameFileTime")), num((p or {}).get("lastFrameFileTime"))
start, pause_at = num((p or {}).get("videoStartFileTime")), num((p or {}).get("pauseAt"))
pause_point = start + pause_at if start is not None and pause_at is not None else None
d1_note = ""
if p is None:
    d1, d1_note = "INVALID", "d12-pause-scroll: not measured"
elif s in (5, 6):
    d1, d1_note = "CHECK", why("d12-pause-scroll", p)
elif s != 0:
    d1, d1_note = "INVALID", why("d12-pause-scroll", p)
elif None in (first, last, pause_point):
    d1, d1_note = "INVALID", "no frame or pause times in result.json"
elif first <= pause_point - D1_MARGIN and last >= pause_point + D1_MARGIN:
    d1 = "PASS"
else:
    d1, d1_note = "CHECK", "no frames on both sides of the pause"
row("D1", "d12-pause-scroll: first / pause point / last frame (file s)", p,
    f"{fmt(first)} / {fmt(pause_point)} / {fmt(last)}", f"±{D1_MARGIN:g} s, frames >= 90%", d1, d1_note)
if frame_misses:
    d1 = "CHECK"
    row("D1", "runs with harness exit 5 or 6", None, len(frame_misses), "0", "CHECK", ", ".join(frame_misses))
verdict["D1"] = d1
vd, ad = num((p or {}).get("videoDuration")), num((p or {}).get("audioDuration"))
diff = abs(vd - ad) if vd is not None and ad is not None else None
if p is None or s != 0 or diff is None:
    d2 = "INVALID"
else:
    d2 = "PASS" if diff <= D2_MAX else "CHECK"
row("D2", "d12-pause-scroll: abs(video - audio) (s)", p, diff, f"<= {D2_MAX:g}", d2,
    "" if p is not None and s == 0 else why("d12-pause-scroll", p), digits=3)
verdict["D2"] = d2

# D3 -------------------------------------------------------------------------------------------------------------
r = R["d3-sync-meeting"]
x = m(r)
problems = []
if r is None or not ok(r):
    problems.append(why("d3-sync-meeting", r))
else:
    vol = num(x.get("maxVolume"))
    if vol is None or vol <= SILENCE_DB:
        problems.append(f"system channel max_volume {fmt(vol, 1)} dB <= {SILENCE_DB:g}")
    if not x.get("flashes"):
        problems.append("no flash")
    if not x.get("beeps"):
        problems.append("no beep")
offset = num(x.get("maxOffset"))
d3 = "INVALID" if problems or offset is None else ("PASS" if offset <= D3_MAX else "CHECK")
note = "; ".join(problems) or (f"flashes {x.get('flashes')}, beeps {x.get('beeps')}, offsets {x.get('offsets')}, "
                               f"max_volume {fmt(num(x.get('maxVolume')), 1)} dB")
row("D3", f"d3-sync-meeting ({D3_FPS} fps, --sync-marks): max abs(flash - beep) (s)", r, offset, f"<= {D3_MAX:g}", d3,
    note, digits=3)
verdict["D3"] = d3

# Output -----------------------------------------------------------------------------------------------------------
head = ["ID", "Row", "Exit", "Frames", "Dropped", "MB/min", "Video s", "Audio s", "Value", "Limit", "Result", "Note"]
lines = ["| " + " | ".join(head) + " |", "|" + "|".join("---" for _ in head) + "|"]
lines += ["| " + " | ".join(str(c).replace("|", "/") for c in r) + " |" for r in rows]
lines += [f"| {i} | verdict |  |  |  |  |  |  |  |  | {verdict[i]} |  |" for i in IDS]
groups = [(kind, [i for i in IDS if verdict[i] == kind]) for kind in ("FAIL", "INVALID", "CHECK")]
groups = [(kind, ids) for kind, ids in groups if ids]
line = "GATE: " + (" | ".join(f"{kind} {' '.join(ids)}" for kind, ids in groups) if groups else "PASS")
code = {"FAIL": 1, "INVALID": 2, "CHECK": 3}[groups[0][0]] if groups else 0
text = "\n".join(lines)
with open(table_path, "w") as f:
    f.write(text + "\n\n" + line + "\n")
print(text)
print()
print(line)
sys.exit(code)
PY
}

# ---------------------------------------------------------------------------------------------------------------
# Gate parts (each owns its runs)

measure_g1() {
  local scenario
  for scenario in static slides scroll meeting video; do
    run_plain "g1-window-$scenario" "$scenario" window
  done
  run_plain g1-display-meeting meeting display
  run_plain g1-area-static static "$AREA_TARGET"
  measure_quality
}

QUALITY_RUNS="q060-window-meeting:0.60 q085-window-meeting:0.85"

# stored_miss <run>: the stored result.json of <run> is a harness exit 5 or 6 (a D1 miss).
stored_miss() {
  local stored
  stored="$(jq -r '.measure.status // .exitCode // empty' "$GATE_DIR/$1/result.json" 2>/dev/null || true)"
  [[ "$stored" == 5 || "$stored" == 6 ]]
}

# measure_quality [frame-misses]: the task 6.6 quality rows (owned by G1); with an argument only the runs whose stored
# status is a harness exit 5 or 6 (a D1 miss that `--only D1` must be able to clear).
measure_quality() {
  local spec name quality
  for spec in $QUALITY_RUNS; do
    IFS=: read -r name quality <<<"$spec"
    if [[ -n "${1:-}" ]]; then stored_miss "$name" || continue; fi
    run_plain "$name" meeting window --quality "$quality"
  done
}

measure_g2() {
  run_baseline base-meeting meeting
  run_baseline base-video video
}

measure_g6() {
  local scenario fps
  for scenario in meeting video; do
    for fps in 20 30; do run_plain "g6-window-$scenario-$fps" "$scenario" window --fps "$fps"; done
  done
}

NG_RUNS="ng-window-static-30:static:30 ng-window-scroll-30:scroll:30 ng-window-meeting-60:meeting:60"

# measure_non_gating [frame-misses]: the non-gating rows; with an argument only the ng-* runs whose stored status is a
# harness exit 5 or 6 (a D1 miss that `--only D1` must be able to clear).
measure_non_gating() {
  local spec name scenario fps
  [[ -n "${1:-}" ]] || { run_baseline base-static static; run_baseline base-scroll scroll; }
  for spec in $NG_RUNS; do
    IFS=: read -r name scenario fps <<<"$spec"
    if [[ -n "${1:-}" ]]; then stored_miss "$name" || continue; fi
    run_plain "$name" "$scenario" window --fps "$fps"
  done
}

measure_g3() {
  local ref_dir="$GATE_DIR/g3-ref" dir="$GATE_DIR/g3-window-static" wid ref_status=-1 width height
  local video start target pick gap ssim
  echo "==> g3-ref: --stage-only static --no-cursor, screencapture -x -o -l<WID> ref.png"
  launch "$ref_dir" static --target window --seconds 5 --stage-only --no-cursor
  if wait_for "$START_TIMEOUT" has_window_id "$ref_dir"; then
    wid="$(window_id "$ref_dir")"
    nap 1
    ref_status=0
    screencapture -x -o -l"$wid" "$ref_dir/ref.png" >/dev/null 2>&1 || ref_status=$?
  fi
  collect "$ref_dir"
  width="$(json_num "$(ffprobe -v error -show_entries stream=width -of csv=p=0 "$ref_dir/ref.png" 2>/dev/null)")"
  height="$(json_num "$(ffprobe -v error -show_entries stream=height -of csv=p=0 "$ref_dir/ref.png" 2>/dev/null)")"
  record_status "$ref_dir" "$(jq -n --argjson s "$ref_status" --argjson w "$width" --argjson h "$height" \
    '{refStatus: $s, refWidth: $w, refHeight: $h}')"

  echo "==> g3-window-static: static --target window --seconds $RUN_SECONDS --no-cursor"
  launch "$dir" static --target window --seconds "$RUN_SECONDS" --no-cursor
  collect "$dir"
  video="$(session_file "$dir" video.mp4)"
  pick=null gap=null ssim=null start=null
  if [[ -n "$video" && -f "$ref_dir/ref.png" ]]; then
    # ffmpeg -ss counts from the file's start_time and keeps the first frame at or after the target.
    start="$(json_num "$(ffprobe -v error -show_entries format=start_time -of csv=p=0 "$video" 2>/dev/null)")"
    if [[ "$start" != null ]]; then
      target="$(awk -v s="$start" -v t="$G3_AT_SECONDS" 'BEGIN { printf "%.6f", s + t }')"
      pick="$(ffprobe -v error -select_streams v:0 -show_entries packet=pts_time -of csv=p=0 "$video" 2>/dev/null \
        | awk -v t="$target" '$1 + 0 >= t && (best == "" || $1 + 0 < best) { best = $1 + 0 } END { print best }')"
      pick="$(json_num "$pick")"
      [[ "$pick" == null ]] || gap="$(awk -v p="$pick" -v t="$target" 'BEGIN { printf "%.6f", p - t }')"
    fi
    ssim="$(ffmpeg -nostdin -hide_banner -ss "$G3_AT_SECONDS" -i "$video" -i "$ref_dir/ref.png" -frames:v 1 \
      -lavfi "[0:v][1:v]ssim" -f null - 2>&1 | sed -n 's/.*SSIM .*All:\([0-9.]*\).*/\1/p' | tail -n 1)"
    ssim="$(json_num "$ssim")"
  fi
  echo "    start_time $start s, frame at $pick s (gap $gap s), SSIM All $ssim"
  record_status "$dir" "$(jq -n --argjson st "$start" --argjson p "$pick" --argjson g "$gap" --argjson s "$ssim" \
    '{startTime: $st, frameAt: $p, frameGap: $g, ssim: $s}')"
  scrub "$dir"
  scrub "$ref_dir"
}

pids_of() { pgrep -x "$1" 2>/dev/null | tr '\n' ',' | sed 's/,$//' || true; }
join_pids() { echo "$*" | tr ', ' '\n\n' | awk 'NF && !seen[$0]++' | paste -sd, - || true; }

# g4_run <name> [--no-video]
g4_run() {
  local name="$1" dir="$GATE_DIR/$1" harness listed encoders measure="{}"
  shift
  echo "==> $name: meeting --target display --seconds $RUN_SECONDS $* (top -l $G4_TOP_SAMPLES -s 1)"
  launch "$dir" meeting --target display --seconds "$RUN_SECONDS" "$@"
  harness="$HPID"
  if wait_for "$START_TIMEOUT" has_session "$dir"; then
    listed="$(join_pids "$(pids_of WindowServer)" "$(pids_of replayd)")"
    encoders="$(pids_of VTEncoderXPCService)"
    top -l "$G4_TOP_SAMPLES" -s 1 -stats pid,command,cpu,power,mem >"$dir/top.txt" 2>/dev/null &
    TPID=$!
    nap "$G4_ENCODER_DELAY"
    # The encoder service starts with the stream; replayd too when it was not running.
    encoders="$(join_pids "$encoders" "$(pids_of VTEncoderXPCService)")"
    listed="$(join_pids "$listed" "$(pids_of replayd)")"
    wait "$TPID" || true
    TPID=""
    collect "$dir"
    measure="$(top_py "$dir/top.txt" "$harness" "$(join_pids "$listed" "$encoders")" "$encoders" || echo '{}')"
    echo "    $(jq -c '{samples, samplePeriod, covered, cpuSum, encoderPids}' <<<"$measure" 2>/dev/null || echo "top unparsable")"
  else
    collect "$dir"
  fi
  record_status "$dir" "$measure"
  scrub "$dir"
}

measure_g4() {
  local i
  for ((i = 1; i <= G4_PAIRS; i++)); do
    g4_run "g4-$i-audio" --no-video
    g4_run "g4-$i-video"
  done
}

# g5_run <name> <container> [--mov]
g5_run() {
  local name="$1" container="$2" dir="$GATE_DIR/$1" video duration=null bytes=0 killed_at=null
  shift 2
  echo "==> $name: meeting --target window --seconds $RUN_SECONDS $*, kill -9 at $G5_KILL_AT s"
  launch "$dir" meeting --target window --seconds "$RUN_SECONDS" "$@"
  if wait_for "$START_TIMEOUT" has_video "$dir"; then
    nap "$G5_KILL_AT"
    if kill -0 "$HPID" 2>/dev/null; then
      kill -9 "$HPID" 2>/dev/null || true
      killed_at="$G5_KILL_AT"
    fi
  fi
  collect "$dir"
  video="$(session_file "$dir" "video.$container")"
  if [[ -n "$video" ]]; then
    duration="$(json_num "$(ff_duration "$video")")"
    bytes="$(file_bytes "$video")"
  fi
  echo "    ffprobe duration $duration s, $bytes bytes"
  if [[ ! -s "$dir/result.json" ]]; then
    jq -n --argjson s "$STATUS" --arg c "$container" \
      '{scenario: "meeting", target: "window", container: $c, exitCode: $s,
        error: (if $s == 137 then null else "no result.json from the harness" end)}' >"$dir/result.json"
  fi
  record_status "$dir" "$(jq -n --argjson d "$duration" --argjson b "$bytes" --argjson k "$killed_at" \
    --arg c "$container" '{ffprobeDuration: $d, bytes: $b, killedAt: $k, container: $c}')"
  scrub "$dir"   # the killed harness left its stage copy and session folder
}

measure_g5() {
  g5_run g5-kill-mp4 mp4
  g5_run g5-kill-mov mov --mov
}

measure_d12() {
  run_plain d12-pause-scroll scroll window --pause-at "$D1_PAUSE_AT" --resume-at "$D1_RESUME_AT"
}

measure_d3() {
  local dir="$GATE_DIR/d3-sync-meeting" video audio max_volume measure="{}"
  echo "==> d3-sync-meeting: meeting --target window --seconds $RUN_SECONDS --sync-marks --fps $D3_FPS"
  launch "$dir" meeting --target window --seconds "$RUN_SECONDS" --sync-marks --fps "$D3_FPS"
  collect "$dir"
  video="$(session_file "$dir" video.mp4)"
  audio="$(session_file "$dir" audio.caf)"
  if [[ -n "$video" && -n "$audio" && -s "$dir/result.json" ]]; then
    ffmpeg -nostdin -hide_banner -nostats -i "$video" \
      -vf "signalstats,metadata=print:key=lavfi.signalstats.YAVG" -f null - >"$dir/yavg.txt" 2>&1 || true
    ffmpeg -nostdin -hide_banner -nostats -i "$audio" \
      -af "pan=mono|c0=c1,silencedetect=n=${SILENCE_DB}dB:d=$SILENCE_MIN_SECONDS" -f null - >"$dir/silence.txt" 2>&1 \
      || true
    max_volume="$(ffmpeg -nostdin -hide_banner -nostats -i "$audio" -af "pan=mono|c0=c1,volumedetect" -f null - 2>&1 \
      | sed -n 's/.*max_volume: \(-*[0-9.]*\) dB.*/\1/p' | tail -n 1)"
    measure="$(d3_py "$dir/yavg.txt" "$dir/silence.txt" "$(jq -r '.audioDuration // "null"' "$dir/result.json")" \
      "$(jq -r '.firstFrameFileTime // "null"' "$dir/result.json")" "$(json_num "$max_volume")" \
      "$SILENCE_END_IGNORE" || echo '{}')"
    echo "    $(jq -c '{flashes, beeps, offsets, maxOffset, maxVolume}' <<<"$measure" 2>/dev/null || echo "unparsable")"
  fi
  record_status "$dir" "$measure"
  scrub "$dir"
}

# ---------------------------------------------------------------------------------------------------------------
# Commands

cmd_preflight() {
  (($# == 0)) || die "preflight takes no options"
  require_tools
  build_debug
  rm -rf "$PREFLIGHT_DIR"
  mkdir -p "$PREFLIGHT_DIR"
  echo "==> Preflight -> $PREFLIGHT_DIR"
  "$BIN" --background --measure-screen preflight --out "$PREFLIGHT_DIR" --stage "$STAGE" --clip "$CLIP" &
  HPID=$!
  STATUS=0
  wait "$HPID" || STATUS=$?
  HPID=""
  VERDICT_DONE=1
  case "$STATUS" in
    0) echo "==> preflight ok" ;;
    2) user_stop "another EchoDiar is running: quit it (never killed by this script), then re-run" ;;
    3) user_stop "no built-in Retina display (lid closed?) or the stage is not on it" ;;
    11) user_stop "Screen Recording is not granted to the terminal that runs this script (System Settings >" \
      "Privacy & Security > Screen & System Audio Recording), then re-run" ;;
    16) user_stop "the system channel was silent: grant system audio recording to the terminal that runs this" \
      "script (System Settings > Privacy & Security > Screen & System Audio Recording), then re-run" ;;
    *) echo "error: preflight failed (harness exit $STATUS)" >&2; exit 1 ;;
  esac
}

cmd_gate() {
  local only="" id wanted
  if (($# > 0)); then
    [[ "$1" == --only ]] || die "usage: gate [--only <ids>]"
    shift
    (($# > 0)) || die "--only needs IDs (G1..G6, D1..D3)"
    only="$(echo "$*" | tr ',' ' ' | tr '[:lower:]' '[:upper:]')"
    for id in $only; do
      [[ " $ALL_IDS " == *" $id "* ]] || die "unknown ID $id (G1..G6, D1..D3)"
    done
  fi
  require_tools
  build_debug
  if [[ -z "$only" ]]; then
    echo "==> Full gate: deleting stored results in $GATE_DIR"
    rm -rf "$GATE_DIR"
  fi
  mkdir -p "$GATE_DIR"
  wanted() { [[ -z "$only" || " $only " == *" $1 "* ]]; }
  # `if`, not `&&`: errexit stays on inside the measuring functions.
  if wanted G1; then measure_g1; fi
  if wanted G2; then measure_g2; fi
  if wanted G6; then measure_g6; fi
  if [[ -z "$only" ]]; then
    measure_non_gating
  elif wanted D1; then
    measure_non_gating frame-misses
    if ! wanted G1; then measure_quality frame-misses; fi
  fi
  if wanted G3; then measure_g3; fi
  if wanted G4; then measure_g4; fi
  if wanted G5; then measure_g5; fi
  if wanted D1 || wanted D2; then measure_d12; fi
  if wanted D3; then measure_d3; fi
  scrub_all
  echo
  echo "==> Table (also in $TABLE):"
  local code=0
  verdict_py "$GATE_DIR" "$TABLE" || code=$?
  ((code <= 3)) || die "the verdict could not be computed (exit $code)"
  VERDICT_DONE=1
  exit "$code"
}

main() {
  trap on_exit EXIT
  trap 'exit 130' INT
  trap 'exit 143' TERM
  case "${1:-}" in
    preflight) shift; cmd_preflight "$@" ;;
    gate) shift; cmd_gate "$@" ;;
    -h | --help | help) VERDICT_DONE=1; usage ;;
    *) usage >&2; die "unknown command ${1:-<none>}" ;;
  esac
}

# Sourced (for testing the helpers): define everything, run nothing.
if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then main "$@"; fi
