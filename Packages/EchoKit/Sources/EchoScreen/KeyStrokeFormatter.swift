import CoreGraphics

/// Turns one key-down into the text the key overlay shows, in macOS menu style: ⌃ ⌥ ⇧ ⌘, then the key (letters
/// uppercased, special keys as glyphs, e.g. "⇧⌘K", "⌃⌥⌫", "⌥F5").
///
/// Privacy gate, unless `allKeys` is on:
/// - With Command or Control held, the key shows ("⌘K", "⌃⌥⌫").
/// - With Option (and maybe Shift) but neither Command nor Control, only special keys show (arrows, ⌫, ⌦, ↩, ⇥, ⎋,
///   F-keys, Home, End, Page Up/Down: "⌥F5", "⌥→"); a printable key or Space returns nil, because Option types text
///   on many layouts (German "@" is ⌥L; "[ ] { } | \ ~ €" need Option; US ⌥S is "ß").
/// - Without Command, Control or Option, every key returns nil. Shift, Caps Lock and Fn do not count.
/// So text typed outside secure fields (including passwords) does not reach the picture.
/// With `allKeys` on, every key shows: a plain key alone ("A"), a Shift-only key with the Shift glyph before the base
/// key ("⇧A", "⇧1"), never the shifted character.
///
/// `characters` is the event's characters ignoring modifiers. The caller should pass the unshifted base character
/// (e.g. "1" for ⇧1); a shifted one ("!") is shown as given.
public enum KeyStrokeFormatter {
    public static func text(keyCode: UInt16, flags: CGEventFlags, characters: String?, allKeys: Bool) -> String? {
        if modifierKeyCodes.contains(keyCode) { return nil }
        let key: String?
        if allKeys || !flags.isDisjoint(with: [.maskCommand, .maskControl]) {
            key = specialKeys[keyCode] ?? printable(characters)
        } else if flags.contains(.maskAlternate), keyCode != spaceKeyCode {
            key = specialKeys[keyCode]
        } else {
            key = nil
        }
        guard let key else { return nil }
        var text = ""
        if flags.contains(.maskControl) { text += "⌃" }
        if flags.contains(.maskAlternate) { text += "⌥" }
        if flags.contains(.maskShift) { text += "⇧" }
        if flags.contains(.maskCommand) { text += "⌘" }
        return text + key
    }

    /// Command, Shift, Caps Lock, Option, Control (left and right) and Fn: modifier-only key-downs.
    private static let modifierKeyCodes: Set<UInt16> = [0x36, 0x37, 0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F]

    /// Space: text-like, so Option-Space (a non-breaking space) is not shown without `allKeys`.
    private static let spaceKeyCode: UInt16 = 0x31

    /// Virtual key codes (HIToolbox kVK_*) of keys whose characters are control or private-use scalars.
    private static let specialKeys: [UInt16: String] = [
        0x24: "↩", 0x30: "⇥", 0x31: "Space", 0x33: "⌫", 0x75: "⌦", 0x35: "⎋",
        0x7B: "←", 0x7C: "→", 0x7E: "↑", 0x7D: "↓",
        0x73: "↖", 0x77: "↘", 0x74: "⇞", 0x79: "⇟",
        0x7A: "F1", 0x78: "F2", 0x63: "F3", 0x76: "F4", 0x60: "F5", 0x61: "F6",
        0x62: "F7", 0x64: "F8", 0x65: "F9", 0x6D: "F10", 0x67: "F11", 0x6F: "F12",
    ]

    /// The uppercased key character, or nil when it is missing, whitespace, a control or a private-use scalar.
    private static func printable(_ characters: String?) -> String? {
        guard let characters, !characters.isEmpty else { return nil }
        let unprintable = characters.unicodeScalars.contains {
            $0.properties.generalCategory == .control || $0.properties.generalCategory == .privateUse
                || $0.properties.isWhitespace
        }
        return unprintable ? nil : characters.uppercased()
    }
}
