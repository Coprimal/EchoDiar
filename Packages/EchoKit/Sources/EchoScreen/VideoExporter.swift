import AVFoundation
import CoreMedia

public enum VideoExporterError: Error, Sendable, Equatable {
    /// The video file has no readable video track.
    case noVideoTrack
    /// The chosen channels are not in the given audio (a missing file, or a channel the file does not have).
    case audioUnavailable
    /// The mic and system files have different sample rates.
    case sampleRateMismatch
    /// The destination is the video or one of the audio files being read.
    case destinationIsSource
    /// Reading or writing failed; the message is AVFoundation's when it gave one.
    case failed(String)
}

/// Writes a conversation's screen recording with its audio into one `.mp4`: the video track copied as it is (no
/// re-encoding, same frames and timing, so a video that started late still starts late), plus one mono AAC track
/// decoded from the conversation's audio, with the microphone, the system audio, both mixed, or no audio track.
/// Video time is audio-file time, so both tracks start at zero.
public enum VideoExporter {
    /// Which audio goes into the export.
    public enum Channels: String, CaseIterable, Sendable {
        case both, mic, system, none
    }

    /// One channel of an audio file.
    public struct Source: Sendable, Equatable {
        public let url: URL
        public let channel: Int

        public init(url: URL, channel: Int) {
            self.url = url
            self.channel = channel
        }
    }

    /// A conversation's audio, already found on disk: the microphone and the system audio, either one may be missing.
    public struct Audio: Sendable, Equatable {
        public let mic: Source?
        public let system: Source?

        public init(mic: Source?, system: Source?) {
            self.mic = mic
            self.system = system
        }

        /// One file with ch0 mic and ch1 system (`audio.caf`, `audio.m4a`).
        public static func stereo(_ url: URL) -> Audio {
            Audio(mic: Source(url: url, channel: 0), system: Source(url: url, channel: 1))
        }

        /// The channels `choice` takes; nil when one of them is missing. Empty for `.none`.
        public func sources(for choice: Channels) -> [Source]? {
            switch choice {
            case .both: if let mic, let system { [mic, system] } else { nil }
            case .mic: mic.map { [$0] }
            case .system: system.map { [$0] }
            case .none: []
            }
        }
    }

    /// What an export wrote, for the DEBUG harness.
    public struct Summary: Sendable {
        /// Video samples copied.
        public let videoSamples: Int
        /// Presentation time of the first video sample in the output, seconds; nil without one.
        public let firstVideoTime: Double?
        /// Seconds of audio written; 0 without an audio track.
        public let audioSeconds: Double
    }

    private static let chunkFrames: AVAudioFrameCount = 4_096

    /// Writes `video` plus the `channels` of `audio` to `destination` (an `.mp4`, replaced if it exists, but never
    /// one of the files being read: `destinationIsSource`). Works in a
    /// hidden temp file next to `destination` that is moved into place at the end and removed on failure or
    /// cancellation. `progress` gets 0…1 from the exporting task. Both channels are mixed at half gain each, which
    /// cannot clip. Call off the main thread.
    @discardableResult
    public static func export(
        video: URL, audio: Audio?, channels: Channels, to destination: URL,
        progress: @escaping @Sendable (Double) -> Void = { _ in }
    ) async throws -> Summary {
        let inputs = [video, audio?.mic?.url, audio?.system?.url].compactMap { $0 }.map(resolved)
        guard !inputs.contains(resolved(destination)) else { throw VideoExporterError.destinationIsSource }
        guard let sources = channels == .none ? [] : audio?.sources(for: channels) else {
            throw VideoExporterError.audioUnavailable
        }
        let name = destination.deletingPathExtension().lastPathComponent
        let temp = destination.deletingLastPathComponent().appendingPathComponent(".\(name)-\(UUID().uuidString).mp4")
        do {
            let summary = try await write(video: video, sources: sources, to: temp, progress: progress)
            try Task.checkCancellation()
            if FileManager.default.fileExists(atPath: destination.path) {
                _ = try FileManager.default.replaceItemAt(destination, withItemAt: temp)
            } else {
                try FileManager.default.moveItem(at: temp, to: destination)
            }
            progress(1)
            return summary
        } catch {
            try? FileManager.default.removeItem(at: temp)
            throw error
        }
    }

    private static func resolved(_ url: URL) -> String {
        url.standardizedFileURL.resolvingSymlinksInPath().path
    }

    // MARK: - Writing

