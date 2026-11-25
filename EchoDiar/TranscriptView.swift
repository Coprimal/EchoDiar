import AppKit
import EchoCore
import EchoTranscribe
import SwiftUI

extension Notification.Name {
    /// Space in the main window on a conversation's Transcript tab (posted by the app delegate's key monitor).
    static let togglePlayback = Notification.Name("EchoDiar.togglePlayback")
}

/// One conversation inside the main window (its title, date, duration and size, its tabs and Reveal in Finder are in
/// the toolbar). The Transcript tab ends with a bar of playback, actions and speakers (click to rename). The segments
/// are one selectable text; a click on a segment's timestamp or name plays from its start; Space plays or pauses.
/// Stays mounted across tabs, so playback goes on in Summary and Chat.
struct TranscriptView: View {
    let conversation: Conversation
    let model: AppModel
    /// The newest transcript (`transcript.json`): rename writes it and AI citations point into it; the list, Copy,
    /// notices and chips show `shownTranscript`.
    @State private var transcript: Transcript?
    /// Kept transcripts, oldest first, the newest last; the version menu shows when there are two or more.
    @State private var versions: [Transcript.Version] = []
    /// The version picked in the version menu; nil is the newest. Older ones are view-only.
    @State private var shownVersion: Int?
    /// The audio file `playback` opened, to reload its segments when another version is shown.
    @State private var audioURL: URL?
    /// The video file `playback` opened, with the span that has a picture; nil without one (or before `load`).
    @State private var videoClip: VideoClip?
    /// The video the open Export video sheet exports, taken when it opened (a reload cannot empty the sheet).
    @State private var exportedVideo: URL?
    /// `shownTranscript` as the text the card shows; the first one is built off the main thread with the files.
    @State private var shownText: TranscriptText?
    /// Speaker ids of `shownTranscript`: me first, then s1, s2…
    @State private var speakers: [String] = []
    /// `transcript.json` exists but could not be read.
    @State private var loadFailed = false
    /// The first `load` has returned; until then the tab shows `DetailLoadingPane`.
    @State private var loaded = false
    @State private var playback = PlaybackController()
    /// The speaker whose rename popover is open.
    @State private var renaming: String?
    @State private var nameDraft = ""
    /// The "+N" popover listing the speakers without a chip is open.
    @State private var showingMoreSpeakers = false
    /// Language for the next Re-transcribe; "" keeps the recording's language markers.
    @State private var retranscribeLanguage = ""
    /// The segment the list scrolls to, set by an AI citation; cleared once scrolled.
    @State private var scrollTarget: UUID?
    /// The tab whose body is shown: follows `model.detailTab` one update later, so `forward` is set before the bodies
    /// swap and both the outgoing and the incoming body slide the same way.
    @State private var shownTab: AppModel.DetailTab
    /// The last tab switch went right (Transcript → Summary → Chat).
    @State private var forward = true
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    init(conversation: Conversation, model: AppModel) {
        self.conversation = conversation
        self.model = model
        _shownTab = State(initialValue: model.detailTab)
    }

