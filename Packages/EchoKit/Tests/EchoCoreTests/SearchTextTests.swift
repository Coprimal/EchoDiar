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

/// Guards the transcript search: what `ranges` finds, plus words with a query word inside them or, when asked for, a
/// typo or two away (none for words of 4 letters or fewer), and a query of several words only as consecutive words.
/// Windows `PhraseSearchTextTests`.
struct PhraseSearchTextTests {
    private let text = "The Meeting about the project deadline moved; Đà Nẵng team said the project is late."

    @Test func findsEveryPlainOccurrence() {
        #expect(found("nang", in: "Nẵng said: Đà NANG, not da nang.") == ["Nẵng", "NANG", "nang"])
        #expect(found("da", in: "Nẵng said: Đà NANG, not da nang.") == ["da"])
    }

    @Test func toleratesTyposInLongerWords() {
        #expect(found("meetng") == ["Meeting"])
        #expect(found("deadlnie") == ["deadline"])
        #expect(found("projet") == ["project", "project"])
    }

    @Test func shortWordsMustMatchExactly() {
        #expect(found("tema").isEmpty)
        #expect(found("nang") == ["Nẵng"])
    }

    @Test func severalWordsMatchOnlyConsecutiveWords() {
        #expect(found("project deadlne") == ["project deadline"])
        #expect(found("proj dead") == ["project deadline"])
        #expect(found("project late").isEmpty)
    }

    @Test func blankOrMissingQueryFindsNothing() {
        #expect(found("  ").isEmpty)
        #expect(found("kangaroo").isEmpty)
    }

    /// Over several texts (segments, messages, catch-ups): in order, each with its text's index; typos only when no
    /// text has the query without them.
    @Test func findAcrossTextsWithTyposOnlyWhenNoneMatches() {
        let texts = ["Kế hoạch thành phố", "tháng sau", "thành công"]
        func found(_ query: String) -> [String] {
            SearchText.find(query, in: texts).map { "\($0.text):\(texts[$0.text][$0.range])" }
        }
        #expect(found("thanh") == ["0:thành", "2:thành"])
        #expect(found("thamh") == ["0:thành", "2:thành"])
        #expect(found("  ").isEmpty)
    }

    @Test func typosOnlyWhenAskedFor() {
        #expect(found("meetng", typos: false).isEmpty)
        #expect(found("thamh", in: "Kế hoạch thành phố, tháng sau") == ["thành"])
    }

    private func found(_ query: String, in text: String? = nil, typos: Bool = true) -> [String] {
        let text = text ?? self.text
        return SearchText.phraseRanges(of: query, in: text, typos: typos).map { String(text[$0]) }
    }
}

/// Guards the filters (conversations, chats, catch-ups, Settings, models): every query word inside some word of the
/// texts, in any order; typos only when asked for, which the filters do only when nothing matches without them
/// (`filter`), so a correct word never lets look-alikes in; the highlight marks typo words only then.
/// Windows `FilterSearchTextTests`.
struct FilterSearchTextTests {
    private let texts = ["Weekly sync", "The meeting approved the budget."]

    @Test func everyWordSomewhereInAnyOrder() {
        #expect(SearchText.matches("budget weekly", in: texts))
        #expect(SearchText.matches("MEET", in: texts))
        #expect(SearchText.matches("  ", in: texts))
        #expect(!SearchText.matches("budget kangaroo", in: texts))
    }

    @Test func typosOnlyWhenAllowed() {
        #expect(!SearchText.matches("budgat", in: texts))
        #expect(SearchText.matches("budgat", in: texts, typos: true))
        #expect(!SearchText.matches("tema", in: texts, typos: true))
    }

    @Test func precomputedWordsServeLongTexts() {
        let words = SearchText.Words(["The meeting approved the budget."])
        #expect(SearchText.matches("budget weekly", in: ["Weekly sync"], words: [SearchText.Words(["Weekly sync"]), words]))
        #expect(!SearchText.matches("budget weekly", in: ["Weekly sync"], words: [SearchText.Words(["Weekly sync"])]))
    }

    @Test func filterAllowsTyposOnlyWhenNothingMatchesWithout() {
        let items = ["Kế hoạch thành phố", "Tháng sau"]
        #expect(SearchText.filter(items, "thanh") { SearchText.matches("thanh", in: [$0], typos: $1) } == ["Kế hoạch thành phố"])
        #expect(SearchText.filter(items, "thamh") { SearchText.matches("thamh", in: [$0], typos: $1) } == ["Kế hoạch thành phố"])
        #expect(SearchText.filter(items, "  ") { SearchText.matches("  ", in: [$0], typos: $1) } == items)
    }

    @Test func matchRangesMarkWhatMatched() {
        let text = "Weekly meeting: budget"
        func found(_ query: String) -> [String] {
            SearchText.matchRanges(of: query, in: text).map { String(text[$0]) }
        }
        #expect(found("budget weekly") == ["Weekly", "budget"])
        #expect(found("meet") == ["meet"])
        #expect(found("budgat") == ["budget"])
        #expect(found("tema").isEmpty)
    }
}
