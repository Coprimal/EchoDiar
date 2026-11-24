import AppKit
import AVFoundation
import EchoCore
import EchoScreen
import SwiftUI

/// A conversation's screen recording, found and measured off the main thread by `TranscriptView.load()`.
struct VideoClip: Equatable, Sendable {
    let url: URL
    /// Seconds of the conversation (audio-file time) that have a picture: from the first frame to the end of the last
    /// one. Not 0…duration: the file starts with an empty edit until the first frame.
    let shown: ClosedRange<Double>

    /// The folder's video file, `ScreenSource.videoFileName` first (as the harness finds it); nil when it has none.
    static func file(in folder: URL) -> URL? {
        let files = LibraryStore.videoFiles(in: folder)
        return files.first { $0.lastPathComponent == ScreenSource.videoFileName } ?? files.first
    }

    /// Loads the video track, its edits and the duration; nil when the file has no readable video track. Call off the
    /// main thread.
    static func load(_ url: URL) async -> VideoClip? {
        let asset = AVURLAsset(url: url)
        do {
            guard let track = try await asset.loadTracks(withMediaType: .video).first else { return nil }
            let duration = try await asset.load(.duration).seconds
            let filled = try await track.load(.segments).filter { !$0.isEmpty }.map(\.timeMapping.target)
            let start = filled.map(\.start.seconds).min() ?? 0
            let end = filled.map(\.end.seconds).max() ?? duration
            guard start.isFinite, end.isFinite, end > start else { return nil }
            return VideoClip(url: url, shown: start...end)
        } catch {
            NSLog("EchoDiar: could not read the video \(url.lastPathComponent): \(error)")
            return nil
        }
    }
}

/// The conversation's screen recording beside or above the transcript: the picture of `playback.videoPlayer`,
/// aspect-fit in a card like the transcript's, without system controls; a click plays or pauses. A placeholder of the
/// same card shows at once and its spinner only after 150 ms; "No screen recording at this point" shows while the
/// play head is outside the recorded picture (a video that started late or stopped early).
struct VideoPaneView: View {
    let playback: PlaybackController
    /// nil until `TranscriptView.load()` measured the file.
    let clip: VideoClip?
    /// For the DEBUG timing log.
    let conversationID: UUID
    @State private var layerReady = false

    private static let cornerRadius: CGFloat = 20

    var body: some View {
        let opened = clip != nil && playback.videoPlayer != nil && playback.videoDuration > 0
        ZStack {
            if let player = playback.videoPlayer, clip != nil {
                PlayerLayerView(player: player) { layerReady = $0 }
            }
            PictureCover(playback: playback, shown: clip?.shown, opened: opened, layerReady: layerReady)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .clipShape(RoundedRectangle(cornerRadius: Self.cornerRadius, style: .continuous))
        .modifier(GlassCard(cornerRadius: Self.cornerRadius))
        .contentShape(Rectangle())
        .onTapGesture {
            guard playback.isReady else { return }
            playback.togglePlay()
        }
        // Keeps the note readable as text.
        .accessibilityElement(children: .contain)
        .accessibilityLabel(Text("Screen recording"))
        .accessibilityAction {
            guard playback.isReady else { return }
            playback.togglePlay()
        }
        #if DEBUG
        .onChange(of: opened, initial: true) { _, opened in
            if opened { DetailTiming.log("video-opened", conversationID) }
        }
        .onChange(of: layerReady, initial: true) { _, ready in
            if ready { DetailTiming.log("video-frame", conversationID) }
        }
        #endif
    }
}

/// What covers the picture: the placeholder until the video opened (and until its frame shows inside the picture's
/// range), the note while the play head is outside that range, else nothing. The only reader of `currentTime` in the
/// pane, so the 4 Hz updates redraw nothing else.
private struct PictureCover: View {
    let playback: PlaybackController
    let shown: ClosedRange<Double>?
    let opened: Bool
    let layerReady: Bool

    /// How far outside the picture's range the play head must be for the note: at rest at 0 it sits in the empty edit
    /// before the first frame (about 0.25 s in a recording), and the last frame shows until the audio ends.
    private static let edgeTolerance = 0.5

    private enum Cover { case loading, outside, picture }