    var body: some View {
        ZStack(alignment: .top) {
            if shownTab == .transcript {
                // The bar slides with the transcript: it has nothing for Summary and Chat. Until the files are read,
                // a light pane (beside the video pane's placeholder when there is a video), so the click that opened
                // the conversation shows at once.
                VStack(spacing: 10) {
                    withVideoPane {
                        if loaded { content } else { DetailLoadingPane() }
                    }
                    if loaded { bottomBar }
                }
                .transition(tabTransition)
            } else {
                AIPanelView(conversation: conversation, model: model, tab: shownTab, transition: tabTransition)
                    .transition(tabTransition)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .clipped()
        .padding(12)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .task(id: conversation) { await load() }
        #if DEBUG
        .onAppear { DetailTiming.log("placeholder", conversation.id) }
        #endif
        .task { model.refreshInstalledModels() }
        .onChange(of: model.detailTab) { old, new in
            forward = (Self.tabs.firstIndex(of: new) ?? 0) > (Self.tabs.firstIndex(of: old) ?? 0)
            // Also under Reduce Motion: `tabTransition` is then a fade.
            withAnimation(Motion.standard) { shownTab = new }
        }
        .onChange(of: model.pendingTranscriptTime) { showPendingTime() }
        .onChange(of: shownVersion) { applyShownVersion() }
        .onChange(of: model.settings.playbackChannels) { playback.setChannels(model.settings.playbackChannels) }
        // Not while a speaker name is edited: the text field takes Space.
        .onReceive(NotificationCenter.default.publisher(for: .togglePlayback)) { _ in
            guard playback.isReady, renaming == nil else { return }
            playback.togglePlay()
        }
        .onDisappear { playback.close() }
        .sheet(isPresented: Binding(get: { exportedVideo != nil }, set: { if !$0 { exportedVideo = nil } })) {
            if let exportedVideo {
                VideoExportSheet(title: conversation.title, folder: conversation.folder, video: exportedVideo)
            }
        }
    }

    static let tabs: [AppModel.DetailTab] = [.transcript, .summary, .chat]

    /// Moving right, the new body comes in a little from the trailing side and the old one leaves toward the leading
    /// side; the other way round moving left. The old body fades out faster, so the two never show through each other.
    /// Only a fade under Reduce Motion.
    private var tabTransition: AnyTransition {
        if reduceMotion { return .opacity }
        let travel: CGFloat = slidesForward ? 24 : -24
        return .asymmetric(
            insertion: .offset(x: travel).combined(with: .opacity),
            removal: .offset(x: -travel).combined(with: .opacity).animation(.easeOut(duration: 0.1))
        )
    }

    /// The outgoing body keeps the transition of its last update, the one where `model.detailTab` has changed but
    /// `shownTab` not yet: take the direction from that pending change, else from `forward`.
    private var slidesForward: Bool {
        guard model.detailTab != shownTab else { return forward }
        return (Self.tabs.firstIndex(of: model.detailTab) ?? 0) > (Self.tabs.firstIndex(of: shownTab) ?? 0)
    }

    static func tabTitle(_ tab: AppModel.DetailTab) -> LocalizedStringKey {
        switch tab {
        case .transcript: "Transcript"
        case .summary: "Summary"
        case .chat: "Chat"
        }
    }

    // MARK: - Bottom bar

    /// Like the Summary and Chat tabs' bars: the transcription's state, then playback, actions and the speakers.
    private var bottomBar: some View {
        VStack(alignment: .leading, spacing: 10) {
            // Not wrapped in a row: its body is often empty, which then takes no space. A running job shows on
            // Re-transcribe instead.
            if conversation.hasTranscript && !failed && !model.isTranscribing(conversation.id) {
                TranscriptionStatus(conversation: conversation, model: model)
            }
            if conversation.hasTranscript && failed {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    TranscriptionFailureReason(conversation: conversation, model: model)
                    Spacer(minLength: 8)
                    TranscriptionStatus(conversation: conversation, model: model)
                }
            }
            if shownTranscript?.notes.contains(Transcript.cloudDiarizationFailedNote) == true {
                cloudDiarizationFailedNotice
            } else if shownTranscript?.notes.contains(Transcript.diarizationMissingNote) == true {
                diarizationMissingNotice
            }
            if conversation.hasAudio || videoClip != nil {
                HStack(spacing: 10) {
                    PlaybackScrubber(playback: playback)
                    PlaybackTime(playback: playback).fixedSize()
                }
            }
            // Up to three speaker chips, fewer when the row is short; "+N" lists the others.
            ViewThatFits(in: .horizontal) {
                ForEach(chipCounts, id: \.self) { controlRow(chips: $0) }
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .modifier(GlassCard(cornerRadius: 20))
    }

    private var failed: Bool { TranscriptionStatus.showsFailure(conversation, model) }

    /// The transcript was made without the speaker model, so every system-audio voice is Speaker 1.
    private var diarizationMissingNotice: some View {
        let installed = model.diarizerAvailable
        return HStack(alignment: .firstTextBaseline, spacing: 8) {
            Group {
                Image(systemName: "person.2.slash")
                if installed {
                    Text("Speakers were not told apart. The speaker model is installed now: re-transcribe.")
                } else {
                    Text("Speakers were not told apart: the speaker model is not installed (Settings › AI Providers › Local AI).")
                }
            }
            .foregroundStyle(.secondary)
            if !installed {
                Button("Download…") { model.openLocalAI(.diarization) }
                    .buttonStyle(.link)
            }
        }
        .font(.caption)
    }

    /// The cloud diarizer failed, so a model on this Mac told the speakers apart, when one was installed.
    private var cloudDiarizationFailedNotice: some View {
        let localMissing = shownTranscript?.notes.contains(Transcript.diarizationMissingNote) == true
        return HStack(alignment: .firstTextBaseline, spacing: 8) {
            Group {
                Image(systemName: "exclamationmark.icloud")
                Text(localMissing
                     ? "Cloud diarization failed and no diarization model is installed on this Mac."
                     : "Cloud diarization failed; speakers were told apart on this Mac.")
            }
            .foregroundStyle(.secondary)
            if localMissing {
                Button("Download…") { model.openLocalAI(.diarization) }
                    .buttonStyle(.link)
            }
        }
        .font(.caption)
    }

    /// The transport first, then Re-transcribe and Summarize, the other buttons, and the speaker chips at the trailing
    /// edge.
    private func controlRow(chips: Int) -> some View {
        HStack(spacing: 8) {
            if conversation.hasAudio {
                transport
            } else if videoClip != nil {
                // The video still plays.
                transport
                audioRemovedNote
            } else {
                audioRemovedNote
            }
            primaryActions
            secondaryActions
            Spacer(minLength: 8)
            if shownTranscript != nil && !speakers.isEmpty {
                HStack(spacing: 6) {
                    ForEach(speakers.prefix(chips), id: \.self, content: speakerChip)
                    if chips < speakers.count { moreSpeakersChip(Array(speakers.dropFirst(chips))) }
                }
            }
        }
    }

    /// Where the video shows: left of the transcript, above it, or not at all; saved in the settings. A menu whose
    /// button shows the current layout's icon.
    private var layoutSwitch: some View {
        let current = model.settings.transcriptVideoLayout
        return Menu {
            ForEach(TranscriptVideoLayout.allCases, id: \.self) { layout in
                Toggle(isOn: Binding(
                    get: { layout == current },
                    set: { if $0 { model.updateSettings { $0.transcriptVideoLayout = layout } } }
                )) {
                    Label(Self.layoutName(layout), systemImage: Self.layoutIcon(layout))
                }
            }
        } label: {
            HStack(spacing: 6) {
                Image(systemName: Self.layoutIcon(current))
                MenuChevron()
            }
            .accessibilityLabel(Self.layoutName(current))
        }
        // As tall as the row's other buttons (Export video, Delete video, Copy).
        .modifier(CompactMenu(maxWidth: nil, height: BottomBarButtonStyle.height, horizontalPadding: 10))
        .help("Video layout")
    }

    private static func layoutIcon(_ layout: TranscriptVideoLayout) -> String {
        switch layout {
        case .sideBySide: "rectangle.split.2x1"
        case .stacked: "rectangle.split.1x2"
        case .hidden: "video.slash"
        }
    }

    private static func layoutName(_ layout: TranscriptVideoLayout) -> LocalizedStringKey {
        switch layout {
        case .sideBySide: "Video on the left"
        case .stacked: "Video on top"
        case .hidden: "Hide video"
        }
    }

    /// The chip counts `controlRow` tries, most first: 3, 2, 1, 0 (fewer when there are fewer speakers).
    private var chipCounts: [Int] {
        Array((0...min(Self.maxSpeakerChips, speakers.count)).reversed())
    }

    private static let maxSpeakerChips = 3

    /// The video layout menu, Export video and Delete video (with a video), and Copy, all round.
    private var secondaryActions: some View {
        HStack(spacing: 6) {
            if videoClip != nil {
                layoutSwitch
                exportVideoButton
                deleteVideoButton
            }
            Button(action: copyTranscript) {
                Label("Copy transcript", systemImage: "square.on.square")
                    .labelStyle(.iconOnly)
            }
            .buttonStyle(BottomBarButtonStyle(horizontalPadding: 0))
            .help("Copy transcript")
            .disabled(shownTranscript?.segments.isEmpty ?? true)
        }
    }

    /// Re-transcribe and Summarize, in the accent color like Send; a spinner and "Transcribing…" in Re-transcribe's
    /// place while a job is queued or runs, with Stop (round) beside it ("Stopping…" once clicked).
    private var primaryActions: some View {
        HStack(spacing: 6) {
            if model.isTranscribing(conversation.id) {
                let stopping = model.stoppingTranscriptions.contains(conversation.id)
                Button {} label: {
                    HStack(spacing: 8) {
                        ProgressView().controlSize(.small)
                        if stopping { Text("Stopping…") } else { Text("Transcribing…") }
                    }
                }
                .buttonStyle(BottomBarButtonStyle(prominent: true))
                .allowsHitTesting(false)
                .fixedSize()
                if stopping || model.canStopTranscription(conversation.id) {
                    Button { model.stopTranscription(conversation.id) } label: {
                        Label("Stop transcription", systemImage: "stop.fill")
                            .labelStyle(.iconOnly)
                    }
                    .buttonStyle(BottomBarButtonStyle(horizontalPadding: 0))
                    .help("Stop transcription")
                    .disabled(stopping)
                }
            } else {
                retranscribeMenu
            }
            if transcript?.segments.isEmpty == false { summaryButton }
        }
    }

    /// Opens `VideoExportSheet`. Not while the conversation is recorded, compressed or transcribed (as Delete video).
    private var exportVideoButton: some View {
        Button { exportedVideo = videoClip?.url } label: {
            Label("Export video…", systemImage: "square.and.arrow.up")
                .labelStyle(.iconOnly)
        }
        .buttonStyle(BottomBarButtonStyle(horizontalPadding: 0))
        .help("Export video…")
        .disabled(!DeleteMenuItems.canDelete(conversation, model))
    }

    /// Moves the video to the Trash, asked first like Delete audio; the conversation then reloads without it (`load`
    /// runs again as `hasVideo` turns false), so the pane and the layout switch go. Not while the conversation is
    /// recorded, compressed or transcribed.
    private var deleteVideoButton: some View {
        let asks = !model.settings.deleteWithoutConfirmation
        return Button {
            DeleteMenuItems.delete(.video, conversation, model)
        } label: {
            Label(asks ? "Delete video…" : "Delete video", systemImage: "trash")
                .labelStyle(.iconOnly)
        }
        .buttonStyle(BottomBarButtonStyle(horizontalPadding: 0))
        .help(asks ? "Delete video…" : "Delete video")
        .disabled(!DeleteMenuItems.canDelete(conversation, model))
    }

    /// Opens the Summary tab, and starts a summary when there is none yet and the AI tab could start one; a spinner and
    /// "Summarizing…" in its place while this conversation's summary is written.
    @ViewBuilder
    private var summaryButton: some View {
        if model.aiJobs.entry(for: conversation.id)?.kind == .summary {
            Button {} label: {
                HStack(spacing: 8) {
                    ProgressView().controlSize(.small)
                    Text("Summarizing…")
                }
            }
            .buttonStyle(BottomBarButtonStyle(prominent: true))
            .allowsHitTesting(false)
            .fixedSize()
        } else {
            Button(conversation.hasSummary ? "Summary" : "Summarize", action: summarize)
                .buttonStyle(BottomBarButtonStyle(prominent: true))
                .fixedSize()
        }
    }

    /// The tab switches in the click's frame; the job starts as the Summary tab's Summarize button starts it, and
    /// `startAIJob` does nothing while this conversation has a job (other conversations' jobs do not block it).
    private func summarize() {
        model.detailTab = .summary
        guard !conversation.hasSummary, let transcript, let provider = model.currentAIProvider(for: .summary) else {
            return
        }
        model.startAIJob(
            .summary, conversation: conversation, transcript: transcript, summary: nil, history: [],
            provider: provider, model: model.aiModel(for: provider, purpose: .summary))
    }

    /// Installed batch ASR models and configured cloud engines; picking one transcribes the conversation again with
    /// it, in the language picked below them.
    private var retranscribeMenu: some View {
        Menu {
            EngineMenuItems(title: "Transcribe again with", model: model) { engineID in
                let language = retranscribeLanguage.isEmpty ? nil : retranscribeLanguage
                model.transcribe(conversation.id, engineID: engineID, language: language)
            }
            if !model.installedBatchEngines.isEmpty || !model.cloudEngineConfigs.isEmpty {
                LanguagePicker(title: "Language", selection: $retranscribeLanguage, model: model, asRecorded: true)
            }
        } label: {
            HStack(spacing: 6) {
                Text("Re-transcribe")
                MenuChevron()
            }
        }
        .menuStyle(.button)
        .buttonStyle(BottomBarButtonStyle(prominent: true))
        .fixedSize()
        .disabled(!canTranscribe)
        .help(conversation.hasAudio ? "Re-transcribe with another model or language" : "Audio has been removed")
    }

    /// Not while a job runs, the audio is still being recorded or compressed, or it was deleted.
    private var canTranscribe: Bool {
        conversation.hasAudio && !model.isTranscribing(conversation.id) && conversation.status != .recording
            && conversation.status != .compressing && conversation.status != .failed
    }

    /// In place of the transport once the audio was moved to the Trash (from a Delete menu, Storage or auto-delete);
    /// beside it when the video still plays.
    private var audioRemovedNote: some View {
        Label("Audio has been removed", systemImage: "speaker.slash")
            .font(.caption)
            .foregroundStyle(.secondary)
            .fixedSize()
            .help(videoClip == nil
                  ? Text("Playback and re-transcribing are no longer possible. The transcript, summary and chat stay.")
                  : Text("Re-transcribing is no longer possible. The video, transcript, summary and chat stay."))
    }

    private var transport: some View {
        HStack(spacing: 8) {
            Button(action: playback.togglePlay) {
                Label(playback.isPlaying ? "Pause" : "Play", systemImage: playback.isPlaying ? "pause.fill" : "play.fill")
                    .labelStyle(.iconOnly)
            }
            .buttonStyle(BottomBarButtonStyle(horizontalPadding: 0))
            .disabled(!playback.isReady)
            .help(playback.isPlaying ? "Pause (Space)" : "Play (Space)")
        }
        .fixedSize()
    }

    // MARK: - Speakers

    private func speakerChip(_ speaker: String) -> some View {
        Button {
            nameDraft = transcript?.speakerNames[speaker] ?? ""
            renaming = speaker
        } label: {
            speakerLabel(speaker)
                .modifier(SpeakerChipShape())
        }
        .buttonStyle(.plain)
        // Its own width: the row tries fewer chips instead of truncating names.
        .fixedSize()
        // Older versions are view-only: a rename edits the newest.
        .disabled(model.isTranscribing(conversation.id) || shownVersion != nil)
        .help("Rename this speaker")
        .popover(isPresented: Binding(
            get: { renaming == speaker && !showingMoreSpeakers },
            set: { if !$0 { renaming = nil } }
        )) {
            renameForm(speaker)
        }
    }

    private func speakerLabel(_ speaker: String) -> some View {
        HStack(spacing: 5) {
            Circle().fill(Self.color(for: speaker)).frame(width: 7, height: 7)
            Text(shownTranscript.map { Self.displayName(for: speaker, in: $0) } ?? speaker)
        }
    }

    /// The speakers without a chip; its popover lists them, and a click on one renames it in the same popover.
    private func moreSpeakersChip(_ hidden: [String]) -> some View {
        Button {
            renaming = nil
            showingMoreSpeakers = true
        } label: {
            Text(verbatim: "+\(hidden.count)")
                .modifier(SpeakerChipShape())
        }
        .buttonStyle(.plain)
        .fixedSize()
        .disabled(model.isTranscribing(conversation.id) || shownVersion != nil)
        .help("Show the other speakers")
        .popover(isPresented: Binding(
            get: { showingMoreSpeakers },
            set: {
                if !$0 {
                    showingMoreSpeakers = false
                    renaming = nil
                }
            }
        )) {
            if let renaming, hidden.contains(renaming) {
                renameForm(renaming)
            } else {
                VStack(alignment: .leading, spacing: 2) {
                    ForEach(hidden, id: \.self) { speaker in
                        Button {
                            nameDraft = transcript?.speakerNames[speaker] ?? ""
                            renaming = speaker
                        } label: {
                            speakerLabel(speaker)
                                .font(.callout)
                                .padding(.horizontal, 8)
                                .padding(.vertical, 4)
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        .help("Rename this speaker")
                    }
                }
                .padding(6)
                .frame(minWidth: 180)
            }
        }
    }

    /// The name field of a speaker: Return saves, Esc or a click elsewhere cancels.
    private func renameForm(_ speaker: String) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Name for \(Self.defaultName(for: speaker))")
                .font(.caption)
                .foregroundStyle(.secondary)
            TextField(Self.defaultName(for: speaker), text: $nameDraft)
                .onSubmit {
                    rename(speaker, to: nameDraft)
                    renaming = nil
                }
        }
        .padding(12)
        .frame(width: 220)
    }

    /// Saves the name into `transcript.json` (read fresh, only this name changes); a blank name restores the default.
    private func rename(_ speaker: String, to name: String) {
        let name = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard var updated = transcript, updated.speakerNames[speaker] != (name.isEmpty ? nil : name) else { return }
        updated.speakerNames[speaker] = name.isEmpty ? nil : name
        transcript = updated
        updateShownText()
        let folder = conversation.folder
        let id = conversation.id
        let model = model
        Task.detached(priority: .utility) {
            do {
                var stored = try Transcript.read(from: folder)
                stored.speakerNames[speaker] = name.isEmpty ? nil : name
                try stored.write(to: folder)
                await model.transcriptEdited(id)
            } catch {
                NSLog("EchoDiar: could not save the speaker name in \(folder.lastPathComponent): \(error)")
            }
        }
    }

    /// `Transcript.displayName(for:)` with the default names localized: the user's name for `speaker`, else "Me",
    /// "Speaker N" or the raw id. Display only: search and the AI prompts keep EchoCore's English names.
    nonisolated static func displayName(for speaker: String, in transcript: Transcript) -> String {
        let name = transcript.displayName(for: speaker)
        return name == Transcript.defaultName(for: speaker) ? defaultName(for: speaker) : name
    }

    /// `Transcript.defaultName(for:)`, localized.
    nonisolated static func defaultName(for speaker: String) -> String {
        if speaker == Transcript.Speaker.me { return String(localized: "Me") }
        if speaker.hasPrefix("s"), let number = Int(speaker.dropFirst()) {
            return String(localized: "Speaker \(number)")
        }
        return speaker
    }

    /// Me in the accent color; other speakers by number from a fixed list, so a speaker keeps its color.
    nonisolated static func color(for speaker: String) -> Color {
        if speaker == Transcript.Speaker.me { return .accentColor }
        let colors: [Color] = [.orange, .green, .pink, .purple, .teal, .yellow, .mint, .indigo]
        let number = speaker.hasPrefix("s") ? Int(speaker.dropFirst()) : nil
        let index = number.map { $0 - 1 } ?? speaker.unicodeScalars.reduce(0) { $0 + Int($1.value) }
        return colors[max(index, 0) % colors.count]
    }

    /// me, then s1, s2… by number, then any other id.
    private static func speakers(in transcript: Transcript) -> [String] {
        func order(_ speaker: String) -> (Int, Int, String) {
            if speaker == Transcript.Speaker.me { return (0, 0, speaker) }
            if speaker.hasPrefix("s"), let number = Int(speaker.dropFirst()) { return (1, number, speaker) }
            return (2, 0, speaker)
        }
        return Set(transcript.segments.map(\.speaker)).sorted { order($0) < order($1) }
    }

    // MARK: - Segments

    /// The segments in a card like Summary's and Chat's, or a note in its middle while there are none; the version
    /// menu is pinned above them, as in Summary.
    private var content: some View {
        VStack(alignment: .leading, spacing: 0) {
            if showsVersionMenu {
                versionMenu
                    .padding(.horizontal, 14)
                    .padding(.top, 16)
            }
            Group {
                if let shownText, shownTranscript?.segments.isEmpty == false {
                    TranscriptTextView(text: shownText, playback: playback, scrollTarget: $scrollTarget)
                } else if loading {
                    ProgressView()
                } else {
                    VStack(spacing: 10) {
                        Text(emptyText).foregroundStyle(.secondary)
                        if !conversation.hasTranscript {
                            TranscriptionFailureReason(conversation: conversation, model: model)
                                .multilineTextAlignment(.center)
                            TranscriptionStatus(conversation: conversation, model: model)
                        }
                    }
                    .padding(20)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .modifier(GlassCard(cornerRadius: 20))
    }

    /// `main` (the transcript card, or its loading pane) with the video pane left of it or above it, as
    /// `transcriptVideoLayout` says; alone without a video or when the layout hides it.
    @ViewBuilder
    private func withVideoPane<Main: View>(@ViewBuilder _ main: () -> Main) -> some View {
        if showsVideoPane {
            let pane = VideoPaneView(playback: playback, clip: videoClip, conversationID: conversation.id)
            if model.settings.transcriptVideoLayout == .stacked {
                PaneSplit(vertical: true, firstMinimum: 120, secondMinimum: 160) { pane } second: { main() }
            } else {
                PaneSplit(vertical: false, firstMinimum: 200, secondMinimum: 260) { pane } second: { main() }
            }
        } else {
            main()
        }
    }

    /// Not while the video is still being written. Not tied to the audio: a conversation keeps its video after
    /// "Delete audio after transcription".
    private var videoPlayable: Bool { conversation.hasVideo && conversation.status != .recording }

    /// Before `load`, from the conversation, so the pane's placeholder shows in the click's frame; after it, from the
    /// file found.
    private var showsVideoPane: Bool {
        model.settings.transcriptVideoLayout != .hidden && (loaded ? videoClip != nil : videoPlayable)
    }

    /// Segment clicks and the transport play the audio, or the video without it.
    private var mediaPlayable: Bool { audioURL != nil || videoClip != nil }

    /// The picked older version's transcript, else the newest.
    private var shownTranscript: Transcript? {
        versions.first { $0.number == shownVersion }?.transcript ?? transcript
    }

    private var showsVersionMenu: Bool { versions.count >= 2 && !model.isTranscribing(conversation.id) }

    /// The kept transcripts, newest first, each with its engine and date. Picking the newest sets `shownVersion` to
    /// nil, so it shows `transcript` with its renamed speakers.
    private var versionMenu: some View {
        let shown = shownVersion ?? versions.last?.number ?? 0
        return Menu {
            ForEach(versions.reversed(), id: \.number) { version in
                Toggle(isOn: Binding(
                    get: { version.number == shown },
                    set: { if $0 { shownVersion = version.number == versions.last?.number ? nil : version.number } }
                )) {
                    Text("Version \(version.number)")
                    Text(versionDetails(version.transcript))
                }
            }
        } label: {
            let engine = shownTranscript.flatMap { model.transcriptEngineName($0) }
            AIMenuLabel(title: ([String(localized: "Version \(shown)")] + [engine].compactMap { $0 })
                .joined(separator: " · "), systemImage: "clock.arrow.circlepath")
        }
        .modifier(CompactMenu(maxWidth: 260, height: Self.versionMenuHeight))
        .help("Earlier transcripts of this conversation")
    }

    /// As tall as the Summary tab's version menu.
    private static let versionMenuHeight: CGFloat = 28

    private func versionDetails(_ transcript: Transcript) -> String {
        [model.transcriptEngineName(transcript), transcript.createdAt.formatted(date: .abbreviated, time: .shortened)]
            .compactMap { $0 }
            .joined(separator: " · ")
    }

    private var loading: Bool { conversation.hasTranscript && transcript == nil && !loadFailed }

    private var emptyText: LocalizedStringKey {
        if loadFailed { return "The transcript could not be read." }
        if shownTranscript != nil { return "No speech was found in this recording." }
        switch conversation.status {
        case .recording: return "Recording in progress."
        case .compressing: return "Compressing the audio…"
        case .failed: return "The recording failed."
        default: return "No transcript yet."
        }
    }

    // MARK: - Actions

    /// Reads the transcript, builds its text and resolves the audio file off the main thread; runs again when the
    /// conversation changes (e.g. a re-transcription finished). No playback while the audio is being recorded or
    /// compressed, or once it was deleted (which stops it). A file gone without the app (deleted in Finder) lists the
    /// conversation again, without audio. The video likewise, with its own gate (`videoPlayable`); its track and
    /// duration are read here too, so the player opens on a measured file.
    private func load() async {
        let folder = conversation.folder
        let wantsTranscript = conversation.hasTranscript
        let playable = conversation.hasAudio && conversation.status != .recording && conversation.status != .compressing
        let videoPlayable = videoPlayable
        let (loaded, audio, video, videoGone, text) = await Task.detached(priority: .utility) {
            // The newest version is transcript.json itself.
            let versions = wantsTranscript ? Transcript.readVersions(from: folder) : []
            let audio = playable
                ? (try? RecordingSession.read(from: folder)).flatMap {
                    ConversationFiles.audioURL(in: folder, fileName: $0.audioFileName)
                }.flatMap { FileManager.default.fileExists(atPath: $0.path) ? $0 : nil }
                : nil
            let videoFile = videoPlayable ? VideoClip.file(in: folder) : nil
            // A file that exists but cannot be read shows no pane, and is not "gone".
            let video = if let videoFile { await VideoClip.load(videoFile) } else { VideoClip?.none }
            let text = versions.last.map { TranscriptText($0.transcript, playable: audio != nil || video != nil) }
            return (versions, audio, video, videoPlayable && videoFile == nil, text)
        }.value
        guard !Task.isCancelled else { return }
        if playable, audio == nil { model.audioMissing(conversation.id) }
        if videoGone { model.videoMissing(conversation.id) }
        versions = loaded
        transcript = loaded.last?.transcript
        shownVersion = nil
        audioURL = audio
        videoClip = video
        shownText = text
        loadFailed = wantsTranscript && loaded.isEmpty
        applyShownVersion()
        self.loaded = true
        #if DEBUG
        DetailTiming.log("content", conversation.id)
        #endif
        showPendingTime()
    }

    /// The speakers, text and playback highlight of `shownTranscript` (same audio, so only the segments change);
    /// closes a rename popover when an older, view-only version shows.
    private func applyShownVersion() {
        speakers = shownTranscript.map(Self.speakers) ?? []
        updateShownText()
        playback.load(url: audioURL, video: videoClip?.url, firstFrame: videoClip?.shown.lowerBound ?? 0,
                      segments: shownTranscript?.segments ?? [], channels: model.settings.playbackChannels)
        if shownVersion != nil {
            renaming = nil
            showingMoreSpeakers = false
        }
    }

    /// Builds the text again when `shownTranscript` changed since (a version switch or a rename), on the main thread:
    /// only the conversation's first text is built with the files.
    private func updateShownText() {
        guard let shown = shownTranscript else {
            shownText = nil
            return
        }
        if shownText?.transcript != shown || shownText?.playable != mediaPlayable {
            shownText = TranscriptText(shown, playable: mediaPlayable)
        }
    }

    /// Takes the time of a clicked AI citation (`AppModel.pendingTranscriptTime`) once the transcript is loaded:
    /// scrolls to the segment containing it (or the last one starting before it) and moves the play head there,
    /// inside that segment so it is highlighted, without playing. Cleared even without segments, so a later
    /// transcript does not jump to it.
    private func showPendingTime() {
        guard let time = model.pendingTranscriptTime, !loading else { return }
        model.pendingTranscriptTime = nil
        // Citations point into the newest transcript; applied now, so the seek highlights its segment.
        if shownVersion != nil {
            shownVersion = nil
            applyShownVersion()
        }
        guard let segments = transcript?.segments,
              let segment = segments.last(where: { $0.start <= time }) ?? segments.first
        else { return }
        playback.seek(to: min(max(time, segment.start), segment.end))
        scrollTarget = segment.id
    }

    /// One "Name [mm:ss]: text" line per segment, with the renamed names.
    private func copyTranscript() {
        guard let transcript = shownTranscript else { return }
        let text = transcript.segments
            .map { "\(Self.displayName(for: $0.speaker, in: transcript)) [\(Self.timestamp($0.start))]: \($0.text)" }
            .joined(separator: "\n")
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
    }

    /// mm:ss; minutes go past 59 in recordings longer than an hour.
    static func timestamp(_ seconds: Double) -> String {
        Duration.seconds(seconds).formatted(.time(pattern: .minuteSecond(padMinuteToLength: 2)))
    }
}

/// The open conversation's tab until its files are read: the card its content shows in, at once, and a spinner once
/// the reading takes long enough to notice; a spinner shown for a frame or two reads as a flicker.
struct DetailLoadingPane: View {
    @State private var showsSpinner = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        ProgressView()
            .opacity(showsSpinner ? 1 : 0)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .modifier(GlassCard(cornerRadius: 20))
            .task {
                try? await Task.sleep(for: .milliseconds(150))
                guard !Task.isCancelled else { return }
                withAnimation(reduceMotion ? nil : Motion.standard) { showsSpinner = true }
            }
    }
}

/// Play head and length; the only reader of `currentTime`, so the 4 Hz updates redraw nothing else.
private struct PlaybackTime: View {
    let playback: PlaybackController

    var body: some View {
        Text("\(TranscriptView.timestamp(playback.currentTime)) / \(TranscriptView.timestamp(playback.duration))")
            .font(.caption.monospacedDigit())
            .foregroundStyle(.secondary)
    }
}

/// The play head on a track: a click or a drag moves it, and playback pauses during a drag and goes on after it.
/// Reads `currentTime` itself, like `PlaybackTime`, so the 4 Hz updates redraw nothing else.
private struct PlaybackScrubber: View {
    let playback: PlaybackController
    @State private var hovered = false
    @State private var dragging = false
    @State private var resumeAfterDrag = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private static let knobSize: CGFloat = 12

    var body: some View {
        let active = playback.isReady && (hovered || dragging)
        GeometryReader { geometry in
            let width = geometry.size.width
            let fraction = playback.duration > 0 ? min(max(playback.currentTime / playback.duration, 0), 1) : 0
            ZStack(alignment: .leading) {
                Capsule().fill(Palette.switchOffFill)
                Capsule().fill(Color.accentColor).frame(width: width * fraction)
            }
            .frame(height: active ? 6 : 4)
            .overlay(alignment: .leading) {
                Circle()
                    .fill(.white)
                    .frame(width: Self.knobSize, height: Self.knobSize)
                    .offset(x: width * fraction - Self.knobSize / 2)
                    .opacity(active ? 1 : 0)
            }
            .frame(maxHeight: .infinity)
            .contentShape(Rectangle())
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { value in
                        guard playback.isReady, width > 0 else { return }
                        if !dragging {
                            dragging = true
                            resumeAfterDrag = playback.isPlaying
                            if resumeAfterDrag { playback.pause() }
                        }
                        playback.seek(to: min(max(value.location.x / width, 0), 1) * playback.duration)
                    }
                    .onEnded { _ in
                        guard dragging else { return }
                        dragging = false
                        if resumeAfterDrag { playback.play() }
                    }
            )
        }
        .frame(height: 14)
        .onHover { hovered = $0 }
        .pointerStyle(playback.isReady ? .link : nil)
        .animation(reduceMotion ? nil : .easeOut(duration: 0.15), value: active)
        .accessibilityRepresentation {
            Slider(
                value: Binding(get: { playback.currentTime }, set: { playback.seek(to: $0) }),
                in: 0...max(playback.duration, 0.01)
            ) {
                Text("Playback position")
            }
            .disabled(!playback.isReady)
        }
    }
}

/// A speaker chip's capsule, as tall as the bar's buttons.
private struct SpeakerChipShape: ViewModifier {
    func body(content: Content) -> some View {
        content
            .font(.callout.weight(.medium))
            .padding(.horizontal, 12)
            .frame(height: BottomBarButtonStyle.height)
            .background(Palette.hoverFill, in: Capsule())
            .contentShape(Capsule())
    }
}

/// The video pane and the transcript side by side or stacked, split half and half at first, with a 10 pt gap between
/// the two cards that drags to resize them (a resize pointer over it). No divider line: unlike `HSplitView` and
/// `VSplitView`, whose divider cannot be hidden, the cards' own edges separate them.
private struct PaneSplit<First: View, Second: View>: View {
    let vertical: Bool
    let firstMinimum: CGFloat
    let secondMinimum: CGFloat
    @ViewBuilder let first: First
    @ViewBuilder let second: Second
    /// The first pane's share of the space without the gap.
    @State private var fraction: CGFloat = 0.5
    /// The first pane's length when the drag began.
    @State private var dragStart: CGFloat?

    private static var gap: CGFloat { 10 }

    var body: some View {
        GeometryReader { geometry in
            let total = max((vertical ? geometry.size.height : geometry.size.width) - Self.gap, 0)
            let length = clamped(total * fraction, total: total)
            let layout = vertical ? AnyLayout(VStackLayout(spacing: 0)) : AnyLayout(HStackLayout(spacing: 0))
            layout {
                first.frame(width: vertical ? nil : length, height: vertical ? length : nil)
                Color.clear
                    .frame(width: vertical ? nil : Self.gap, height: vertical ? Self.gap : nil)
                    .contentShape(Rectangle())
                    .pointerStyle(vertical ? .rowResize : .columnResize)
                    .gesture(DragGesture(minimumDistance: 1, coordinateSpace: .global)
                        .onChanged { drag in
                            let start = dragStart ?? length
                            if dragStart == nil { dragStart = start }
                            let moved = vertical ? drag.translation.height : drag.translation.width
                            if total > 0 { fraction = clamped(start + moved, total: total) / total }
                        }
                        .onEnded { _ in dragStart = nil })
                second.frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
    }

    /// `length` kept between the two minimums; half and half when the space is too small for both.
    private func clamped(_ length: CGFloat, total: CGFloat) -> CGFloat {
        guard total >= firstMinimum + secondMinimum else { return total / 2 }
        return min(max(length, firstMinimum), total - secondMinimum)
    }
}
