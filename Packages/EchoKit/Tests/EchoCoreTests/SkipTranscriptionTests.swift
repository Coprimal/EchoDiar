import EchoCore
import Foundation
import Testing

/// "Screen only" recordings carry the `skipsTranscription` mark: never transcribed automatically, whatever
/// "Transcribe automatically" says. Old files have neither the mark nor the `video` key.
struct SkipTranscriptionTests {
    @Test func markedSessionNeverAutoTranscribes() {
        var on = AppSettings()
        on.autoTranscribe = true
        var off = AppSettings()
        off.autoTranscribe = false
        #expect(!RecordingSession(skipsTranscription: true).autoTranscribes(with: on))
        #expect(RecordingSession().autoTranscribes(with: on))
        #expect(!RecordingSession().autoTranscribes(with: off))
        #expect(!RecordingSession.autoTranscribes(skips: true, settings: on))
    }

    @Test func sessionWithoutTheKeysDecodesAsUnmarked() throws {
        let json = #"{"id":"6F1C2A9E-3B4D-4E5F-8A7B-1C2D3E4F5A6B","pid":42,"startedAt":800000000}"#
        let session = try JSONDecoder().decode(RecordingSession.self, from: Data(json.utf8))
        #expect(!session.skipsTranscription)
        #expect(session.video == nil)
    }

    /// A malformed `video` block (missing key, wrong type, truncated write) must not make the whole session
    /// unreadable: the recording keeps its audio, title and mark, and reads as audio only.
    @Test func malformedVideoDecodesAsNoVideoAndKeepsTheRest() throws {
        let json = #"""
        {"id":"6F1C2A9E-3B4D-4E5F-8A7B-1C2D3E4F5A6B","pid":42,"startedAt":800000000,"title":"Standup",
         "skipsTranscription":true,"video":{"fileName":"video.mp4","source":"display","quality":"high"}}
        """#
        let session = try JSONDecoder().decode(RecordingSession.self, from: Data(json.utf8))
        #expect(session.video == nil)
        #expect(session.skipsTranscription)
        #expect(session.title == "Standup")
        #expect(session.pid == 42)
    }

    @Test func bothKeysSurviveTheSessionAndTheConversation() throws {
        let folder = FileManager.default.temporaryDirectory
            .appendingPathComponent("SkipTranscriptionTests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        var session = RecordingSession(state: .stopped, skipsTranscription: true)
        session.video = .init(fileName: "video.mp4", source: .area, fps: 15, quality: 0.75, width: 2880, height: 1800)
        try session.write(to: folder)
        #expect(try RecordingSession.read(from: folder) == session)

        try Conversation(session: session, folder: folder, duration: 12).write()
        #expect(try Conversation.read(from: folder).skipsTranscription)
    }
}
