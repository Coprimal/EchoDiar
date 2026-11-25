import AVFoundation
import EchoAudio
import EchoCore
import Foundation
import Observation

/// Plays the open transcript's audio file through an engine: both voices in both ears, or as recorded (mic left,
/// system right) with `PlaybackChannels.split`. The engine exists only between `load` and `close` and runs only
/// while playing; the play head is read at 4 Hz and only while playing, so nothing runs while paused.
///
/// The conversation's screen recording, when there is one, plays on a muted `videoPlayer` (video time is audio-file
/// time). With audio, the audio is the clock and the video follows it: started at the same host time, sought exactly
/// on pause and seek, and re-anchored by the ticker when it is more than `videoTolerance` away. Without audio, the
/// video player is the clock. While paused, a play head before the video's first frame (the empty edit at its start)
/// shows that first frame, so the pane is not blank.
@MainActor
@Observable
final class PlaybackController {
    private(set) var isPlaying = false
    /// Seconds; updated at most 4 times a second while playing.
    private(set) var currentTime: Double = 0
    private(set) var duration: Double = 0
    /// The segment under the play head; set only when the play head enters another segment, so the segment list
    /// is not redrawn at every tick.
    private(set) var activeSegmentID: UUID?
    /// An audio file is open, or (without one) the video is ready to play.
    private(set) var isReady = false
    /// The muted player of the conversation's video, for an `AVPlayerLayer`; nil without video.
    private(set) var videoPlayer: AVPlayer?
    /// Seconds of the video file (0 until it loaded, or without video).
    private(set) var videoDuration: Double = 0
    /// Plays the audio at volume 0 (the DEBUG harness).
    @ObservationIgnored var mutesSound = false

    @ObservationIgnored private var engine: AVAudioEngine?
    @ObservationIgnored private var player: AVAudioPlayerNode?
    @ObservationIgnored private var file: AVAudioFile?
    @ObservationIgnored private var url: URL?
    @ObservationIgnored private var channels: PlaybackChannels = .mixed
    /// Where the scheduled segment starts; the player's own time counts from there.
    @ObservationIgnored private var startFrame: AVAudioFramePosition = 0
    /// The segment from `startFrame` is on the player.
    @ObservationIgnored private var scheduled = false
    /// Bumped whenever the scheduled segment is dropped, so its late completion is ignored.
    @ObservationIgnored private var generation = 0
    @ObservationIgnored private var configurationObserver: (any NSObjectProtocol)?
    /// Sorted by start.
    @ObservationIgnored private var segments: [(id: UUID, start: Double, end: Double)] = []
    @ObservationIgnored private var ticker: Task<Void, Never>?
    /// A `seek(to:)` made before the player existed; applied when `load` opens one.
    @ObservationIgnored private var pendingSeek: Double?
    @ObservationIgnored private var videoURL: URL?
    @ObservationIgnored private var audioDuration: Double = 0
    @ObservationIgnored private var videoEndObserver: (any NSObjectProtocol)?
    @ObservationIgnored private var videoLoader: Task<Void, Never>?
    /// Seconds of the conversation where the video's first frame starts; 0 without video.
    @ObservationIgnored private var videoFirstFrame: Double = 0

    /// Above this distance from the audio, the ticker re-anchors the video.
    private static let videoTolerance = 0.12
    /// How far ahead the audio and the video are scheduled to start together.
    private static let videoLead = 0.05

    /// Opens `url` (audio) and `video` unless they are already open (nil and nil close the players), and sets the
    /// segments to highlight and how the channels play. `firstFrame` is where the video's picture starts (`VideoClip`).
    func load(url: URL?, video: URL?, firstFrame: Double = 0, segments: [Transcript.Segment],
              channels: PlaybackChannels) {
        self.segments = segments.map { ($0.id, $0.start, $0.end) }.sorted { $0.start < $1.start }
        if url != self.url || video != videoURL {
            // A seek made before this view's player opened (recording or compressing) still applies.
            let pending = pendingSeek
            close()
            pendingSeek = pending
            self.channels = channels
            if let url { open(url) }
            // Without audio, the pending seek waits for the video's duration (`videoLoaded`).
            if let video {
                videoFirstFrame = firstFrame
                openVideo(video)
            }
            if isReady, let pendingSeek {
                self.pendingSeek = nil
                seek(to: pendingSeek)
            } else if file != nil {
                // At rest at the start: the paused video shows its first frame.
                seekVideo(to: currentTime)
            }
        } else {
            setChannels(channels)
        }
        if isPlaying || currentTime > 0 { updateActiveSegment() }
    }

