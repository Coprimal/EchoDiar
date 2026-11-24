import AppKit
import AVFoundation
import EchoAudio
import EchoCore
import EchoScreen
import SwiftUI
import UniformTypeIdentifiers

extension VideoExporter.Audio {
    /// The folder's audio as the transcription finds it: the session's `audioFileName`; with `audio-mic.m4a`, its
    /// `audio-system.m4a` beside it; a stereo file holds ch0 mic and ch1 system, a mono one is the system audio. nil
    /// when there is no audio. Reads the file's header: call off the main thread.
    static func find(in folder: URL) -> VideoExporter.Audio? {
        guard let session = try? RecordingSession.read(from: folder),
              let url = existing(folder, session.audioFileName) else { return nil }
        if session.audioFileName == AudioCompressor.micFileName {
            let system = existing(folder, AudioCompressor.systemFileName)
            return VideoExporter.Audio(
                mic: VideoExporter.Source(url: url, channel: 0),
                system: system.map { VideoExporter.Source(url: $0, channel: 0) })
        }
        switch (try? AVAudioFile(forReading: url))?.fileFormat.channelCount ?? 0 {
        case 0: return nil
        case 1: return VideoExporter.Audio(mic: nil, system: VideoExporter.Source(url: url, channel: 0))
        default: return .stereo(url)
        }
    }

    private static func existing(_ folder: URL, _ name: String) -> URL? {
        ConversationFiles.audioURL(in: folder, fileName: name).flatMap {
            FileManager.default.fileExists(atPath: $0.path) ? $0 : nil
        }
    }
}

/// Exports a conversation's screen recording with its audio to an `.mp4` the user picks: Both, Microphone, System
/// audio or No audio (the ones the conversation does not have are disabled), then a save panel, progress with
/// Cancel, and Show in Finder at the end. The audio is looked up and the file written off the main thread; closing
/// the sheet cancels the export, which removes its partial file.
struct VideoExportSheet: View {
    let title: String
    let folder: URL
    let video: URL
    @Environment(\.dismiss) private var dismiss

    @State private var audio: VideoExporter.Audio?
    /// The audio has been looked up; until then no option can be picked.
    @State private var resolved = false
    @State private var choice: VideoExporter.Channels = .both
    @State private var phase: Phase = .choosing
    @State private var progress = 0.0
    @State private var task: Task<Void, Never>?

    private enum Phase: Equatable {
        case choosing, exporting
        case done(URL)
        case failed(String)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Export Video").font(.title3.weight(.semibold))
            switch phase {
            case .choosing:
                Picker("Audio:", selection: $choice) {
                    ForEach(VideoExporter.Channels.allCases, id: \.self) { channels in
                        Text(Self.label(channels)).tag(channels).disabled(!offers(channels))
                    }
                }
                .pickerStyle(.radioGroup)
                .disabled(!resolved)
            case .exporting:
                Text("Exporting…").foregroundStyle(.secondary)
                ProgressView(value: progress)
            case .done(let url):
                Label(url.lastPathComponent, systemImage: "checkmark.circle.fill")
                    .lineLimit(1)
                    .truncationMode(.middle)
            case .failed(let message):
                Text(message).font(.callout).foregroundStyle(.red)
            }
            buttons
        }
        .padding(20)
        .frame(width: 380, alignment: .leading)
        .buttonBorderShape(.capsule)
        .task { await resolve() }
        .onDisappear { task?.cancel() }
    }

    @ViewBuilder
    private var buttons: some View {
        HStack {
            Spacer()
            switch phase {
            case .choosing:
                Button("Cancel") { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("Export…", action: chooseFile)
                    .keyboardShortcut(.defaultAction)
                    .disabled(!resolved || !offers(choice))
            case .exporting:
                Button("Cancel") {
                    task?.cancel()
                    dismiss()
                }
                .keyboardShortcut(.cancelAction)
            case .done(let url):
                Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([url]) }
                Button("Done") { dismiss() }
                    .keyboardShortcut(.defaultAction)
            case .failed:
                Button("Close") { dismiss() }
                    .keyboardShortcut(.cancelAction)
            }
        }
    }

    private static func label(_ channels: VideoExporter.Channels) -> LocalizedStringKey {
        switch channels {
        case .both: "Both"
        case .mic: "Microphone"
        case .system: "System audio"
        case .none: "No audio"
        }
    }

    private func offers(_ channels: VideoExporter.Channels) -> Bool {
        channels == .none || audio?.sources(for: channels) != nil
    }

    private func resolve() async {
        let folder = folder
        let found = await Task.detached(priority: .userInitiated) { VideoExporter.Audio.find(in: folder) }.value
        audio = found
        choice = [.both, .system, .mic].first { found?.sources(for: $0) != nil } ?? .none
        resolved = true
    }

    /// The conversation's title, without the characters a file name cannot hold, plus ".mp4".
    private var defaultFileName: String {
        let name = title.replacingOccurrences(of: "/", with: "-").replacingOccurrences(of: ":", with: "-")
            .trimmingCharacters(in: .whitespacesAndNewlines)
        return (name.isEmpty ? "Recording" : name) + ".mp4"
    }

    private func chooseFile() {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.mpeg4Movie]
        panel.nameFieldStringValue = defaultFileName
        panel.canCreateDirectories = true
        let handler: (NSApplication.ModalResponse) -> Void = { response in
            guard response == .OK, let url = panel.url else { return }
            // Never over the recording or its audio: nothing is written inside the conversation's folder.
            let folderPath = folder.standardizedFileURL.resolvingSymlinksInPath().path + "/"
            guard !url.standardizedFileURL.resolvingSymlinksInPath().path.hasPrefix(folderPath) else {
                phase = .failed(String(localized: "Choose a place outside the conversation's folder."))
                return
            }
            start(url)
        }
        if let window = NSApp.keyWindow {
            panel.beginSheetModal(for: window, completionHandler: handler)
        } else {
            panel.begin(completionHandler: handler)
        }
    }

    private func start(_ destination: URL) {
        phase = .exporting
        progress = 0
        let video = video
        let audio = audio
        let choice = choice
        task = Task {
            let (updates, sink) = AsyncStream.makeStream(of: Double.self, bufferingPolicy: .bufferingNewest(1))
            let watcher = Task { for await value in updates { progress = value } }
            defer { watcher.cancel() }
            do {
                try await VideoExporter.export(video: video, audio: audio, channels: choice, to: destination) {
                    sink.yield($0)
                }
                sink.finish()
                phase = .done(destination)
            } catch is CancellationError {
                sink.finish()
            } catch {
                sink.finish()
                NSLog("EchoDiar: video export failed: \(error)")
                phase = .failed(String(localized: "The video could not be exported."))
            }
        }
    }
}