    var body: some View {
        let cover = self.cover
        ZStack {
            switch cover {
            case .loading:
                Palette.surface
                DelayedSpinner()
            case .outside:
                Palette.surface
                Text("No screen recording at this point")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .padding(20)
            case .picture:
                Color.clear
            }
        }
    }

    private var cover: Cover {
        guard opened, let shown else { return .loading }
        let time = playback.currentTime
        if time < shown.lowerBound - Self.edgeTolerance || time > shown.upperBound + Self.edgeTolerance {
            return .outside
        }
        // Before the first frame (the empty edit) the layer may have nothing to show yet: the card stays empty.
        return layerReady || time < shown.lowerBound ? .picture : .loading
    }
}

/// A spinner that fades in only after 150 ms, so a fast load does not flash it.
private struct DelayedSpinner: View {
    @State private var visible = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        ProgressView()
            .opacity(visible ? 1 : 0)
            .task {
                try? await Task.sleep(for: .milliseconds(150))
                guard !Task.isCancelled else { return }
                withAnimation(reduceMotion ? nil : Motion.standard) { visible = true }
            }
    }
}

/// An `AVPlayerLayer` (aspect-fit, no controls) for `player`; reports whether its layer has a frame to show.
private struct PlayerLayerView: NSViewRepresentable {
    let player: AVPlayer
    let onReady: (Bool) -> Void

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeNSView(context: Context) -> PlayerNSView {
        let view = PlayerNSView()
        context.coordinator.onReady = onReady
        context.coordinator.observe(view.playerLayer)
        view.playerLayer.player = player
        return view
    }

    func updateNSView(_ view: PlayerNSView, context: Context) {
        context.coordinator.onReady = onReady
        if view.playerLayer.player !== player { view.playerLayer.player = player }
    }

    static func dismantleNSView(_ view: PlayerNSView, coordinator: Coordinator) {
        coordinator.observation = nil
        view.playerLayer.player = nil
    }

    @MainActor
    final class Coordinator {
        var onReady: ((Bool) -> Void)?
        var observation: NSKeyValueObservation?

        func observe(_ layer: AVPlayerLayer) {
            observation = layer.observe(\.isReadyForDisplay, options: [.initial, .new]) { [weak self] layer, _ in
                let ready = layer.isReadyForDisplay
                Task { @MainActor in self?.onReady?(ready) }
            }
        }
    }
}

/// Hosts the player layer; lets clicks through to the pane's tap gesture.
private final class PlayerNSView: NSView {
    let playerLayer = AVPlayerLayer()

    init() {
        super.init(frame: .zero)
        playerLayer.videoGravity = .resizeAspect
        // Layer-hosting: the layer is set before `wantsLayer`.
        layer = playerLayer
        wantsLayer = true
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) is not used") }

    override func hitTest(_ point: NSPoint) -> NSView? { nil }
}

#if DEBUG
/// DEBUG timings of opening a conversation (VM checks of "click reacts first, then loading state"): `AppModel.open`
/// stamps the open (with how long after the click's event), and the Transcript tab logs its placeholder (at appear),
/// its content, the video opened (asset measured, player loaded) and the layer's first frame once each, in milliseconds after it, on `ProcessInfo.systemUptime` (the clock of
/// `NSEvent.timestamp`). Read with `log stream --predicate 'eventMessage CONTAINS "EchoDiar timing"'`.
@MainActor
enum DetailTiming {
    private static var opened: (id: UUID, at: TimeInterval)?
    private static var logged: Set<String> = []

    static func open(_ id: UUID) {
        let now = ProcessInfo.processInfo.systemUptime
        opened = (id, now)
        logged = []
        let event = NSApp.currentEvent.map { String(format: " (event +%.1f ms)", (now - $0.timestamp) * 1000) } ?? ""
        NSLog("EchoDiar timing: open %@%@", id.uuidString, event)
    }

    /// Logs `stage` the first time it happens after the open of `id`.
    static func log(_ stage: String, _ id: UUID) {
        guard let opened, opened.id == id, !logged.contains(stage) else { return }
        logged.insert(stage)
        let elapsed = (ProcessInfo.processInfo.systemUptime - opened.at) * 1000
        NSLog("EchoDiar timing: %@ %@ +%.1f ms", stage, id.uuidString, elapsed)
    }
}
#endif