    /// Rebuilds the engine for `channels`, keeping the play head and whether it plays.
    func setChannels(_ channels: PlaybackChannels) {
        guard channels != self.channels else { return }
        self.channels = channels
        // The exact play head, not the last 4 Hz reading.
        tick()
        reopen()
    }

    /// Stops and releases the engine and the video player.
    func close() {
        ticker?.cancel()
        ticker = nil
        generation += 1
        releaseAudio()
        releaseVideo()
        isReady = false
        isPlaying = false
        currentTime = 0
        duration = 0
        activeSegmentID = nil
        pendingSeek = nil
    }

    func togglePlay() {
        if isPlaying { pause() } else { play() }
    }

    /// Plays from the play head; from the start when it is at the end.
    func play() {
        guard let engine, let player, let file, file.length > 0 else {
            if file == nil { playVideo() }
            return
        }
        if !scheduled {
            if startFrame >= file.length {
                startFrame = 0
                currentTime = 0
            }
            schedule(file, on: player)
        }
        do {
            try engine.start()
        } catch {
            NSLog("EchoDiar: could not start playback: \(error)")
            return
        }
        if let videoPlayer {
            // Both start at the same host time, the video at the audio's play head.
            let host = mach_absolute_time() + AVAudioTime.hostTime(forSeconds: Self.videoLead)
            player.play(at: AVAudioTime(hostTime: host))
            videoPlayer.setRate(1, time: Self.cmTime(currentTime), atHostTime: CMClockMakeHostTimeFromSystemUnits(host))
        } else {
            player.play()
        }
        isPlaying = true
        updateActiveSegment()
        startTicker()
    }

    func pause() {
        tick()
        player?.pause()
        engine?.pause()
        ticker?.cancel()
        ticker = nil
        isPlaying = false
        if let videoPlayer {
            videoPlayer.pause()
            if file != nil {
                seekVideo(to: currentTime)
            } else if let time = videoTime() {
                currentTime = time
                updateActiveSegment()
                // Paused in the empty edit: the picture waits on the first frame.
                if time < videoFirstFrame { seekVideo(to: time) }
            }
        }
    }

    /// Seeks to `time` (a segment start) and plays.
    func play(from time: Double) {
        seek(to: time)
        if !isPlaying { play() }
    }

    /// Moves the play head to `time`, still playing if it was; before a player exists, `load` applies it once one
    /// opens.
    func seek(to time: Double) {
        if file == nil, videoPlayer != nil, isReady {
            seekVideoClock(to: time)
            return
        }
        guard let player, let file else {
            pendingSeek = time
            currentTime = time
            updateActiveSegment()
            return
        }
        // The audio is the clock: the play head stays inside it (`startFrame` within the file).
        let time = min(max(0, time), audioDuration)
        let wasPlaying = isPlaying
        generation += 1
        player.stop()
        scheduled = false
        startFrame = AVAudioFramePosition(time * file.processingFormat.sampleRate)
        currentTime = time
        if wasPlaying {
            play()
        } else {
            seekVideo(to: time)
            updateActiveSegment()
        }
    }

    /// The exact play head now (the audio's, from its last render, or the video's), also set as `currentTime`.
    func exactTime() -> Double {
        tick()
        return currentTime
    }

    private func open(_ url: URL) {
        self.url = url
        do {
            let file = try AVAudioFile(forReading: url)
            let engine = AVAudioEngine()
            let player = AVAudioPlayerNode()
            PlaybackGraph.connect(player, to: engine, format: file.processingFormat, channels: channels)
            engine.prepare()
            self.file = file
            self.engine = engine
            self.player = player
            if mutesSound { engine.mainMixerNode.outputVolume = 0 }
            audioDuration = Double(file.length) / file.processingFormat.sampleRate
            duration = max(audioDuration, videoDuration)
            isReady = true
            // A new output device (headphones plugged in or out) stops the engine.
            configurationObserver = NotificationCenter.default.addObserver(
                forName: .AVAudioEngineConfigurationChange, object: engine, queue: .main
            ) { [weak self] _ in
                MainActor.assumeIsolated { self?.reopen() }
            }
        } catch {
            NSLog("EchoDiar: could not open \(url.lastPathComponent) for playback: \(error)")
        }
    }