    private static func write(
        video: URL, sources: [Source], to url: URL, progress: @Sendable (Double) -> Void
    ) async throws -> Summary {
        let asset = AVURLAsset(url: video)
        guard let track = try await asset.loadTracks(withMediaType: .video).first else {
            throw VideoExporterError.noVideoTrack
        }
        let (formats, transform) = try await track.load(.formatDescriptions, .preferredTransform)
        let duration = try await asset.load(.duration)

        let reader = try AVAssetReader(asset: asset)
        let videoOutput = AVAssetReaderTrackOutput(track: track, outputSettings: nil)
        videoOutput.alwaysCopiesSampleData = false
        guard reader.canAdd(videoOutput) else { throw VideoExporterError.failed("cannot read the video track") }
        reader.add(videoOutput)

        let writer = try AVAssetWriter(outputURL: url, fileType: .mp4)
        writer.shouldOptimizeForNetworkUse = true
        let videoInput = AVAssetWriterInput(mediaType: .video, outputSettings: nil, sourceFormatHint: formats.first)
        videoInput.transform = transform
        guard writer.canAdd(videoInput) else { throw VideoExporterError.failed("cannot copy the video track") }
        writer.add(videoInput)

        let mixer = try sources.isEmpty ? nil : AudioMixer(sources: sources)
        var audioInput: AVAssetWriterInput?
        if let mixer {
            let settings: [String: Any] = [
                AVFormatIDKey: kAudioFormatMPEG4AAC,
                AVSampleRateKey: mixer.sampleRate,
                // At the source's rate (16 kHz from the recorder). No AVEncoderBitRateKey: the encoder picks one that
                // suits the rate; 64 to 128 kbit/s at 16 kHz mono fail with "Cannot Encode Media" (-12651).
                AVNumberOfChannelsKey: 1,
            ]
            guard writer.canApply(outputSettings: settings, forMediaType: .audio) else {
                throw VideoExporterError.failed("the AAC encoder refused \(Int(mixer.sampleRate)) Hz mono")
            }
            let input = AVAssetWriterInput(mediaType: .audio, outputSettings: settings)
            guard writer.canAdd(input) else { throw VideoExporterError.failed("cannot add the audio track") }
            writer.add(input)
            audioInput = input
        }

        guard reader.startReading() else { throw reader.error ?? VideoExporterError.failed("cannot start reading") }
        guard writer.startWriting() else {
            reader.cancelReading()
            throw writer.error ?? VideoExporterError.failed("cannot start writing")
        }
        writer.startSession(atSourceTime: .zero)

        var videoDone = false
        var audioDone = audioInput == nil
        var videoSamples = 0
        var firstVideoTime: Double?
        var videoTime = 0.0
        var audioFrames: Int64 = 0
        var lastReport = -1.0
        do {
            // One loop feeds both inputs: the writer interleaves them and holds back whichever runs ahead.
            while !videoDone || !audioDone {
                try Task.checkCancellation()
                var fed = false
                while !videoDone, videoInput.isReadyForMoreMediaData {
                    fed = true
                    guard let sample = videoOutput.copyNextSampleBuffer() else {
                        if reader.status == .failed { throw reader.error ?? VideoExporterError.failed("reading") }
                        videoInput.markAsFinished()
                        videoDone = true
                        break
                    }
                    // Edit markers (no samples) are skipped: the samples carry their output times.
                    guard CMSampleBufferGetNumSamples(sample) > 0 else { continue }
                    // The output time has the source's edits applied: a video that started late starts late.
                    let time = CMSampleBufferGetOutputPresentationTimeStamp(sample).seconds
                    if firstVideoTime == nil { firstVideoTime = time }
                    if time.isFinite { videoTime = max(videoTime, time) }
                    guard videoInput.append(sample) else { throw writer.error ?? VideoExporterError.failed("writing") }
                    videoSamples += 1
                }
                if let input = audioInput, let mixer, !audioDone, input.isReadyForMoreMediaData {
                    fed = true
                    if let buffer = try mixer.next(chunk: chunkFrames, at: audioFrames) {
                        guard input.append(buffer) else { throw writer.error ?? VideoExporterError.failed("writing") }
                        audioFrames += Int64(CMSampleBufferGetNumSamples(buffer))
                    } else {
                        input.markAsFinished()
                        audioDone = true
                    }
                }
                let video = videoFraction(videoTime, duration.seconds, videoDone)
                let done = mixer.map { (Double(audioFrames) / Double(max($0.length, 1)) + video) / 2 } ?? video
                if done - lastReport >= 0.01 {
                    lastReport = done
                    progress(min(done, 0.99))
                }
                if !fed { try await Task.sleep(for: .milliseconds(2)) }
            }
            // The source's end (its last frame is cut there), or the audio's when that is later.
            let audioEnd = mixer.map { CMTime(value: audioFrames, timescale: CMTimeScale($0.sampleRate)) } ?? .zero
            writer.endSession(atSourceTime: max(duration, audioEnd))
            await writer.finishWriting()
            guard writer.status == .completed else { throw writer.error ?? VideoExporterError.failed("writing") }
        } catch {
            reader.cancelReading()
            if writer.status == .writing { writer.cancelWriting() }
            throw error
        }
        return Summary(
            videoSamples: videoSamples, firstVideoTime: firstVideoTime,
            audioSeconds: mixer.map { Double(audioFrames) / $0.sampleRate } ?? 0)
    }

    private static func videoFraction(_ time: Double, _ duration: Double, _ done: Bool) -> Double {
        done ? 1 : duration > 0 ? min(time / duration, 1) : 0
    }
}

// MARK: - Audio

