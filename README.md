<div align="center">

<img src="assets/logo_square_512.png" width="128" alt="EchoDiar icon">

# EchoDiar

**Record any meeting on your Mac, see who said what, and ask AI about it.**

[![Latest release](https://img.shields.io/github/v/release/Coprimal/EchoDiar)](https://github.com/Coprimal/EchoDiar/releases/latest)
[![License: AGPLv3](https://img.shields.io/badge/license-AGPLv3-blue)](LICENSE)
![macOS 15+](https://img.shields.io/badge/macOS-15%2B-blue)
![Windows 11](https://img.shields.io/badge/Windows-11-blue)

[Website](https://echodiar.coprimal.dev) · [Download](https://github.com/Coprimal/EchoDiar/releases/latest) · [Docs](https://echodiar.coprimal.dev/docs.html) · [Changelog](CHANGELOG.md)

<img src="assets/poster.jpg" width="100%" alt="EchoDiar: a transcript with speakers and an AI answer that cites timestamps">

</div>

## Features

- Record one meeting app (Google Meet in a browser, Teams, Zoom, ...) or all system audio, plus your microphone, on separate channels. Pause, resume, live level meters and a menu bar status.
- Optional screen recording on macOS: a whole display, one window or a portion of the screen you drag, saved next to the recording's audio as a small HEVC video (about 5 MB per minute for a meeting window at 15 fps; macOS Screenshot's window recording of the same meeting is about 38 times larger). Frame rate, quality, the cursor and clicks, a camera bubble and key presses are options; a "screen only" recording keeps the picture and sound without a transcript. Turn on "Record the screen too" in the menu next to Record, then Record asks what to record: the entire screen, a window or a portion of the screen. During a recording, the screen button turns the screen on and off as often as you like; one video covers the whole recording, with a black "Screen recording off" picture where the screen was off. The video plays beside the transcript and "Export video" saves it with the sound as one MP4.
- On-device transcription (WhisperKit, Parakeet, Qwen3-ASR or Apple Speech) with optional speaker separation and a transcript you can click to hear each line. WhisperKit and Parakeet transcribe while you record, with live captions, so the transcript is ready seconds after Stop. Apple Speech (macOS 26 and later) shows live captions too, and makes the saved transcript after Stop.
- Optional cloud transcription: OpenAI-compatible services, Gemini, xAI, ElevenLabs, Deepgram, AssemblyAI, Speechmatics, Soniox and Gladia, with every key in Settings > API Keys. While you record, a live transcript can come from a local model (free, display only: the saved transcript still comes from the cloud after Stop) or from the provider's realtime stream (ElevenLabs, Deepgram, AssemblyAI, OpenAI, xAI, Speechmatics, Soniox, Gladia). Realtime costs money: two live connections for the whole recording, on top of the transcription after Stop, unless you use the realtime result as the transcript.
- AI summaries and chat with on-device Foundation Models, local MLX models, local servers (LM Studio, Ollama, ...), cloud APIs (OpenAI, Anthropic, Gemini, ...) or coding CLIs (Claude Code, Codex, OpenCode, Gemini CLI). Answers cite timestamps that jump to that moment. "Copy as prompt" for any other AI tool.
- Meeting detection that offers to record, a floating indicator that can follow the meeting window, and auto-stop when the call ends.
- Calendar triggers with any account in macOS Calendar (iCloud, Google, Outlook/Exchange, ...): when a meeting starts, EchoDiar asks, waits for the call or records right away (without asking only for events you organize, accepted or answered Maybe to, and events without invitees in the EchoDiar calendar or a calendar On My Mac; other events ask), names the recording after the event and can stop when it ends. A Calendar page and the menu bar list upcoming meetings to join, record or skip, and you can create your own meetings, one-off or repeating.
- A library grouped by day with search (⌘F), compression to AAC, crash recovery, automatic deletion of old recordings, global shortcuts to record (⌥⌘R) and pause (⌥⌘P) and automatic updates.

## Install

### macOS

Download the `.dmg` from [Releases](https://github.com/Coprimal/EchoDiar/releases/latest), drag EchoDiar to `/Applications` and launch it. Requires macOS 15 or later on Apple Silicon.

### Windows 11 (x64, Arm64)

Get EchoDiar from the [Microsoft Store](https://apps.microsoft.com/detail/9N5X564S1F2D?mode=direct): the Store installs it in one click and keeps it updated.

Installed EchoDiar earlier with its certificate (`EchoDiar.cer`)? [Move to the Microsoft Store](https://echodiar.coprimal.dev/docs.html#windows-warnings): your recordings and API keys stay.

## Permissions

- **Microphone**: records your voice.
- **System Audio Recording**: records the meeting's audio. Onboarding tests it and opens the right Privacy & Security pane.
- **Screen Recording** (optional): asked the first time you record the screen; macOS applies it once EchoDiar is quit and opened again.
- **Camera** and **Input Monitoring** (optional): asked only when you turn on the camera bubble or key presses for screen recordings. Settings > Recording > Screen > Permissions shows the state of each.
- **Accessibility** (optional): only to attach the recording indicator to the meeting window.
- **Calendars** (optional): reads your meetings from macOS Calendar to offer recording when they start and to name the recording, and adds the events you create to an "EchoDiar" calendar. EchoDiar writes to no other calendar.

Always tell the other participants that you are recording, and follow the law where you and they live.

## Privacy

- Local-first: recordings, transcripts, summaries and chats stay in `~/Library/Application Support/EchoDiar/`. No account, no analytics.
- Screen recordings, with the camera picture and key presses drawn into them, stay in the conversation's folder on this Mac. Key presses are only drawn into the picture, never logged; with "Show all keys" off, only shortcuts with ⌘ or ⌃ and Option with special keys (arrows, Delete, Return, Tab, Esc, function keys, ...) are shown, never typed text.
- API keys are stored in the Keychain.
- Calendars are read on this Mac; EchoDiar writes only to its own "EchoDiar" calendar (On My Mac when available, otherwise your default calendar account, e.g. iCloud, which then syncs those events like any other) and stores only that calendar's id, the ids of calendars you don't watch and the ids of meetings you skip. Don't share the EchoDiar calendar: anyone who can add events to it could start a recording. A recording started from an event takes the event's title, which goes to an AI provider only like any other conversation title, when you use one.
- Audio or transcripts go to a cloud provider or a CLI only when you choose one; cloud transcription asks once for each destination before sending audio.
- Models are downloaded from Hugging Face only when you click Download. Update checks use the Sparkle feed at `echodiar.coprimal.dev`.

## Resource usage

Idle in the background: **27 MB**, 0% CPU (measured 2026-10-19 on a Release build of 0.1.0 with `scripts/measure.sh idle 30`). Recording and transcribing have not been measured yet.

## Development

```sh
brew install xcodegen
scripts/dev.sh   # generate the project, build Debug, launch
```

See [CONTRIBUTING.md](CONTRIBUTING.md). Releases are maintainer-only.

## License

© 2026 [Coprimal](https://coprimal.dev). Licensed under the GNU Affero General Public License v3.0 (AGPL-3.0), see [LICENSE](LICENSE).
