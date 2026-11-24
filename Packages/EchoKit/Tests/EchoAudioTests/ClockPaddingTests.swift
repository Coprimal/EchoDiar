import AVFoundation
import Darwin
import Foundation
import Testing
@testable import EchoAudio

/// The audio file is the screen video's time base: at pause and stop its length must equal the clock's file time,
/// even when no buffer reached either channel (mic muted, system tap idle).
struct ClockPaddingTests {
    @Test func silentStopPadsToTheClock() throws {
        let run = try Run()
        let started = Date()
        Thread.sleep(forTimeInterval: 2.5)
        let wall = Date().timeIntervalSince(started)
        try run.writer.stop()
        #expect(run.writer.stats.failure == nil)
        #expect(abs(run.writer.stats.duration - wall) < 0.15)
        #expect(abs(try run.fileSamples().count - Int(wall * 16_000)) < 2_400)
    }

    @Test func silentPauseExcludesOnlyThePausedSpan() throws {
        let run = try Run()
        var started = Date()
        Thread.sleep(forTimeInterval: 1.5)
        var unpaused = Date().timeIntervalSince(started)
        run.writer.pause()
        let atPause = run.writer.stats.duration
        #expect(abs(atPause - unpaused) < 0.15)
        Thread.sleep(forTimeInterval: 1)
        run.writer.resume()
        let resumedAt = try #require(run.writer.fileTime(atHostTime: mach_absolute_time()))
        #expect(abs(resumedAt - atPause) < 0.05)
        started = Date()
        Thread.sleep(forTimeInterval: 1)
        unpaused += Date().timeIntervalSince(started)
        try run.writer.stop()
        #expect(run.writer.stats.failure == nil)
        #expect(abs(run.writer.stats.duration - unpaused) < 0.15)
    }

    @Test func capturedAudioKeepsItsHostTimePosition() throws {
        let run = try Run()
        let started = Date()
        let tone = (0..<8_000).map { Float(sin(Double($0) * 2 * .pi * 440 / 16_000) * 0.5) }
        run.mic.write(tone, count: tone.count, hostTime: mach_absolute_time())
        Thread.sleep(forTimeInterval: 1.5)
        let wall = Date().timeIntervalSince(started)
        try run.writer.stop()
        #expect(run.writer.stats.failure == nil)
        #expect(abs(run.writer.stats.duration - wall) < 0.15)
        let mic = try run.fileSamples()
        let head = mic.prefix(7_000).map(abs).max() ?? 0
        let tail = mic.dropFirst(8_800).map(abs).max() ?? 1
        #expect(head > 0.4)
        #expect(tail == 0)
    }

    /// A writer started on empty 16 kHz rings, writing to a temp CAF.
    private final class Run {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("\(UUID().uuidString).caf")
        let mic = RingBuffer(capacityFrames: 16_000, sampleRate: 16_000)
        let system = RingBuffer(capacityFrames: 16_000, sampleRate: 16_000)
        let writer: ChannelWriter

        init() throws {
            writer = try ChannelWriter(fileURL: url)
            let format = ChannelWriter.SourceFormat(sampleRate: 16_000, channels: 1)
            try writer.start(micRing: mic, systemRing: system, micFormat: format, systemFormat: format)
        }

        deinit {
            try? FileManager.default.removeItem(at: url)
        }

        /// Channel 0 (mic) of the written file, as Float.
        func fileSamples() throws -> [Float] {
            let file = try AVAudioFile(forReading: url, commonFormat: .pcmFormatFloat32, interleaved: false)
            let buffer = try #require(AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(file.length)))
            try file.read(into: buffer)
            return Array(UnsafeBufferPointer(start: buffer.floatChannelData![0], count: Int(buffer.frameLength)))
        }
    }
}
