# Changelog

All notable changes to SimpleWhisper are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> This file is the source of truth for release notes. When cutting a release,
> move the `[Unreleased]` entries into a new version section and paste that
> section into the GitHub Release body.

## [1.5.0] - 2026-09-02

### Added
- Engine picker in Settings with four cloud providers plus Local.
- Google Gemini 3.5 Transcribe (`gemini-3.5-transcribe`) via the Gemini API.
- Groq hosted Whisper (`whisper-large-v3-turbo`, `whisper-large-v3`).
- Deepgram Nova-3 with key-term boosting from the custom vocabulary list.
- OpenAI model selection: `gpt-4o-mini-transcribe` (new default), `gpt-4o-transcribe`, `whisper-1`.
- One DPAPI-encrypted API key per provider.

### Changed
- OpenAI NuGet package upgraded from 2.9.1 to 2.13.0.
- New installs and existing installs default to `gpt-4o-mini-transcribe` instead of `whisper-1`.

## [1.4.0] - 2026-08-19

### Added
- Cancel a recording or in-flight transcription at any time with **Esc** or the new **✕ button** on the overlay.
- Automatic 90-second timeout so "Processing…" can never hang forever.

### Fixed
- An accidental recording could leave the app stuck on "Processing…" indefinitely, ignoring all further trigger presses.

## [1.3.2] - 2026-06-08

### Fixed
- After a silent auto-update, the app now relaunches itself once the installer
  finishes. Previously it installed the update but stayed closed until the next
  manual launch or login.

## [1.3.1] - 2026-06-08

### Fixed
- The tray **About** menu item flashed open and immediately disappeared. It now
  opens a proper window that stays open until you close it.

## [1.3.0] - 2026-03-23

### Added
- **Auto-update**: the app checks GitHub for new releases on startup and every
  4 hours, showing a tray balloon when an update is available.
- **Check for Updates** item in the system-tray context menu.
- One-click download and silent install from the tray or the Settings window.
- Toggle to enable/disable auto-update in Settings → General → Updates.

### Changed
- The installer now gracefully closes and restarts the app during updates.

## [1.2.0] - 2026-03-23

### Added
- **Output volume reduction**: system audio is automatically ducked while
  recording, so playback doesn't bleed into your dictation.

### Changed
- Miscellaneous improvements.

## [1.1.0] - 2026-03-12

### Added
- Real-time streaming transcription.
- Muted-microphone detection.
- Windows installer and packaging.

## [1.0.0] - 2026-03-11

### Added
- Initial release: speech-to-text anywhere with a global hotkey, system-tray
  app, and a dark theme.

<!-- Releases 1.0.0–1.2.0 predate GitHub Releases and are tagged only where noted. -->
[Unreleased]: https://github.com/megensel/SimpleWhisper/compare/v1.3.2...HEAD
[1.3.2]: https://github.com/megensel/SimpleWhisper/compare/v1.3.1...v1.3.2
[1.3.1]: https://github.com/megensel/SimpleWhisper/compare/v1.3.0...v1.3.1
[1.3.0]: https://github.com/megensel/SimpleWhisper/releases/tag/v1.3.0
