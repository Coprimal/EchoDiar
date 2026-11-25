import Foundation

/// Which conversations auto-delete moves to the Trash. Pure: it deletes nothing (`LibraryStore` does) and ignores
/// `settings.autoDeleteEnabled`, so the Storage tab can preview before the user turns auto-delete on.
public enum RetentionPolicy {
    public struct Deletion: Equatable, Sendable {
        public var conversationID: UUID
        public var mode: RetentionMode
        /// What goes to the Trash: the conversation folder (whole conversation), its audio and video files (audio and
        /// video) or its video files (video only).
        public var items: [URL]

        public init(conversationID: UUID, mode: RetentionMode, items: [URL]) {
            self.conversationID = conversationID
            self.mode = mode
            self.items = items
        }
    }

    /// Transcribed conversations created more than `settings.autoDeleteAfterDays` days before `now`. Never selects a
    /// conversation that is recording, compressing, transcribing, failed or not yet transcribed. Audio and video: lists
    /// the conversation's `audioFiles` then `videoFiles`, and skips it when both are empty. Video only: lists just its
    /// `videoFiles`, and skips it when it has none left.
    public static func select(
        conversations: [Conversation], now: Date, settings: AppSettings,
        audioFiles: (Conversation) -> [URL] = { LibraryStore.audioFiles(in: $0.folder) },
        videoFiles: (Conversation) -> [URL] = { LibraryStore.videoFiles(in: $0.folder) }
    ) -> [Deletion] {
        let days = min(max(settings.autoDeleteAfterDays, AppSettings.autoDeleteDaysRange.lowerBound),
                       AppSettings.autoDeleteDaysRange.upperBound)
        let cutoff = now.addingTimeInterval(-Double(days) * 86_400)
        return conversations.compactMap { conversation in
            guard conversation.status == .transcribed, conversation.hasTranscript, conversation.createdAt < cutoff else {
                return nil
            }
            switch settings.autoDeleteMode {
            case .wholeConversation:
                return Deletion(conversationID: conversation.id, mode: .wholeConversation, items: [conversation.folder])
            case .audioOnly:
                let files = audioFiles(conversation) + videoFiles(conversation)
                return files.isEmpty ? nil : Deletion(conversationID: conversation.id, mode: .audioOnly, items: files)
            case .videoOnly:
                let video = videoFiles(conversation)
                return video.isEmpty ? nil : Deletion(conversationID: conversation.id, mode: .videoOnly, items: video)
            }
        }
    }
}