/// Reads the chosen channels in fixed chunks and hands out mono float LPCM sample buffers: one channel as it is, or
/// the average of two (half gain each, so the mix cannot clip). Files of different lengths are padded with silence.
private struct AudioMixer {
    private let files: [AVAudioFile]
    private let buffers: [AVAudioPCMBuffer]
    /// For each chosen source: the index of its file, and its channel.
    private let picks: [(file: Int, channel: Int)]
    private let format: CMAudioFormatDescription
    let sampleRate: Double
    /// Frames of the longest file.
    let length: Int64

    init(sources: [VideoExporter.Source]) throws {
        var urls: [URL] = []
        var files: [AVAudioFile] = []
        var picks: [(file: Int, channel: Int)] = []
        for source in sources {
            let index: Int
            if let existing = urls.firstIndex(of: source.url) {
                index = existing
            } else {
                guard FileManager.default.fileExists(atPath: source.url.path) else {
                    throw VideoExporterError.audioUnavailable
                }
                files.append(try AVAudioFile(forReading: source.url))
                urls.append(source.url)
                index = files.count - 1
            }
            guard source.channel >= 0, source.channel < Int(files[index].processingFormat.channelCount) else {
                throw VideoExporterError.audioUnavailable
            }
            picks.append((index, source.channel))
        }
        let rate = files[0].processingFormat.sampleRate
        guard files.allSatisfy({ $0.processingFormat.sampleRate == rate }) else {
            throw VideoExporterError.sampleRateMismatch
        }
        buffers = try files.map {
            guard let buffer = AVAudioPCMBuffer(pcmFormat: $0.processingFormat, frameCapacity: 4_096) else {
                throw VideoExporterError.failed("cannot allocate an audio buffer")
            }
            return buffer
        }
        var description = AudioStreamBasicDescription(
            mSampleRate: rate, mFormatID: kAudioFormatLinearPCM,
            mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked, mBytesPerPacket: 4, mFramesPerPacket: 1,
            mBytesPerFrame: 4, mChannelsPerFrame: 1, mBitsPerChannel: 32, mReserved: 0)
        var format: CMAudioFormatDescription?
        let status = CMAudioFormatDescriptionCreate(
            allocator: kCFAllocatorDefault, asbd: &description, layoutSize: 0, layout: nil, magicCookieSize: 0,
            magicCookie: nil, extensions: nil, formatDescriptionOut: &format)
        guard status == noErr, let format else { throw VideoExporterError.failed("audio format \(status)") }
        self.files = files
        self.picks = picks
        self.format = format
        sampleRate = rate
        length = files.map(\.length).max() ?? 0
    }

    /// The next `chunk` frames (fewer at the end) as a mono sample buffer presented at `frame`; nil once every file
    /// has been read.
    func next(chunk: AVAudioFrameCount, at frame: Int64) throws -> CMSampleBuffer? {
        let count = min(chunk, buffers[0].frameCapacity)
        var frames = 0
        for (file, buffer) in zip(files, buffers) {
            buffer.frameLength = 0
            if file.framePosition < file.length {
                try file.read(into: buffer, frameCount: count)
            }
            frames = max(frames, Int(buffer.frameLength))
        }
        guard frames > 0 else { return nil }
        var mono = [Float](repeating: 0, count: frames)
        let gain: Float = picks.count > 1 ? 1 / Float(picks.count) : 1
        for pick in picks {
            let buffer = buffers[pick.file]
            guard let data = buffer.floatChannelData?[pick.channel] else { continue }
            for i in 0..<Int(buffer.frameLength) { mono[i] += data[i] * gain }
        }
        return try sampleBuffer(mono, at: frame)
    }

    private func sampleBuffer(_ samples: [Float], at frame: Int64) throws -> CMSampleBuffer {
        let bytes = samples.count * MemoryLayout<Float>.size
        var block: CMBlockBuffer?
        var status = CMBlockBufferCreateWithMemoryBlock(
            allocator: kCFAllocatorDefault, memoryBlock: nil, blockLength: bytes, blockAllocator: kCFAllocatorDefault,
            customBlockSource: nil, offsetToData: 0, dataLength: bytes, flags: kCMBlockBufferAssureMemoryNowFlag,
            blockBufferOut: &block)
        guard status == noErr, let block else { throw VideoExporterError.failed("audio block \(status)") }
        status = samples.withUnsafeBytes {
            CMBlockBufferReplaceDataBytes(
                with: $0.baseAddress!, blockBuffer: block, offsetIntoDestination: 0, dataLength: bytes)
        }
        guard status == noErr else { throw VideoExporterError.failed("audio copy \(status)") }
        var buffer: CMSampleBuffer?
        status = CMAudioSampleBufferCreateReadyWithPacketDescriptions(
            allocator: kCFAllocatorDefault, dataBuffer: block, formatDescription: format, sampleCount: samples.count,
            presentationTimeStamp: CMTime(value: frame, timescale: CMTimeScale(sampleRate)),
            packetDescriptions: nil, sampleBufferOut: &buffer)
        guard status == noErr, let buffer else { throw VideoExporterError.failed("audio sample \(status)") }
        return buffer
    }
}
