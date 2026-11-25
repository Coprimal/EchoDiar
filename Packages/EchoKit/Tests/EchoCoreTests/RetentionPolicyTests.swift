import EchoCore
import Foundation
import Testing

/// Guards auto-delete against data loss. Pure: no file is read or deleted.
struct RetentionPolicyTests {
    private let now = Date(timeIntervalSince1970: 1_800_000_000)
    private let day: TimeInterval = 86_400

    @Test func selectsOnlyConversationsOlderThanTheAge() {
        let old = conversation(ageDays: 31)
        let recent = conversation(ageDays: 29)
        let deletions = RetentionPolicy.select(
            conversations: [old, recent], now: now, settings: settings(.wholeConversation, days: 30), audioFiles: audio)
        #expect(deletions.map(\.conversationID) == [old.id])
        #expect(deletions.first?.items == [old.folder])
    }

    /// Audio and video trashes just what `audioFiles` and `videoFiles` list (tested on real folders in
    /// `LibraryStoreAudioFilesTests` and `LibraryStoreVideoTests`), never the folder; a conversation with neither left
    /// is skipped.
    @Test func audioAndVideoTrashesBothListedKindsAndSkipsConversationsWithNeither() throws {
        let both = conversation(ageDays: 120)
        let audioOnly = conversation(ageDays: 120)
        let videoOnly = conversation(ageDays: 120)
        let neither = conversation(ageDays: 120)
        let deletions = RetentionPolicy.select(
            conversations: [both, audioOnly, videoOnly, neither], now: now, settings: settings(.audioOnly, days: 90),
            audioFiles: { [both.id, audioOnly.id].contains($0.id) ? audio($0) : [] },
            videoFiles: { [both.id, videoOnly.id].contains($0.id) ? video($0) : [] })
        #expect(deletions.map(\.conversationID) == [both.id, audioOnly.id, videoOnly.id])
        #expect(deletions.allSatisfy { $0.mode == .audioOnly })
        #expect(deletions.map(\.items) == [audio(both) + video(both), audio(audioOnly), video(videoOnly)])
    }

    /// Video only never lists an audio file; a conversation with no video left is skipped.
    @Test func videoOnlyTrashesOnlyTheListedVideoAndSkipsConversationsWithout() throws {
        let withVideo = conversation(ageDays: 120)
        let withoutVideo = conversation(ageDays: 120)
        let deletions = RetentionPolicy.select(
            conversations: [withVideo, withoutVideo], now: now, settings: settings(.videoOnly, days: 90),
            audioFiles: audio, videoFiles: { $0.id == withVideo.id ? video($0) : [] })
        let deletion = try #require(deletions.first)
        #expect(deletions.count == 1)
        #expect(deletion.conversationID == withVideo.id)
        #expect(deletion.mode == .videoOnly)
        #expect(deletion.items == video(withVideo))
    }

    /// Task 2.4 warns before a change to a mode that deletes more.
    @Test func ranksOrderModesByHowMuchTheyDelete() {
        #expect(RetentionMode.videoOnly.rank < RetentionMode.audioOnly.rank)
        #expect(RetentionMode.audioOnly.rank < RetentionMode.wholeConversation.rank)
    }

    @Test(arguments: [
        Conversation.Status.recording, .compressing, .transcribing, .ready, .recovered, .compressionFailed,
        .transcriptionFailed, .failed, .unknown,
    ])
    func busyOrUntranscribedConversationsAreNeverSelected(status: Conversation.Status) {
        let old = conversation(ageDays: 400, status: status)
        for mode in [RetentionMode.videoOnly, .audioOnly, .wholeConversation] {
            #expect(RetentionPolicy.select(
                conversations: [old], now: now, settings: settings(mode, days: 1), audioFiles: audio,
                videoFiles: video).isEmpty)
        }
    }

    @Test func transcribedStatusWithoutTranscriptIsNeverSelected() {
        let old = conversation(ageDays: 400, hasTranscript: false)
        #expect(RetentionPolicy.select(
            conversations: [old], now: now, settings: settings(.wholeConversation, days: 1), audioFiles: audio).isEmpty)
    }

    private func settings(_ mode: RetentionMode, days: Int) -> AppSettings {
        AppSettings(autoDeleteEnabled: true, autoDeleteAfterDays: days, autoDeleteMode: mode)
    }

    private func conversation(
        ageDays: Double, status: Conversation.Status = .transcribed, hasTranscript: Bool = true
    ) -> Conversation {
        let id = UUID()
        return Conversation(
            id: id, createdAt: now.addingTimeInterval(-ageDays * day), status: status, hasTranscript: hasTranscript,
            hasSummary: true, folder: URL(fileURLWithPath: "/tmp/retention-\(id.uuidString)", isDirectory: true))
    }

    /// The audio files the policy is told the folder holds.
    private func audio(_ conversation: Conversation) -> [URL] {
        [conversation.folder.appendingPathComponent("audio.m4a"), conversation.folder.appendingPathComponent("audio.caf")]
    }

    /// The video files the policy is told the folder holds.
    private func video(_ conversation: Conversation) -> [URL] {
        [conversation.folder.appendingPathComponent("screen.mp4")]
    }
}
