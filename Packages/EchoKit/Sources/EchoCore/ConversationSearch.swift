import Foundation

/// Full-text search over a conversation: title, speakers, transcript, summary and chat.
public enum ConversationSearch {
    /// The searchable text of one conversation, one string per field.
    public struct Document: Sendable {
        /// The only field that changes: a rename sets the current title.
        public var title: String
        /// Display and default names of every speaker, in first-appearance order.
        public let speakers: [String]
        public let transcript: String
        public let summary: String
        public let chat: String
        /// The words of every field but the title, folded once, for the search on every keystroke.
        private let words: SearchText.Words

        public init(title: String, speakers: [String], transcript: String, summary: String, chat: String) {
            self.title = title
            self.speakers = speakers
            self.transcript = transcript
            self.summary = summary
            self.chat = chat
            words = SearchText.Words([transcript, summary, chat] + speakers)
        }

        /// True when `term` is blank, or some field contains it, or each of its words is in some field, in any order
        /// (`SearchText.matches`, ignoring case and accents); a word may be a typo away when `typos`.
        public func matches(_ term: String, typos: Bool = false) -> Bool {
            SearchText.matches(term, in: [title, transcript, summary, chat] + speakers,
                               words: [SearchText.Words([title]), words], typos: typos)
        }
    }

    /// Builds the document of one conversation; a missing transcript, summary or chat yields empty fields.
    public static func document(title: String, transcript: Transcript?, summary: String?, chat: ChatLog?) -> Document {
        let segments = transcript?.segments ?? []
        var speakers: [String] = []
        if let transcript {
            for speaker in segments.map(\.speaker) {
                for name in [transcript.displayName(for: speaker), Transcript.defaultName(for: speaker)]
                where !speakers.contains(name) {
                    speakers.append(name)
                }
            }
        }
        return Document(
            title: title,
            speakers: speakers,
            transcript: segments.map(\.text).joined(separator: "\n"),
            summary: summary ?? "",
            chat: (chat?.messages ?? []).map(\.text).joined(separator: "\n"))
    }
}
