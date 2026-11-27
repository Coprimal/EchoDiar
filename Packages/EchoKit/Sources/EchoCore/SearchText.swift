import Foundation

/// The app's text search, ignoring case and accents (as `localizedStandardContains`; "đ" is a letter of its own). A
/// query is found as a whole, or word by word: each query word inside a word of the text, and with a typo or two
/// (`allowedEdits`) only when asked for. The filters and the transcript search allow typos only when nothing is found
/// without them, so a correct word never lets look-alikes in (folded Vietnamese syllables are often one letter apart).
/// Windows `SearchText`.
public enum SearchText {
    /// Every occurrence of `term` (trimmed) in `text`, in order, not overlapping; none for a blank term.
    public static func ranges(of term: String, in text: String) -> [Range<String.Index>] {
        let term = term.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !term.isEmpty else { return [] }
        var found: [Range<String.Index>] = []
        var start = text.startIndex
        while let range = text.range(of: term, options: [.caseInsensitive, .diacriticInsensitive],
                                     range: start..<text.endIndex, locale: .current) {
            found.append(range)
            start = range.upperBound
        }
        return found
    }

    // MARK: - Filters

    /// The words of some texts, folded once: a filter that runs on every keystroke reads long texts (transcripts,
    /// chats) through these, built when the texts are read.
    public struct Words: Sendable {
        private let set: Set<[UInt16]>
        private let list: [[UInt16]]

        public init(_ texts: [String]) {
            var set = Set<[UInt16]>()
            for text in texts {
                let folded = SearchText.fold(text)
                for range in SearchText.words(in: folded) { set.insert(Array(folded[range].utf16)) }
            }
            self.set = set
            list = Array(set)
        }

        /// Some word has `term` inside it, or is a typo away from it when `typos`.
        func contains(_ term: [UInt16], typos: Bool) -> Bool {
            set.contains(term) || list.contains { SearchText.matches($0, term: term, typos: typos) }
        }
    }

    /// True when `query` is blank, some text contains it whole, or each of its words is inside a word of the texts
    /// (`words`, else the texts' own), in any order; a word may be a typo away when `typos`.
    public static func matches(_ query: String, in texts: [String], words: [Words]? = nil, typos: Bool = false) -> Bool {
        let query = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !query.isEmpty else { return true }
        if texts.contains(where: { $0.localizedStandardContains(query) }) { return true }
        let terms = terms(of: query)
        guard !terms.isEmpty else { return false }
        let words = words ?? [Words(texts)]
        return terms.allSatisfy { term in words.contains { $0.contains(term, typos: typos) } }
    }

