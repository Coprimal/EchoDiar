import Foundation

/// Where a search term occurs in a text, ignoring case and accents (as `localizedStandardContains`): the catch-up
/// search highlights these ranges. Windows `SearchText.Ranges`.
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
}
