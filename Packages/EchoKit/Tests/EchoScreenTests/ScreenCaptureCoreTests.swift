import AVFoundation
import CoreMedia
import CoreVideo
import Foundation
import Testing
@testable import EchoScreen

/// Guards the screen video's time base and the area geometry: frames land on the audio file clock (first frame at its
/// file time, paused spans skipped, stale times dropped, the track ends where the recording stopped), and an area on a
/// display left of the primary maps to that display's own top-left points.
struct ScreenCaptureCoreTests {
    @Test func videoTrackFollowsFileTimes() async throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("EchoScreenTests-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let url = folder.appendingPathComponent(ScreenSource.videoFileName)
        let writer = try ScreenVideoWriter(url: url, width: 640, height: 360, fps: 15, quality: 0.75, fileType: .mp4)

        // 2.0 s to 2.4667 s, then a paused span with no frames, then 4.0 s to 4.4667 s (timescale 600, 1/15 s = 40).
        let before = (0..<8).map { CMTime(value: 1200 + Int64($0) * 40, timescale: 600) }
        let after = (0..<8).map { CMTime(value: 2400 + Int64($0) * 40, timescale: 600) }
        var accepted: [CMTime] = []
        for time in before {
            if writer.append(try makeFrame(), fileTime: time) { accepted.append(time) }
            try await Task.sleep(for: .milliseconds(30))
        }
        #expect(!writer.append(try makeFrame(), fileTime: before[5]))
        #expect(!writer.append(try makeFrame(), fileTime: accepted.last ?? before[7]))
        for time in after {
            if writer.append(try makeFrame(), fileTime: time) { accepted.append(time) }
            try await Task.sleep(for: .milliseconds(30))
        }
        let end = CMTime(value: 3000, timescale: 600)
        try await writer.finish(at: end)

        #expect(accepted.first == before[0])
        #expect(accepted.contains { $0 >= after[0] })
        let times = try await presentationTimes(of: url)
        #expect(times.map(\.seconds) == accepted.map(\.seconds))
        #expect(zip(times, times.dropFirst()).allSatisfy { $0 < $1 })
        #expect(times.first?.seconds == 2.0)
        let track = try #require(try await AVURLAsset(url: url).loadTracks(withMediaType: .video).first)
        let range = try await track.load(.timeRange)
        #expect(abs(range.end.seconds - end.seconds) < 0.001)
    }

    @Test func areaOnDisplayLeftOfPrimaryMapsToDisplayLocalPoints() {
        // A 1512x982 built-in display left of and above a taller primary (AppKit global, bottom-left origin).
        let display = CGRect(x: -1512, y: 300, width: 1512, height: 982)
        let area = CGRect(x: -1312, y: 500, width: 800.5, height: 400.75)

        let local = ScreenSource.displayLocal(area: area, displayFrame: display, scale: 2)

        #expect(local.rect == CGRect(x: 200, y: 381.25, width: 800.5, height: 400.75))
        #expect(local.pixelWidth == 1600)
        #expect(local.pixelHeight == 800)
    }
}

private func makeFrame() throws -> CMSampleBuffer {
    var pixelBuffer: CVPixelBuffer?
    let attributes = [kCVPixelBufferIOSurfacePropertiesKey: [:]] as CFDictionary
    let status = CVPixelBufferCreate(kCFAllocatorDefault, 640, 360, kCVPixelFormatType_32BGRA, attributes, &pixelBuffer)
    let image = try #require(status == kCVReturnSuccess ? pixelBuffer : nil)
    CVPixelBufferLockBaseAddress(image, [])
    if let base = CVPixelBufferGetBaseAddress(image) {
        memset(base, 0x80, CVPixelBufferGetDataSize(image))
    }
    CVPixelBufferUnlockBaseAddress(image, [])
    let format = try CMVideoFormatDescription(imageBuffer: image)
    // A host-clock-like time, as ScreenCaptureKit delivers; the writer must replace it with the file time.
    let timing = CMSampleTimingInfo(duration: .invalid, presentationTimeStamp: CMTime(value: 987_654, timescale: 1000),
                                    decodeTimeStamp: .invalid)
    return try CMSampleBuffer(imageBuffer: image, formatDescription: format, sampleTiming: timing)
}

/// Track presentation times of the track's media samples, in presentation order. Read undecoded, because a decoding
/// reader fills the leading empty edit (session start at zero, first frame at 2 s) with a copy of the first frame at
/// 0 s; undecoded samples carry media times, mapped to track time through the file's edit list. HEVC samples come in
/// decode order, so they are sorted; a duplicate time would survive the sort.
private func presentationTimes(of url: URL) async throws -> [CMTime] {
    let asset = AVURLAsset(url: url)
    let track = try #require(try await asset.loadTracks(withMediaType: .video).first)
    let segments = try await track.load(.segments).filter { !$0.isEmpty }
    let reader = try AVAssetReader(asset: asset)
    let output = AVAssetReaderTrackOutput(track: track, outputSettings: nil)
    reader.add(output)
    #expect(reader.startReading())
    var times: [CMTime] = []
    while let sample = output.copyNextSampleBuffer() {
        guard sample.numSamples > 0 else { continue }
        let mediaTime = sample.presentationTimeStamp
        let mapping = try #require(segments.first { $0.timeMapping.source.containsTime(mediaTime) }?.timeMapping)
        times.append(mapping.target.start + (mediaTime - mapping.source.start))
    }
    #expect(reader.status == .completed)
    return times.sorted()
}
