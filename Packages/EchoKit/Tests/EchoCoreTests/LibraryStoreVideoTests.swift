import EchoCore
import Foundation
import Testing

/// A screen recording's video can outlive its audio ("Delete audio", delete audio after transcription): the library
/// must keep it when the audio and the transcripts go, delete it on its own, and count it in its own bucket.
struct LibraryStoreVideoTests {
    @Test func videoOutlivesAudioAndTranscriptsAndIsCountedApart() async throws {
        let fileManager = FileManager.default
        let root = fileManager.temporaryDirectory.appendingPathComponent("video-\(UUID().uuidString)", isDirectory: true)
        let folder = root.appendingPathComponent("conversation", isDirectory: true)
        try fileManager.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? fileManager.removeItem(at: root) }
        let conversation = Conversation(id: UUID(), createdAt: Date(), hasTranscript: true, folder: folder)
        try conversation.write()
        try Data(repeating: 1, count: 10_000).write(to: folder.appendingPathComponent("audio.m4a"))
        try Data(repeating: 2, count: 50_000).write(to: folder.appendingPathComponent("video.mp4"))
        try Data("{}".utf8).write(to: folder.appendingPathComponent(Transcript.fileName))
        let video = folder.appendingPathComponent("video.mp4")
        let store = LibraryStore(root: root)
        await store.refresh()

        let usage = await store.usage { 7 }
        let onDisk = LibraryStore.sizeOnDisk(of: folder)
        #expect(usage.video > usage.audio)
        #expect(usage.total == onDisk + 7)
        let perConversation = try #require(usage.conversations.first)
        #expect(perConversation.video == usage.video)
        #expect(perConversation.audio + perConversation.video <= perConversation.total)

        try await store.deleteAudio(id: conversation.id)
        #expect(await store.conversations.first?.hasVideo == true)
        try await store.deleteTranscriptsAndAI(id: conversation.id)
        #expect(fileManager.fileExists(atPath: video.path))
        #expect(await store.conversations.first?.hasVideo == true)

        try await store.deleteVideo(id: conversation.id)
        #expect(!fileManager.fileExists(atPath: video.path))
        #expect(fileManager.fileExists(atPath: folder.appendingPathComponent(Conversation.fileName).path))
        #expect(await store.conversations.first?.hasVideo == false)
    }

    /// Auto-delete "Audio and video" (raw `audioOnly`) trashes both kinds; "Video only" just the video. The transcript
    /// and the folder stay in both.
    @Test(arguments: [(RetentionMode.audioOnly, false), (.videoOnly, true)])
    func autoDeleteTrashesTheModesMediaAndKeepsTheRest(mode: RetentionMode, keepsAudio: Bool) async throws {
        let fileManager = FileManager.default
        let root = fileManager.temporaryDirectory.appendingPathComponent("video-\(UUID().uuidString)", isDirectory: true)
        let folder = root.appendingPathComponent("conversation", isDirectory: true)
        try fileManager.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? fileManager.removeItem(at: root) }
        let now = Date()
        let conversation = Conversation(
            id: UUID(), createdAt: now.addingTimeInterval(-100 * 86_400), status: .transcribed, hasTranscript: true,
            folder: folder)
        try conversation.write()
        let audio = folder.appendingPathComponent("audio.m4a")
        let video = folder.appendingPathComponent("video.mp4")
        let transcript = folder.appendingPathComponent(Transcript.fileName)
        try Data(repeating: 1, count: 1_000).write(to: audio)
        try Data(repeating: 2, count: 1_000).write(to: video)
        try Data("{}".utf8).write(to: transcript)
        let store = LibraryStore(root: root)

        let done = await store.applyRetention(
            settings: AppSettings(autoDeleteEnabled: true, autoDeleteAfterDays: 30, autoDeleteMode: mode), now: now)

        #expect(done.map(\.conversationID) == [conversation.id])
        #expect(fileManager.fileExists(atPath: audio.path) == keepsAudio)
        #expect(!fileManager.fileExists(atPath: video.path))
        #expect(fileManager.fileExists(atPath: transcript.path))
        #expect(fileManager.fileExists(atPath: folder.appendingPathComponent(Conversation.fileName).path))
        #expect(await store.conversations.first?.hasVideo == false)
        #expect(await store.conversations.first?.hasAudio == keepsAudio)
    }
}