    /// The items `matches` (given an item and whether typos are allowed) accepts without typos; with them only when
    /// none is. All for a blank query.
    public static func filter<Item>(_ items: [Item], _ query: String,
                                    _ matches: (Item, _ typos: Bool) -> Bool) -> [Item] {
        let exact = items.filter { matches($0, false) }
        guard exact.isEmpty, !query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return exact }
        return items.filter { matches($0, true) }
    }

    /// What a filter's highlight marks in `text`: the query and each of its words wherever they occur; when the text
    /// matches only with typos, also the words a typo away. In order, overlapping ranges merged.
    public static func matchRanges(of query: String, in text: String) -> [Range<String.Index>] {
        var found = ([query] + words(in: query).map { String(query[$0]) }).flatMap { ranges(of: $0, in: text) }
        if !matches(query, in: [text]) {
            let terms = terms(of: query)
            found += words(in: text).filter { range in
                let word = Array(fold(text[range]).utf16)
                return terms.contains { matches(word, term: $0, typos: true) }
            }
        }
        return merged(found)
    }

    // MARK: - Transcript

    /// The transcript search: what `ranges` finds, plus runs of consecutive words matching the query's words in order,
    /// each word with its query word inside it, or a typo away when `typos`. In order, overlapping ranges merged; none
    /// for a blank query. Windows `SearchText.PhraseRanges`.
    public static func phraseRanges(of query: String, in text: String, typos: Bool) -> [Range<String.Index>] {
        var found = ranges(of: query, in: text)
        let terms = terms(of: query)
        if !terms.isEmpty {
            let textWords = words(in: text)
            let folded = textWords.map { Array(fold(text[$0]).utf16) }
            var start = 0
            while start + terms.count <= textWords.count {
                if terms.indices.allSatisfy({ matches(folded[start + $0], term: terms[$0], typos: typos) }) {
                    found.append(textWords[start].lowerBound..<textWords[start + terms.count - 1].upperBound)
                    start += terms.count
                } else {
                    start += 1
                }
            }
        }
        return merged(found)
    }

    /// `phraseRanges` over several texts (a transcript's segments, a chat's messages, the catch-ups), in order, each
    /// with its text's index; with typos only when no text has the query without them. Windows `SearchText.Find`.
    public static func find(_ query: String, in texts: [String]) -> [(text: Int, range: Range<String.Index>)] {
        func found(typos: Bool) -> [(text: Int, range: Range<String.Index>)] {
            texts.enumerated().flatMap { index, text in
                phraseRanges(of: query, in: text, typos: typos).map { (index, $0) }
            }
        }
        let exact = found(typos: false)
        return exact.isEmpty ? found(typos: true) : exact
    }

    // MARK: - Words

    /// Typos a query word may have: none up to 4 letters (short words, Vietnamese syllables, are too alike), 1 up
    /// to 7, then 2.
    private static func allowedEdits(_ length: Int) -> Int { length <= 4 ? 0 : length <= 7 ? 1 : 2 }

    /// `term` is inside `word`, or a typo away from it when `typos`.
    private static func matches(_ word: [UInt16], term: [UInt16], typos: Bool) -> Bool {
        if contains(word, term) { return true }
        let allowed = typos ? allowedEdits(term.count) : 0
        guard allowed > 0, abs(word.count - term.count) <= allowed else { return false }
        return editDistance(word, term) <= allowed
    }

    private static func contains(_ word: [UInt16], _ term: [UInt16]) -> Bool {
        guard term.count <= word.count else { return false }
        return (0...(word.count - term.count)).contains { start in
            term.indices.allSatisfy { word[start + $0] == term[$0] }
        }
    }

    /// Levenshtein distance.
    private static func editDistance(_ a: [UInt16], _ b: [UInt16]) -> Int {
        var previous = Array(0...b.count)
        for (i, x) in a.enumerated() {
            var current = [i + 1]
            for (j, y) in b.enumerated() {
                current.append(min(previous[j + 1] + 1, current[j] + 1, previous[j] + (x == y ? 0 : 1)))
            }
            previous = current
        }
        return previous[b.count]
    }

    /// The query's words, folded.
    private static func terms(of query: String) -> [[UInt16]] {
        words(in: query).map { Array(fold(query[$0]).utf16) }
    }

    /// Runs of letters and digits.
    private static func words<S: StringProtocol>(in text: S) -> [Range<S.Index>] {
        var found: [Range<S.Index>] = []
        var start: S.Index?
        for index in text.indices {
            let character = text[index]
            if character.isLetter || character.isNumber {
                if start == nil { start = index }
            } else if let wordStart = start {
                found.append(wordStart..<index)
                start = nil
            }
        }
        if let start { found.append(start..<text.endIndex) }
        return found
    }

    private static func fold<S: StringProtocol>(_ text: S) -> String {
        text.folding(options: [.caseInsensitive, .diacriticInsensitive], locale: nil)
    }

    private static func merged(_ ranges: [Range<String.Index>]) -> [Range<String.Index>] {
        var merged: [Range<String.Index>] = []
        for range in ranges.sorted(by: { $0.lowerBound < $1.lowerBound }) {
            if let last = merged.last, range.lowerBound < last.upperBound {
                merged[merged.count - 1] = last.lowerBound..<max(last.upperBound, range.upperBound)
            } else {
                merged.append(range)
            }
        }
        return merged
    }
}
