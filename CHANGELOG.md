# Changelog

All notable changes made by this mod are listed here. This is a community mod of
[EDI by NoGRo](https://github.com/NoGRo/Edi); only the mod's own changes are documented.

## [1.0.2] — 2026-05-27

First public release of the community mod, built on EDI by NoGRo.

### Added
- Redesigned game launcher — cover-art game cards with adjustable size, corner radius, cover blur, badge shape, and title alignment/size.
- Clear **selected-game** highlight with multiple styles (glow / border / bar / solid).
- Cover & banner search from multiple online sources, including **SteamGridDB** (with an API-key field in Settings) and **itch.io**.
- Multi-image picker from a matched source, plus a built-in **crop tool**.
- Animated **GIF** and **MP4** covers that play on hover/select.
- **Live funscript preview** in the Playback panel — shows the currently playing script, freezes the line + playhead on pause, and reflects live intensity/vibration.
- Optional on-screen **Preview Device** (toggle in a new Settings → **Dev** tab) so playback can be visualized without hardware.
- **Support popup** that credits the original project and offers donation links (Ko-fi, plus Monero with copy button + QR code).
- Customizable **top header bar** with quick-launch chips; Settings + Open Config moved into the header.
- Settings help text consolidated into **info-button popups**.
- Window size/position persisted across launches.

### Changed
- Log panel restyled — newest entry pinned to the top and highlighted.
- Intensity bar fill now reaches full at maximum.
- Themed scrollbars; Game Settings panel sized to fit its content.
- Sharper EDI type-badge icons; removed the divider line between game rows.

### Fixed
- EDI no longer exits when Intiface/Buttplug isn't running (fast TCP reachability probe + widened connection error handling).
- Playback panel shows the active funscript again.
- Handy / OSR / Preview Device reliability fixes.
- Image submenu not opening.

### Security / privacy
- Replaced the bundled HTTPS dev certificate with a freshly generated self-signed one.
- Stripped personal data from shipped configs; runtime/user files are now git-ignored.