    /// Rebuilds the engine for the open file, keeping the play head, whether it plays and the video player.
    private func reopen() {
        guard let url else { return }
        let time = currentTime
        let wasPlaying = isPlaying
        ticker?.cancel()
        ticker = nil
        generation += 1
        videoPlayer?.pause()
        releaseAudio()
        isReady = false
        isPlaying = false
        currentTime = 0
        duration = videoDuration
        activeSegmentID = nil
        pendingSeek = nil
        open(url)
        seek(to: time)
        if wasPlaying { play() }
    }

    /// Stops and releases the engine and its file.
    private func releaseAudio() {
        if let configurationObserver { NotificationCenter.default.removeObserver(configurationObserver) }
        configurationObserver = nil
        player?.stop()
        engine?.stop()
        engine = nil
        player = nil
        file = nil
        url = nil
        startFrame = 0
        scheduled = false
        audioDuration = 0
    }

    // MARK: Video

    /// A muted player for `url`; its duration loads asynchronously (`videoLoaded`).
    private func openVideo(_ url: URL) {
        videoURL = url
        let asset = AVURLAsset(url: url)
        let item = AVPlayerItem(asset: asset)
        let videoPlayer = AVPlayer(playerItem: item)
        videoPlayer.isMuted = true
        // `setRate(_:time:atHostTime:)` requires it.
        videoPlayer.automaticallyWaitsToMinimizeStalling = false
        videoPlayer.actionAtItemEnd = .pause
        self.videoPlayer = videoPlayer
        videoEndObserver = NotificationCenter.default.addObserver(
            forName: AVPlayerItem.didPlayToEndTimeNotification, object: item, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.videoEnded() }
        }
        videoLoader = Task { [weak self] in
            let seconds: Double
            do {
                seconds = try await asset.load(.duration).seconds
            } catch {
                NSLog("EchoDiar: could not open \(url.lastPathComponent) for playback: \(error)")
                return
            }
            guard let self, !Task.isCancelled, self.videoPlayer === videoPlayer, seconds.isFinite else { return }
            videoLoaded(seconds)
        }
    }

    private func videoLoaded(_ seconds: Double) {
        videoDuration = seconds
        duration = max(audioDuration, videoDuration)
        // Without audio the video is the clock: the transport works from now on.
        guard file == nil else { return }
        isReady = true
        if let pendingSeek {
            self.pendingSeek = nil
            seek(to: pendingSeek)
        } else if !isPlaying {
            // At rest at the start: the paused video shows its first frame.
            seekVideo(to: currentTime)
        }
    }

    private func releaseVideo() {
        videoLoader?.cancel()
        videoLoader = nil
        if let videoEndObserver { NotificationCenter.default.removeObserver(videoEndObserver) }
        videoEndObserver = nil
        videoPlayer?.pause()
        videoPlayer?.replaceCurrentItem(with: nil)
        videoPlayer = nil
        videoURL = nil
        videoDuration = 0
        videoFirstFrame = 0
    }

    /// The video player's time, nil when it has none.
    private func videoTime() -> Double? {
        guard let seconds = videoPlayer?.currentTime().seconds, seconds.isFinite else { return nil }
        return min(max(0, seconds), duration)
    }

    /// Moves the video to `time` exactly (it follows the audio). While paused, a time before the first frame moves it
    /// to the first frame instead: the empty edit before it has no picture. Playing keeps the exact time (`play` and
    /// `followAudio` set it with `setRate`, `playVideo` seeks back to the play head).
    private func seekVideo(to time: Double) {
        guard let videoPlayer else { return }
        // Rounded up, so the seek never lands a tick before the frame, in the empty edit.
        let firstFrame = CMTime(
            value: CMTimeValue((videoFirstFrame * Self.timescale).rounded(.up)), timescale: CMTimeScale(Self.timescale))
        let target = !isPlaying && time < videoFirstFrame ? firstFrame : Self.cmTime(time)
        videoPlayer.seek(to: target, toleranceBefore: .zero, toleranceAfter: .zero)
    }

    /// Without audio: plays the video from the play head; from the start when it is at the end.
    private func playVideo() {
        guard let videoPlayer, isReady else { return }
        let restarts = currentTime >= duration
        if restarts { currentTime = 0 }
        isPlaying = true
        // Paused before the first frame, the player waits on that frame (`seekVideo`): it starts from the play head.
        if restarts || currentTime < videoFirstFrame { seekVideo(to: currentTime) }
        videoPlayer.play()
        updateActiveSegment()
        startTicker()
    }

    /// Without audio: moves the video (the clock) to `time`, still playing if it was.
    private func seekVideoClock(to time: Double) {
        let time = min(max(0, time), duration)
        seekVideo(to: time)
        currentTime = time
        updateActiveSegment()
    }

    /// With audio, re-anchors the video at the audio's play head when it is more than `videoTolerance` away and the
    /// play head is inside the video.
    private func followAudio() {
        guard isPlaying, let videoPlayer, file != nil, currentTime < videoDuration,
              let video = videoTime(), abs(video - currentTime) > Self.videoTolerance else { return }
        let host = mach_absolute_time() + AVAudioTime.hostTime(forSeconds: Self.videoLead)
        guard let audio = audioTime(atHost: host) else { return }
        videoPlayer.setRate(1, time: Self.cmTime(audio), atHostTime: CMClockMakeHostTimeFromSystemUnits(host))
    }

    /// The audio's play head at `host`, from its last render; nil before the player rendered.
    private func audioTime(atHost host: UInt64) -> Double? {
        guard let player, let file, let nodeTime = player.lastRenderTime, nodeTime.isHostTimeValid,
              let time = player.playerTime(forNodeTime: nodeTime) else { return nil }
        let rendered = Double(startFrame + max(0, time.sampleTime)) / file.processingFormat.sampleRate
        return rendered + AVAudioTime.seconds(forHostTime: host) - AVAudioTime.seconds(forHostTime: nodeTime.hostTime)
    }

    /// Without audio, the end of the video: stops, clears the highlight and rewinds (as `finished`).
    private func videoEnded() {
        guard file == nil, isPlaying else { return }
        ticker?.cancel()
        ticker = nil
        isPlaying = false
        currentTime = 0
        seekVideo(to: 0)
        if activeSegmentID != nil { activeSegmentID = nil }
    }

    private static let timescale: Double = 90_000

    private static func cmTime(_ seconds: Double) -> CMTime {
        CMTime(seconds: seconds, preferredTimescale: CMTimeScale(timescale))
    }

    /// Schedules the file from `startFrame` to its end.
    private func schedule(_ file: AVAudioFile, on player: AVAudioPlayerNode) {
        generation += 1
        let generation = generation
        player.scheduleSegment(
            file, startingFrame: startFrame, frameCount: AVAudioFrameCount(file.length - startFrame), at: nil,
            completionCallbackType: .dataPlayedBack
        ) { [weak self] _ in
            Task { @MainActor in self?.finished(generation) }
        }
        scheduled = true
    }

    /// The end of the file: stops, clears the highlight and rewinds.
    private func finished(_ generation: Int) {
        guard generation == self.generation else { return }
        ticker?.cancel()
        ticker = nil
        player?.stop()
        engine?.pause()
        startFrame = 0
        scheduled = false
        isPlaying = false
        currentTime = 0
        videoPlayer?.pause()
        seekVideo(to: 0)
        if activeSegmentID != nil { activeSegmentID = nil }
    }

    private func startTicker() {
        ticker?.cancel()
        ticker = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .milliseconds(250))
                guard let self, !Task.isCancelled else { return }
                tick()
                if !isPlaying { return }
                followAudio()
            }
        }
    }

    /// Reads the play head while playing (the video's without audio).
    private func tick() {
        if file == nil {
            guard isPlaying, let time = videoTime() else { return }
            currentTime = time
            updateActiveSegment()
            return
        }
        guard isPlaying, let player, let file, let nodeTime = player.lastRenderTime,
              let time = player.playerTime(forNodeTime: nodeTime) else { return }
        // Negative for a moment while a start scheduled `videoLead` ahead has not begun.
        currentTime = min(Double(startFrame + max(0, time.sampleTime)) / file.processingFormat.sampleRate, duration)
        updateActiveSegment()
    }

    /// The last segment starting at or before the play head, while the play head is inside it.
    private func updateActiveSegment() {
        var low = 0
        var high = segments.count
        while low < high {
            let mid = (low + high) / 2
            if segments[mid].start <= currentTime { low = mid + 1 } else { high = mid }
        }
        let id = low > 0 && currentTime <= segments[low - 1].end ? segments[low - 1].id : nil
        if id != activeSegmentID { activeSegmentID = id }
    }
}
