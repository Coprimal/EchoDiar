import EchoCore
import Testing

/// Guards the catch-up search highlight: every occurrence of the term, ignoring case and accents, in order;
/// "đ" is a letter of its own, not "d" with an accent (as the conversation search).
struct SearchTextTests {
    private let text = "Nẵng said: Đà NANG, not da nang."

    @Test func rangesOfEveryOccurrenceIgnoringCaseAndAccents() {
        #expect(found("nang") == ["Nẵng", "NANG", "nang"])
        #expect(found("đà") == ["Đà"])
        #expect(found("da") == ["da"])
    }

    @Test func blankOrMissingTermFindsNothing() {
        #expect(found("  ").isEmpty)
        #expect(found("kangaroo").isEmpty)
    }

    private func found(_ term: String) -> [String] {
        SearchText.ranges(of: term, in: text).map { String(text[$0]) }
    }
}
