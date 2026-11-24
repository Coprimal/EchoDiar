import CoreGraphics
import Testing
@testable import EchoScreen

/// Guards the key overlay's privacy gate: with "all keys" off, a key becomes a string only with Command or Control
/// held, or with Option (and maybe Shift) on a special key (arrows, ⌫, ⌦, ↩, ⇥, ⎋, F-keys, Home, End, Page Up/Down).
/// Plain keys, Shift-only keys and Option or Option-Shift on a printable key or Space (text on many layouts, e.g. "@"
/// as ⌥L) return nil, so text typed outside secure fields does not reach the picture.
struct KeyStrokeFormatterTests {
    private struct Stroke: Sendable, CustomTestStringConvertible {
        let keyCode: UInt16
        let flags: CGEventFlags
        let characters: String?
        let expected: String?
        var testDescription: String { "\(keyCode) \(flags.rawValue) \(characters ?? "nil") → \(expected ?? "nil")" }
    }

    private static let typedText: [Stroke] = [
        Stroke(keyCode: 0x00, flags: [], characters: "a", expected: nil),
        Stroke(keyCode: 0x00, flags: .maskShift, characters: "a", expected: nil),
        Stroke(keyCode: 0x00, flags: .maskAlphaShift, characters: "a", expected: nil),
        Stroke(keyCode: 0x31, flags: [], characters: " ", expected: nil),
        Stroke(keyCode: 0x24, flags: [], characters: "\r", expected: nil),
        Stroke(keyCode: 0x12, flags: [], characters: "1", expected: nil),
        Stroke(keyCode: 0x7B, flags: [.maskSecondaryFn, .maskNumericPad], characters: "\u{F702}", expected: nil),
        Stroke(keyCode: 0x37, flags: .maskCommand, characters: "k", expected: nil),
        // Option types text on many layouts (German "@" is ⌥L, "|" is ⌥⇧7; US ⌥S is "ß").
        Stroke(keyCode: 0x25, flags: .maskAlternate, characters: "l", expected: nil),
        Stroke(keyCode: 0x1A, flags: [.maskAlternate, .maskShift], characters: "7", expected: nil),
        Stroke(keyCode: 0x31, flags: .maskAlternate, characters: " ", expected: nil),
    ]

    private static let shortcuts: [Stroke] = [
        Stroke(keyCode: 0x28, flags: .maskCommand, characters: "k", expected: "⌘K"),
        Stroke(keyCode: 0x28, flags: [.maskCommand, .maskShift], characters: "k", expected: "⇧⌘K"),
        Stroke(keyCode: 0x33, flags: [.maskControl, .maskAlternate], characters: "\u{7F}", expected: "⌃⌥⌫"),
        Stroke(keyCode: 0x24, flags: .maskCommand, characters: "\r", expected: "⌘↩"),
        Stroke(keyCode: 0x60, flags: [.maskAlternate, .maskSecondaryFn], characters: "\u{F708}", expected: "⌥F5"),
        Stroke(keyCode: 0x7C, flags: [.maskAlternate, .maskSecondaryFn, .maskNumericPad], characters: "\u{F703}", expected: "⌥→"),
    ]

    @Test(arguments: typedText + shortcuts)
    private func shortcutsOnly(_ stroke: Stroke) {
        let text = KeyStrokeFormatter.text(keyCode: stroke.keyCode, flags: stroke.flags, characters: stroke.characters, allKeys: false)
        #expect(text == stroke.expected)
    }

    @Test func allKeysShowsPlainAndShiftedKeys() {
        #expect(KeyStrokeFormatter.text(keyCode: 0x00, flags: [], characters: "a", allKeys: true) == "A")
        #expect(KeyStrokeFormatter.text(keyCode: 0x00, flags: .maskShift, characters: "a", allKeys: true) == "⇧A")
        #expect(KeyStrokeFormatter.text(keyCode: 0x25, flags: .maskAlternate, characters: "l", allKeys: true) == "⌥L")
    }
}
