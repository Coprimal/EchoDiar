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

- **Record** a meeting app (Meet, Teams, Zoom, ...) or all system audio, plus your microphone.
- **Screen recording** (macOS): a display, a window or a region, as a small video beside the transcript.
- **Transcribe on device** (WhisperKit, Parakeet, Qwen3-ASR, Apple Speech) with speakers and live captions, or with a cloud provider of your choice.
- **AI summaries and chat** with local or cloud models; answers cite timestamps you can click.
- **Meeting detection and calendar triggers** that offer to record and stop when the call ends.
- **A searchable library** with auto-delete, global shortcuts (⌥⌘R, ⌥⌘P) and automatic updates.

More in the [docs](https://echodiar.coprimal.dev/docs.html).

## Install

- **macOS 15+ (Apple Silicon):** download the `.dmg` from [Releases](https://github.com/Coprimal/EchoDiar/releases/latest) and drag EchoDiar to `/Applications`.
- **Windows 11 (x64, Arm64):** get it from the [Microsoft Store](https://apps.microsoft.com/detail/9N5X564S1F2D?mode=direct). Installed it earlier with `EchoDiar.cer`? [Move to the Store](https://echodiar.coprimal.dev/docs.html#windows-warnings): your recordings and keys stay.

## Privacy

- Local-first: recordings, transcripts and chats stay on your computer. No account, no analytics.
- Audio or text goes to a cloud provider only when you choose one. API keys are stored in the Keychain.
- Permissions (Microphone, System Audio; optionally Screen Recording, Camera, Calendars, ...) are asked only when a feature needs them.

Always tell the other participants that you are recording.

## Development

```sh
brew install xcodegen
scripts/dev.sh   # generate the project, build Debug, launch
```

See [CONTRIBUTING.md](CONTRIBUTING.md). Releases are maintainer-only.

## License

© 2026 [Coprimal](https://coprimal.dev). Licensed under the GNU Affero General Public License v3.0 (AGPL-3.0), see [LICENSE](LICENSE).
