import AppKit
import EchoAI
import EchoCore
import SwiftUI

/// The open conversation's AI panel, right of its transcript and built like the recording page's side panel: one card
/// with a header (the Summary | Catch-up | Chat tabs, `AppModel.detailTab`, Catch-up only with catch-ups, then menus
/// for the model, every usable provider with its models, and the output language, the "i" about where the data goes
/// and, on Chat, "Start a fresh chat" and History, the earlier chats in a panel at the pane's trailing edge) over the
/// selected tab, and the tab's footer inside the card. Summary shows the kept summaries, the newest first with a menu
/// for the earlier ones; its footer has "Copy as prompt" and Summarize / Re-summarize. Catch-up shows the recording's
/// catch-ups, read-only, with no footer and the menus hidden. Chat shows questions with streamed answers; its footer is
/// the question with "Copy as prompt" and Send. `[mm:ss]` citations scroll the transcript beside it to that time.
struct AIPanelView: View {
    let conversation: Conversation
    let model: AppModel
    /// The pane shown: `TranscriptView`'s shown tab, which follows `model.detailTab` one update later.
    let tab: AppModel.DetailTab
    /// How Summary and Chat slide into each other (`TranscriptView`'s tab transition).
    let transition: AnyTransition
    @State private var transcript: Transcript?
    @State private var summary: String?
    /// Every kept summary, oldest first; `summary` is the newest.
    @State private var summaries: [SummaryVersion] = []
    /// The summary version shown; nil = the newest.
    @State private var shownVersion: Int?
    @State private var chat: [ChatLog.Message] = []
    /// The open chat's title, when it was resumed from the history with one.
    @State private var chatTitle: String?
    @State private var question = ""
    @FocusState private var questionFocused: Bool
    /// An explicit switch to Chat (a tab click, the View menu's Chat) asked for the cursor in the question; kept until
    /// the panel is loaded, so a click before that is not lost. Never set merely because the panel opens on Chat:
    /// ↑/↓ through the cards and Space must keep working.
    @State private var focusPending = false
    @State private var toast: LocalizedStringKey?
    /// Hides the toast 2 s after it shows; replaced by each new toast.
    @State private var toastTask: Task<Void, Never>?
    /// A web link from model output, opened only once its destination is confirmed.
    @State private var pendingLink: URL?
    /// The "i" popover about where the data goes is open.
    @State private var showingDataNotes = false
    /// The conversation's earlier chats, newest first.
    @State private var archives: [ChatArchive.Entry] = []
    /// The History panel is open.
    @State private var showingHistory = false
    /// The History panel's search field is shown; hidden (the query cleared) whenever the panel closes.
    @State private var searchingHistory = false
    @State private var historyQuery = ""
    @State private var historySearchFocus = false
    /// Each earlier chat's messages, by id, read while the search field shows.
    @State private var archiveTexts: [UUID: String] = [:]
    /// The alert confirming that every earlier chat is deleted is open.
    @State private var confirmingClear = false
    /// The earlier chat under the pointer, which shows its Remove button.
    @State private var hoveredEntry: UUID?
    /// The first `load` has returned; until then the header shows over an unframed `DetailLoadingPane`.
    @State private var loaded = false
    /// The catch-ups written while it was recorded (its `catchups.json`), oldest first.
    @State private var catchUps: [CatchUp] = []
    /// The open state the user gave catch-up cards with a click; the others: the newest open, the older ones collapsed.
    @State private var catchUpOpenStates: [UUID: Bool] = [:]
    /// The clicks on catch-ups during a search, which win over the open state it gives; cleared with each new query.
    @State private var catchUpSearchOpenStates: [UUID: Bool] = [:]
    /// The search field is shown under the tab; hidden (the query cleared) with the header's search button or Esc.
    @State private var searching = false
    @State private var searchQuery = ""
    @State private var searchFocus = false
    /// Where the query is found in the shown tab's texts (`searchSources`), in order, and the current match.
    @State private var searchMatches: [SearchMatch] = []
    @State private var searchIndex = 0
    /// The tab and query `searchMatches` were found for: the same ones in a changed text keep their place.
    @State private var searchedKey = ""
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private static let chatEnd = "chatEnd"
    /// The height of a one-line question field, and of every control of the footer.
    private static let inputHeight = BottomBarButtonStyle.height
    /// The header's menus and buttons: as tall as the tabs' track beside them, as in the recording's side panel.
    private static let menuHeight = MainView.headerHeight
    /// The model badge and the round Copy under an answer.
    private static let answerFooterHeight: CGFloat = 22

    var body: some View {
        // Collapsed, a rail stands in for the card; this view stays, so the chat, the question and the loads stay too.
        Group {
            if model.aiPanelCollapsed {
                CollapsedPanel(stacked: model.settings.stackedPanes, tabs: shownTabs, cornerRadius: 20,
                               icon: TranscriptView.tabIcon, title: TranscriptView.tabTitle, open: expand)
            } else {
                card
            }
        }
        .environment(\.openURL, OpenURLAction(handler: openLink))
        .modifier(WebLinkConfirmation(link: $pendingLink))
        .task { model.refreshInstalledModels() }
        // A switch to Chat puts the cursor in the question, once the field is in the window and the panel is loaded,
        // unless a sidebar card is being renamed or the search field shows (typing goes on in it).
        .task(id: focusPending && loaded) {
            guard focusPending, loaded else { return }
            try? await Task.sleep(for: .milliseconds(50))
            guard !Task.isCancelled else { return }
            focusPending = false
            guard model.detailTab == .chat, model.renamingConversation == nil, !searching else { return }
            questionFocused = true
        }
        .onChange(of: model.chatFocusRequests) {
            if model.detailTab == .chat { focusPending = true }
        }
        .task(id: LoadKey(id: conversation.id, hasTranscript: conversation.hasTranscript,
                          jobsEnded: model.aiJobEnds[conversation.id] ?? 0)) { await load() }
        // Start fresh, Resume, a new title and a live answer saved: the chat and the history only (`load()` resets the
        // shown version).
        .task(id: HistoryKey(id: conversation.id, changes: model.chatHistoryChanges)) { await loadChat() }
        // Again when a catch-up still writing at Stop saves its card.
        .task(id: CatchUpKey(id: conversation.id, saved: model.recordingCatchUps.count)) { await loadCatchUps() }
        // A new conversation rebuilds the view (`MainView` gives it the conversation's id), which closes the panel too.
        .onChange(of: model.detailTab) { _, new in
            if new == .chat {
                focusPending = true
            } else {
                showingHistory = false
            }
        }
        .onChange(of: showingHistory) { _, shown in
            if !shown { setHistorySearch(false) }
        }
        .task(id: SearchInput(query: searching ? searchQuery : "", tab: tab, texts: searchSources)) { search() }
    }

    /// The open panel: one card, the header (lined up with the sidebar's), a line, then the tab.
    private var card: some View {
        VStack(spacing: 0) {
            header
                .padding(MainView.headerPadding)
            Divider()
            // Until the files are read, a spinner that shows only once the reading takes long enough to notice, so
            // the click that opened the conversation shows at once.
            VStack(spacing: 10) {
                if loaded {
                    // The search field under the tab, sliding up from the footer while it shows.
                    VStack(spacing: 0) {
                        ZStack {
                            switch tab {
                            case .chat: chatPane.transition(transition)
                            case .catchUp: catchUpPane.transition(transition)
                            case .summary: summaryPane.transition(transition)
                            }
                        }
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                        if searching {
                            FindBar(query: $searchQuery, focusRequest: $searchFocus, label: searchLabel,
                                    count: searchMatches.count, index: searchIndex, step: stepSearch) {
                                setSearch(false)
                            }
                            .padding(.horizontal, 14)
                            .padding(.top, 8)
                            .transition(.move(edge: .bottom).combined(with: .opacity))
                        }
                    }
                    .clipped()
                    errorText
                        .padding(.horizontal, 14)
                    // The catch-ups are read-only: no footer.
                    if tab != .catchUp { footer }
                } else {
                    DetailLoadingPane(framed: false)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
            .overlay(alignment: .bottom) { toastView }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .modifier(GlassCard(cornerRadius: 20))
    }

    private struct LoadKey: Equatable {
        let id: UUID
        let hasTranscript: Bool
        let jobsEnded: Int
    }

    private struct HistoryKey: Equatable {
        let id: UUID
        let changes: Int
    }

    private struct CatchUpKey: Equatable {
        let id: UUID
        let saved: Int
    }

    // MARK: - Chat

    /// The messages, or a note in the middle while there are none.
    private var chatPane: some View {
        Group {
            if chat.isEmpty && questionJob == nil {
                Text(emptyNote ?? "Ask a question about this conversation below.")
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .padding(20)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                messages
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .overlay(alignment: .trailing) {
            if showingHistory {
                historyPanel
                    .transition(reduceMotion ? .opacity : .move(edge: .trailing).combined(with: .opacity))
            }
        }
    }

    private var messages: some View {
        ScrollViewReader { proxy in
            ScrollView {
                VStack(alignment: .leading, spacing: 20) {
                    ForEach(Array(chat.enumerated()), id: \.offset) { index, message in
                        messageView(message.role, text: message.text, model: message.model, live: message.live == true,
                                    marks: marks(index))
                            .id(index)
                    }
                    if let job = questionJob, case .question(let asked) = job.payload.kind {
                        messageView(.user, text: asked, model: nil)
                        if let text = jobText {
                            messageView(.assistant, text: text, model: currentModel, isStreaming: true)
                        } else {
                            HStack(spacing: 6) {
                                ProgressView().controlSize(.small)
                                if let status = jobStatus {
                                    Text(status).foregroundStyle(.secondary)
                                }
                            }
                        }
                    }
                    Color.clear.frame(height: 1).id(Self.chatEnd)
                }
                .padding(.horizontal, 14)
                .padding(.vertical, 16)
            }
            .onAppear { proxy.scrollTo(Self.chatEnd, anchor: .bottom) }
            .onChange(of: chat.count) { _, _ in proxy.scrollTo(Self.chatEnd, anchor: .bottom) }
            .onChange(of: questionJob == nil ? nil : jobText?.count) { _, _ in
                proxy.scrollTo(Self.chatEnd, anchor: .bottom)
            }
            // A question found: to it; an answer scrolls its own text to the match (`MarkdownText`).
            .onChange(of: currentSearchMatch) { _, match in
                guard let match, chat.indices.contains(match.text), chat[match.text].role == .user else { return }
                withAnimation(reduceMotion ? nil : Motion.standard) { proxy.scrollTo(match.text, anchor: .center) }
            }
        }
    }

    @ViewBuilder private func messageView(
        _ role: ChatLog.Message.Role, text: String, model: String?, isStreaming: Bool = false, live: Bool = false,
        marks: SearchMarks = SearchMarks()
    ) -> some View {
        if role == .user {
            VStack(alignment: .trailing, spacing: 6) {
                Text(Self.marked(text, marks))
                    .textSelection(.enabled)
                    .padding(.horizontal, 10)
                    .padding(.vertical, 6)
                    .background(Palette.hoverFill, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
                if live { liveBadge }
            }
            .frame(maxWidth: .infinity, alignment: .trailing)
        } else {
            VStack(alignment: .leading, spacing: 6) {
                MarkdownText(text: text, marks: marks)
                HStack(spacing: 6) {
                    if live { liveBadge }
                    if let model, !model.isEmpty {
                        Text(model)
                            .font(.caption2)
                            .foregroundStyle(.secondary)
                            .padding(.horizontal, 8)
                            .frame(height: Self.answerFooterHeight)
                            .background(Capsule().fill(Palette.inputSurface))
                    }
                    if !isStreaming {
                        Button { copyAnswer(text) } label: {
                            Label("Copy", systemImage: "square.on.square").labelStyle(.iconOnly).font(.caption2)
                        }
                        .buttonStyle(BottomBarButtonStyle(height: Self.answerFooterHeight, horizontalPadding: 0))
                        .help("Copy the answer")
                    }
                }
            }
        }
    }

    /// Marks a message asked or answered in the live chat during the recording, like the model badge.
    private var liveBadge: some View {
        Text("Live")
            .font(.caption2)
            .foregroundStyle(.secondary)
            .padding(.horizontal, 8)
            .frame(height: Self.answerFooterHeight)
            .background(Capsule().fill(Palette.inputSurface))
    }

    // MARK: - History

    /// The open chat, then the earlier ones, newest first: a click makes one the open chat (the open one goes to the
    /// history), not while an answer streams.
    private var historyPanel: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text("History").font(.headline)
                Spacer(minLength: 0)
                Button { setHistorySearch(!searchingHistory) } label: {
                    Label("Search chats", systemImage: "magnifyingglass").labelStyle(.iconOnly)
                        .foregroundStyle(searchingHistory ? AnyShapeStyle(Color.accentColor) : AnyShapeStyle(.primary))
                }
                .buttonStyle(RowIconButtonStyle())
                .help("Search chats")
                .padding(.trailing, 6)
                Button { confirmingClear = true } label: {
                    Label("Delete all earlier chats", systemImage: "trash").labelStyle(.iconOnly)
                }
                .buttonStyle(RowIconButtonStyle())
                .disabled(archives.isEmpty || questionJob != nil)
                .help("Delete all earlier chats")
                .padding(.trailing, 6)
                Button(action: closeHistory) {
                    Label("Close", systemImage: "xmark").labelStyle(.iconOnly)
                }
                .buttonStyle(RowIconButtonStyle())
                .keyboardShortcut(.cancelAction)
                .help("Close the history")
            }
            .padding(.horizontal, 14)
            .padding(.top, 12)
            if searchingHistory {
                ChatHistorySearchField(query: $historyQuery, focusRequest: $historySearchFocus) {
                    setHistorySearch(false)
                }
                .padding(.horizontal, 10)
                .padding(.top, 6)
                .transition(.move(edge: .top).combined(with: .opacity))
            }
            ScrollView {
                let query = searchingHistory ? historyQuery : ""
                let current = currentChatText(query)
                // Typos only when no chat matches without them (`SearchText.filter`).
                let typos = !ChatArchive.matches(title: chatTitle ?? "", text: current, query: query)
                    && !archives.contains { ChatArchive.matches(title: $0.title, text: archiveTexts[$0.id], query: query) }
                let shown = archives.filter {
                    ChatArchive.matches(title: $0.title, text: archiveTexts[$0.id], query: query, typos: typos)
                }
                let currentShown = ChatArchive.matches(title: chatTitle ?? "", text: current, query: query, typos: typos)
                VStack(spacing: 2) {
                    if currentShown { currentChatRow }
                    ForEach(shown) { entry in historyRow(entry) }
                    let noMatch = shown.isEmpty && !currentShown
                    if noMatch || archives.isEmpty {
                        // Inset as the header's title and the rows' text.
                        (noMatch ? Text("No matching chats") : Text("No earlier chats"))
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(.horizontal, 8)
                            .padding(.top, 8)
                    }
                }
                .padding(6)
            }
            .disabled(questionJob != nil)
        }
        .frame(width: 260)
        .frame(maxHeight: .infinity, alignment: .top)
        .modifier(GlassCard(cornerRadius: 14, raised: true))
        .padding(8)
        // The earlier chats' messages, read off the main thread when the field shows and when the chats change.
        .task(id: searchingHistory ? archives : nil) { await loadArchiveTexts() }
        .alert("Delete all earlier chats?", isPresented: $confirmingClear) {
            Button("Delete", role: .destructive) { model.clearChats(of: conversation) }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("The open chat stays. This can't be undone.")
        }
    }

    /// The open chat, first and highlighted: its title, else its first question; "New chat" while it is empty.
    private var currentChatRow: some View {
        VStack(alignment: .leading, spacing: 2) {
            // The title may come from a model: plain text, never Markdown.
            Text(chatTitle ?? chat.first { $0.role == .user }?.text ?? String(localized: "New chat")).lineLimit(2)
            (chat.isEmpty ? Text("Current") : Text("Current · \(chat.count) messages"))
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.horizontal, 8)
        .padding(.vertical, 6)
        .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Palette.hoverFill))
    }

    private func historyRow(_ entry: ChatArchive.Entry) -> some View {
        Button {
            model.resumeChat(entry.id, of: conversation)
            closeHistory()
        } label: {
            VStack(alignment: .leading, spacing: 2) {
                // The title may come from a model: plain text, never Markdown.
                Text(entry.title).lineLimit(2)
                Text("\(entry.archivedAt.formatted(date: .abbreviated, time: .shortened)) · \(entry.messageCount) messages")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.leading, 8)
            // Room for the Remove button.
            .padding(.trailing, 28)
            .padding(.vertical, 6)
        }
        .buttonStyle(HistoryRowStyle(hovered: hoveredEntry == entry.id))
        .overlay(alignment: .trailing) {
            if hoveredEntry == entry.id {
                Button { model.deleteChat(entry.id, of: conversation) } label: {
                    Label("Remove", systemImage: "trash").labelStyle(.iconOnly).font(.caption)
                }
                .buttonStyle(RowIconButtonStyle())
                .help("Remove this chat")
                .padding(.trailing, 10)
                .transition(.opacity)
            }
        }
        .onHover { hovering in
            withAnimation(Motion.hover) {
                if hovering {
                    hoveredEntry = entry.id
                } else if hoveredEntry == entry.id {
                    hoveredEntry = nil
                }
            }
        }
    }

    private func closeHistory() {
        withAnimation(Motion.standard) { showingHistory = false }
    }

    /// The open chat's messages on one string for the search; nil (not joined) while the query is blank.
    private func currentChatText(_ query: String) -> String? {
        query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            ? nil : chat.map(\.text).joined(separator: "\n")
    }

    /// Shows the History search field, focused, or hides it, clearing the query and the messages read for it.
    private func setHistorySearch(_ shown: Bool) {
        withAnimation(reduceMotion ? nil : Motion.standard) { searchingHistory = shown }
        if shown {
            historySearchFocus = true
        } else {
            historyQuery = ""
            archiveTexts = [:]
        }
    }

    private func loadArchiveTexts() async {
        guard searchingHistory else { return }
        let folder = conversation.folder
        let texts = await Task.detached(priority: .userInitiated) { ChatArchive.searchTexts(in: folder) }.value
        guard !Task.isCancelled else { return }
        archiveTexts = texts
    }

    // MARK: - Search

    /// One match of the search: the index of its text in `searchSources`, and its range in that text as shown.
    private struct SearchMatch: Equatable {
        let text: Int
        let range: NSRange
    }

    /// `searchMatches` are found again when any changes.
    private struct SearchInput: Equatable {
        let query: String
        let tab: AppModel.DetailTab
        let texts: [String]
    }

    private var searchLabel: String {
        switch tab {
        case .summary: String(localized: "Search summary")
        case .catchUp: String(localized: "Search catch-ups")
        case .chat: String(localized: "Search chat")
        }
    }

    /// What the shown tab's search looks in, as written: the shown summary (not one being written), the saved chat's
    /// messages, the catch-ups newest first; their order is the matches' order.
    private var searchSources: [String] {
        switch tab {
        case .summary: summaryJob == nil ? shownSummary.map { [$0.text] } ?? [] : []
        case .catchUp: catchUps.reversed().map(\.text)
        case .chat: chat.map(\.text)
        }
    }

    private var currentSearchMatch: SearchMatch? {
        searchMatches.indices.contains(searchIndex) ? searchMatches[searchIndex] : nil
    }

    /// The matches in text `index` of `searchSources`, the current one marked.
    private func marks(_ index: Int) -> SearchMarks {
        var marks = SearchMarks()
        for (position, match) in searchMatches.enumerated() where match.text == index {
            if position == searchIndex { marks.current = marks.ranges.count }
            marks.ranges.append(match.range)
        }
        return marks
    }

    /// A question as shown, with its search marks.
    private static func marked(_ text: String, _ marks: SearchMarks) -> AttributedString {
        var result = AttributedString(text)
        for (index, range) in marks.ranges.enumerated() {
            guard let range = Range(range, in: text),
                  let lower = AttributedString.Index(range.lowerBound, within: result),
                  let upper = AttributedString.Index(range.upperBound, within: result)
            else { continue }
            let current = index == marks.current
            result[lower..<upper].backgroundColor = current ? Color.orange.opacity(0.8) : Color.yellow.opacity(0.3)
            if current { result[lower..<upper].foregroundColor = .black }
        }
        return result
    }

    /// Shows the search field, focused, or hides it and clears the query (and so the marks).
    private func setSearch(_ shown: Bool) {
        withAnimation(reduceMotion ? nil : Motion.standard) { searching = shown }
        if shown {
            searchFocus = true
        } else {
            searchQuery = ""
        }
    }

    /// The next (1) or previous (-1) match, round from the last to the first.
    private func stepSearch(_ step: Int) {
        guard !searchMatches.isEmpty else { return }
        searchIndex = (searchIndex + step + searchMatches.count) % searchMatches.count
    }

    /// Finds the query in the shown tab's texts as shown (an answer's Markdown rendered, `MarkdownText.plain`); a new
    /// query or tab starts at the first match.
    private func search() {
        let query = searching ? searchQuery : ""
        guard !query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            searchMatches = []
            searchIndex = 0
            searchedKey = ""
            return
        }
        let texts = searchSources.enumerated().map { index, text in
            tab == .chat && chat[index].role == .user ? text : MarkdownText.plain(text)
        }
        let matches = SearchText.find(query, in: texts).map {
            SearchMatch(text: $0.text, range: NSRange($0.range, in: texts[$0.text]))
        }
        let key = "\(tab)\u{1}\(query)"
        if key != searchedKey { catchUpSearchOpenStates = [:] }
        searchIndex = key == searchedKey ? min(searchIndex, max(matches.count - 1, 0)) : 0
        searchMatches = matches
        searchedKey = key
    }

    // MARK: - Summary

    /// The summary with the version menu pinned above it; a note in the middle while there is none.
    private var summaryPane: some View {
        Group {
            if summaryJob == nil && summaries.isEmpty {
                Text(emptyNote ?? "No summary yet.")
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .padding(20)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                VStack(alignment: .leading, spacing: 0) {
                    if summaryJob == nil, let shown = shownSummary {
                        versionMenu(shown)
                            .padding(.horizontal, 14)
                            .padding(.top, 16)
                    }
                    ScrollView {
                        VStack(alignment: .leading, spacing: 8) {
                            if summaryJob != nil {
                                // Until the first text: a remote provider sends no status before it.
                                if let text = jobText {
                                    MarkdownText(text: text)
                                } else {
                                    HStack(spacing: 6) {
                                        ProgressView().controlSize(.small)
                                        Text(jobStatus ?? String(localized: "Writing the summary…"))
                                            .foregroundStyle(.secondary)
                                    }
                                }
                            } else if let shown = shownSummary {
                                MarkdownText(text: shown.text, marks: marks(0))
                            } else {
                                Text(emptyNote ?? "No summary yet.")
                                    .foregroundStyle(.secondary)
                            }
                        }
                        .padding(.horizontal, 14)
                        // Below the version menu (shown while no summary is being written), its gap stays as
                        // it was.
                        .padding(.top, summaryJob == nil ? 10 : 16)
                        .padding(.bottom, 16)
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .frame(maxHeight: .infinity)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }

    private var shownSummary: SummaryVersion? {
        summaries.last { $0.number == shownVersion } ?? summaries.last
    }

    /// The kept summaries, newest first, each with its model, date and whether it was stopped.
    private func versionMenu(_ shown: SummaryVersion) -> some View {
        Menu {
            ForEach(summaries.reversed(), id: \.number) { version in
                Toggle(isOn: Binding(
                    get: { version.number == shown.number },
                    set: { if $0 { shownVersion = version.number } }
                )) {
                    Text("Version \(version.number)")
                    let details = versionDetails(version)
                    if !details.isEmpty { Text(details) }
                }
            }
        } label: {
            AIMenuLabel(title: ([String(localized: "Version \(shown.number)")] + [versionModel(shown)].compactMap { $0 })
                .joined(separator: " · "), systemImage: "clock.arrow.circlepath")
        }
        .modifier(CompactMenu(maxWidth: 260, height: MainView.headerControlHeight))
        .help("Earlier summaries of this conversation")
    }

    /// The model, else the provider's name, of a kept summary; nil for one saved before versions.
    private func versionModel(_ version: SummaryVersion) -> String? {
        if let id = version.model, !id.isEmpty { return id }
        guard let provider = version.provider else { return nil }
        return model.aiProviders.first { $0.id == provider }?.name ?? provider
    }

    private func versionDetails(_ version: SummaryVersion) -> String {
        [versionModel(version), version.createdAt?.formatted(date: .abbreviated, time: .shortened),
         version.stopped ? String(localized: "Stopped") : nil]
            .compactMap { $0 }
            .joined(separator: " · ")
    }

    /// The Catch-up tab: the catch-ups written while recording, read-only, newest first, the newest open and the
    /// others collapsed, each opening or closing with a click on its header, each with Copy.
    private var catchUpPane: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 6) {
                let providers = model.aiProviders
                let found = Set(searchMatches.map(\.text))
                ForEach(Array(catchUps.reversed().enumerated()), id: \.element.id) { index, catchUp in
                    // While searching, the ones with a match open too, and the current match's always.
                    let normal = catchUpOpenStates[catchUp.id] ?? (index == 0)
                    let open = found.isEmpty ? normal
                        : currentSearchMatch?.text == index
                            || (catchUpSearchOpenStates[catchUp.id] ?? (found.contains(index) || normal))
                    CatchUpCardView(
                        card: CatchUpCard(catchUp, providers: providers),
                        open: open,
                        toggle: {
                            withAnimation(reduceMotion ? nil : Motion.standard) {
                                if found.isEmpty {
                                    catchUpOpenStates[catchUp.id] = !open
                                } else {
                                    catchUpSearchOpenStates[catchUp.id] = !open
                                }
                            }
                        },
                        stop: {},
                        marks: marks(index))
                }
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 16)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    /// Why nothing can run for this conversation.
    private var emptyNote: LocalizedStringKey? {
        conversation.hasTranscript ? nil : "This conversation has no transcript yet."
    }

    @ViewBuilder private var errorText: some View {
        if let error = model.aiError, error.conversationID == conversation.id {
            Text(error.message)
                .font(.callout)
                .foregroundStyle(.red)
                .lineLimit(4)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    // MARK: - Header and footer

    /// On one row, as in the recording's side panel: the tabs, then the model and language menus, the "i" and, on
    /// Chat, "Start a fresh chat" and History. Narrower, the language menu shows only its globe, then the model menu
    /// only its chip, then the tabs only their icons, then the "i" goes and the two menus pad their sides less, then
    /// the language menu and Chat's two buttons go into a "⋮" menu (the window's minimum width); the row never wraps.
    private var header: some View {
        ViewThatFits(in: .horizontal) {
            headerRow(modelLabel: true, languageLabel: true)
            headerRow(modelLabel: true, languageLabel: false)
            headerRow(modelLabel: false, languageLabel: false)
            headerRow(modelLabel: false, languageLabel: false, iconTabs: true)
            headerRow(modelLabel: false, languageLabel: false, iconTabs: true, tight: true)
            headerRow(modelLabel: false, languageLabel: false, iconTabs: true, tight: true, overflow: true)
        }
        .frame(height: MainView.headerHeight)
    }

    /// `tight`: no "i", the menus with `AIModelMenu.tightPadding`, the tabs and the row's items closer. `overflow`:
    /// the language menu and Chat's two buttons in `moreMenu`, the same width on every tab. Off Chat, measured with
    /// room for Chat's two buttons, so every tab picks the same step. On Catch-up (read-only, nothing to run) the menus
    /// are hidden but keep their room, for the same reason.
    private func headerRow(modelLabel: Bool, languageLabel: Bool, iconTabs: Bool = false,
                           tight: Bool = false, overflow: Bool = false) -> some View {
        let spacing: CGFloat = tight ? 6 : 8
        return ReservedWidth(extra: model.detailTab == .chat || overflow ? 0 : 2 * (Self.menuHeight + spacing)) {
            HStack(spacing: spacing) {
                tabs(iconOnly: iconTabs, tight: tight)
                Spacer(minLength: spacing)
                if model.detailTab == .catchUp {
                    headerMenus(modelLabel: modelLabel, languageLabel: languageLabel, tight: tight, overflow: overflow)
                        .hidden()
                } else {
                    headerMenus(modelLabel: modelLabel, languageLabel: languageLabel, tight: tight, overflow: overflow)
                }
                searchButton
                PanelCollapseButton(stacked: model.settings.stackedPanes) { model.aiPanelCollapsed = true }
            }
        }
    }

    /// Shows or hides the search field under the tab; in the accent color while it shows.
    private var searchButton: some View {
        Button { setSearch(!searching) } label: {
            Label(searchLabel, systemImage: "magnifyingglass")
                .labelStyle(.iconOnly)
                .foregroundStyle(searching ? AnyShapeStyle(Color.accentColor) : AnyShapeStyle(.primary))
        }
        .buttonStyle(BottomBarButtonStyle(height: Self.menuHeight, horizontalPadding: 0))
        .help(searchLabel)
    }

    /// A button of the collapsed panel: opens it on `tab`; Chat as the View menu's Chat, with the cursor in the question.
    private func expand(_ tab: AppModel.DetailTab) {
        if tab == .chat {
            model.requestChat()
        } else {
            model.detailTab = tab
            model.aiPanelCollapsed = false
        }
    }

    /// Summary, Catch-up when the conversation has catch-ups, and Chat.
    private var shownTabs: [AppModel.DetailTab] {
        catchUps.isEmpty ? TranscriptView.tabs.filter { $0 != .catchUp } : TranscriptView.tabs
    }

    private func tabs(iconOnly: Bool, tight: Bool) -> some View {
        // A fill of its own: the panel is `Palette.surface` too, so the default track shows only its outline.
        CapsuleTabs(selection: Binding(get: { model.detailTab }, set: { model.detailTab = $0 }),
                    tabs: shownTabs,
                    title: { iconOnly ? TranscriptView.tabIcon($0) : Text(TranscriptView.tabTitle($0)) },
                    drawsTrack: true, trackFill: Palette.inputSurface, itemPadding: tight ? 8 : 12,
                    itemHeight: MainView.headerControlHeight,
                    help: iconOnly ? { Text(TranscriptView.tabTitle($0)) } : nil)
            .fixedSize()
    }

    @ViewBuilder private func headerMenus(modelLabel: Bool, languageLabel: Bool, tight: Bool,
                                          overflow: Bool) -> some View {
        let padding = tight ? AIModelMenu.tightPadding : nil
        AIModelMenu(model: model, purpose: purpose, height: Self.menuHeight, showsLabel: modelLabel,
                    horizontalPadding: padding)
        if overflow {
            moreMenu
        } else {
            headerButtons(padding: padding, languageLabel: languageLabel, tight: tight)
        }
    }

    /// The header's language menu, the "i" and, on Chat, "Start a fresh chat" and History.
    @ViewBuilder private func headerButtons(padding: CGFloat?, languageLabel: Bool, tight: Bool) -> some View {
        AILanguageMenu(model: model, height: Self.menuHeight, showsLabel: languageLabel, horizontalPadding: padding)
        if !tight { dataNotesButton(height: Self.menuHeight) }
        if model.detailTab == .chat {
            freshChatButton
            Button {
                withAnimation(Motion.standard) { showingHistory.toggle() }
            } label: {
                Label("History", systemImage: "clock.arrow.circlepath").labelStyle(.iconOnly)
            }
            .buttonStyle(BottomBarButtonStyle(height: Self.menuHeight, horizontalPadding: 0))
            .help("Earlier chats of this conversation")
        }
    }

    /// The narrowest header's "⋮": on Chat "Start a fresh chat", the output language in a submenu, and on Chat History.
    private var moreMenu: some View {
        Menu {
            if model.detailTab == .chat {
                Button("Start a fresh chat", systemImage: "plus.bubble") { model.startFreshChat(conversation) }
                    .disabled(chat.isEmpty || questionJob != nil)
            }
            Picker(selection: AILanguageMenu.languageBinding(model)) {
                AILanguageMenu.options(model.settings.summaryLanguage)
            } label: {
                Label("Language", systemImage: "globe")
            }
            .pickerStyle(.menu)
            if model.detailTab == .chat {
                Toggle(isOn: Binding(get: { showingHistory },
                                     set: { shown in withAnimation(Motion.standard) { showingHistory = shown } })) {
                    Label("History", systemImage: "clock.arrow.circlepath")
                }
            }
        } label: {
            Image(systemName: "ellipsis")
                .rotationEffect(.degrees(90))
                .accessibilityLabel("More")
        }
        .modifier(CompactMenu(maxWidth: nil, height: Self.menuHeight, horizontalPadding: 0))
        .help("More")
    }

    /// Inside the card, under the tab: Summary has "Copy as prompt" and its action, Chat the question row (with Send
    /// only). Then the missing-model warning.
    private var footer: some View {
        VStack(alignment: .leading, spacing: 8) {
            busyElsewhere
            if model.detailTab == .chat {
                questionRow
            } else {
                HStack(spacing: 8) {
                    Spacer(minLength: 0)
                    copyButton
                    if summaryJob != nil {
                        Button { model.stopAIJob(conversation.id) } label: {
                            Label("Summarizing", systemImage: "stop.fill")
                        }
                        .buttonStyle(BottomBarButtonStyle(prominent: true))
                        .help("Stop the summary and keep what is written so far")
                    } else if summaries.isEmpty {
                        Button("Summarize") { start(.summary) }
                            .buttonStyle(BottomBarButtonStyle(prominent: true))
                            .disabled(!canRun)
                    } else {
                        Button("Re-summarize") { start(.summary) }
                            .buttonStyle(BottomBarButtonStyle(prominent: true))
                            .disabled(!canRun)
                            .help("Make a new summary; the earlier ones stay in the version menu")
                    }
                }
            }
            AISetupHints(model: model, purpose: purpose)
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .padding(.horizontal, 14)
        .padding(.bottom, 10)
    }

    /// The question (1 to 6 lines; Return sends, Shift+Return starts a new line) and Send (Stop while answering), at
    /// the bottom of the field: centered with it while it has one line.
    private var questionRow: some View {
        HStack(alignment: .bottom, spacing: 8) {
            TextField("Ask about this conversation", text: $question, axis: .vertical)
                .textFieldStyle(.plain)
                .focused($questionFocused)
                .lineLimit(1...6)
                .onSubmit(ask)
                .onKeyPress(.return, phases: .down) { press in
                    guard press.modifiers.contains(.shift) else { return .ignored }
                    NSApp.sendAction(#selector(NSResponder.insertNewlineIgnoringFieldEditor(_:)), to: nil, from: nil)
                    return .handled
                }
                .padding(.horizontal, 12)
                .padding(.vertical, 7)
                .frame(maxWidth: .infinity, minHeight: Self.inputHeight, alignment: .leading)
                .background(RoundedRectangle(cornerRadius: Self.inputHeight / 2, style: .continuous)
                    .fill(Palette.inputSurface))
            if questionJob != nil {
                Button { model.stopAIJob(conversation.id) } label: {
                    Label("Stop", systemImage: "stop.fill").labelStyle(.iconOnly)
                }
                .buttonStyle(BottomBarButtonStyle(prominent: true, horizontalPadding: 0))
                .help("Stop the answer")
            } else {
                Button("Send", action: ask)
                    .buttonStyle(BottomBarButtonStyle(prominent: true))
                    .disabled(!canRun || question.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }
    }

    /// Archives the chat so the next question starts without history; not while it is empty or an answer streams.
    private var freshChatButton: some View {
        Button { model.startFreshChat(conversation) } label: {
            Label("Start a fresh chat", systemImage: "plus.bubble").labelStyle(.iconOnly)
        }
        .buttonStyle(BottomBarButtonStyle(height: Self.menuHeight, horizontalPadding: 0))
        .help("Start a fresh chat")
        .disabled(chat.isEmpty || questionJob != nil)
    }

    /// This conversation's job of the other tab: it blocks Ask and Summarize until it ends, so it says what runs and
    /// can be stopped from here. Other conversations' jobs run beside this one's.
    @ViewBuilder private var busyElsewhere: some View {
        if let job = model.aiJobs.entry(for: conversation.id),
           (model.detailTab == .chat ? questionJob : summaryJob) == nil {
            HStack(spacing: 6) {
                ProgressView().controlSize(.small)
                (job.kind == .summary ? Text("Summarizing \(conversation.title)…")
                    : Text("Answering about \(conversation.title)…"))
                    .lineLimit(1)
                    .truncationMode(.middle)
                Spacer(minLength: 0)
                Button("Stop") { model.stopAIJob(conversation.id) }
                    .modifier(CapsuleButton())
            }
            .font(.caption)
            .foregroundStyle(.secondary)
        }
    }

    /// The "i": where the data goes with the current provider, in a popover; only when there is something to say.
    @ViewBuilder private func dataNotesButton(height: CGFloat) -> some View {
        if hasDataNotes {
            Button { showingDataNotes.toggle() } label: {
                Image(systemName: "info.circle")
            }
            .buttonStyle(BottomBarButtonStyle(height: height, horizontalPadding: 0))
            .help("About where data goes")
            .popover(isPresented: $showingDataNotes, arrowEdge: .top) {
                VStack(alignment: .leading, spacing: 8) { dataNotes }
                    .font(.callout)
                    .padding(14)
                    .frame(width: 280, alignment: .leading)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private var copyButton: some View {
        Button(action: copyPrompt) {
            Label("Copy as prompt", systemImage: "square.on.square").labelStyle(.iconOnly)
        }
        .buttonStyle(BottomBarButtonStyle(horizontalPadding: 0))
        .disabled(transcript == nil)
        .help("Copy the transcript as a summary prompt for any chat app")
    }

    /// Where the data goes with the tab's provider: off this Mac, and how far a CLI agent is contained.
    @ViewBuilder private var dataNotes: some View {
        let provider = model.currentAIProvider(for: purpose)
        if let note = provider?.leavesMacNote {
            Label(note, systemImage: "info.circle")
        }
        if let note = containmentNote {
            Label(note, systemImage: "exclamationmark.shield")
        }
    }

    private var containmentNote: String? {
        model.currentAIProvider(for: purpose)?.containmentNote
    }

    private var hasDataNotes: Bool {
        model.currentAIProvider(for: purpose)?.leavesMac == true || containmentNote != nil
    }

    @ViewBuilder private var toastView: some View {
        if let toast {
            Text(toast)
                .padding(.horizontal, 14)
                .padding(.vertical, 8)
                .modifier(GlassCard(cornerRadius: 14))
                .padding(.bottom, 110)
                .transition(reduceMotion ? .opacity : .offset(y: 8).combined(with: .opacity))
        }
    }

    // MARK: - State

    /// The panel's tab: Chat's questions use the chat default, the Summary tab the summary default.
    private var purpose: AppModel.AIPurpose {
        model.detailTab == .chat ? .chat : .summary
    }

    /// The chat provider's picked model ("" = a CLI's own default), shown on the streaming answer.
    private var currentModel: String? {
        model.currentAIProvider(for: .chat).map { model.aiModel(for: $0, purpose: .chat) }
    }

    private var summaryJob: AppModel.AIJobs.Entry? {
        guard let job = model.aiJobs.entry(for: conversation.id), job.kind == .summary else { return nil }
        return job
    }

    private var questionJob: AppModel.AIJobs.Entry? {
        guard let job = model.aiJobs.entry(for: conversation.id), job.kind != .summary else { return nil }
        return job
    }

    /// The answer streamed so far by this conversation's job; nil before its first text.
    private var jobText: String? {
        model.aiJobText[conversation.id]
    }

    /// What this conversation's job does before its first text: "Queued" behind another in-app job, else its status.
    private var jobStatus: String? {
        model.aiJobs.isQueued(conversation.id) ? String(localized: "Queued") : model.aiJobStatus[conversation.id]
    }

    /// As `startAIJob`'s guard: one job per conversation; other conversations' jobs do not block it.
    private var canRun: Bool {
        model.aiJobs.entry(for: conversation.id) == nil && transcript != nil
            && model.currentAIProvider(for: purpose) != nil
    }

    private func load() async {
        let folder = conversation.folder
        // The catch-ups too, so the summary tab shows them with the summary, not a moment after it.
        let (loaded, versions, messages, catchUpLog) = await Task.detached(priority: .utility) {
            (try? Transcript.read(from: folder), Conversation.readSummaryVersions(from: folder),
             ChatLog.read(from: folder).messages, try? CatchUpLog.load(from: folder))
        }.value
        guard !Task.isCancelled else { return }
        (transcript, summaries, summary, chat) = (loaded, versions, versions.last?.text, messages)
        catchUps = catchUpLog?.catchUps ?? []
        leaveEmptyCatchUpTab()
        shownVersion = nil
        self.loaded = true
    }

    private func loadChat() async {
        let folder = conversation.folder
        let (log, entries) = await Task.detached(priority: .utility) {
            (ChatLog.read(from: folder), ChatArchive.entries(in: folder))
        }.value
        guard !Task.isCancelled else { return }
        (chat, chatTitle, archives) = (log.messages, log.title, entries)
    }

    /// The conversation's catch-ups; none when its `catchups.json` is missing or unreadable.
    private func loadCatchUps() async {
        let folder = conversation.folder
        let log = await Task.detached(priority: .utility) { try? CatchUpLog.load(from: folder) }.value
        guard !Task.isCancelled else { return }
        catchUps = log?.catchUps ?? []
        leaveEmptyCatchUpTab()
    }

    /// A conversation without catch-ups has no Catch-up tab: opened on it (the list's arrows keep the tab), it shows
    /// Summary.
    private func leaveEmptyCatchUpTab() {
        if catchUps.isEmpty && model.detailTab == .catchUp { model.detailTab = .summary }
    }

    private func start(_ kind: AppModel.AIJob.Kind) {
        let purpose: AppModel.AIPurpose = kind == .summary ? .summary : .chat
        guard let transcript, let provider = model.currentAIProvider(for: purpose) else { return }
        model.startAIJob(
            kind, conversation: conversation, transcript: transcript, summary: summary, history: chat,
            provider: provider, model: model.aiModel(for: provider, purpose: purpose))
    }

    private func ask() {
        let text = question.trimmingCharacters(in: .whitespacesAndNewlines)
        guard canRun, !text.isEmpty else { return }
        start(.question(text))
        question = ""
    }

    private func copyPrompt() {
        guard let transcript else { return }
        let prompt = PromptBuilder.copyPrompt(
            conversation: conversation, transcript: transcript, question: nil,
            languageSetting: model.settings.summaryLanguage, summaryLength: model.settings.summaryLength,
            chatLength: model.settings.chatLength)
        // Transient (nspasteboard.org) and this Mac only: clipboard managers and Universal Clipboard skip the transcript.
        let pasteboard = NSPasteboard.general
        let transient = NSPasteboard.PasteboardType("org.nspasteboard.TransientType")
        pasteboard.prepareForNewContents(with: .currentHostOnly)
        pasteboard.addTypes([.string, transient], owner: nil)
        pasteboard.setString(prompt, forType: .string)
        pasteboard.setString("", forType: transient)
        showToast("Copied — paste into any chat")
    }

    /// Shows `text` for 2 s; a newer toast replaces it and gets its own 2 s.
    private func showToast(_ text: LocalizedStringKey) {
        toastTask?.cancel()
        withAnimation(.snappy(duration: 0.2)) { toast = text }
        toastTask = Task {
            try? await Task.sleep(for: .seconds(2))
            guard !Task.isCancelled else { return }
            withAnimation(.snappy(duration: 0.2)) { toast = nil }
        }
    }

    private func copyAnswer(_ text: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
        showToast("Copied")
    }

    /// Citations open the transcript at their time (whole seconds, at most 24 h); web links open in the browser
    /// once the alert showing their destination is confirmed (the link text is model output); anything else is
    /// dropped.
    private func openLink(_ url: URL) -> OpenURLAction.Result {
        if url.scheme == MarkdownText.citationScheme {
            let id = conversation.id
            guard let seconds = Int(url.absoluteString.dropFirst(MarkdownText.citationScheme.count + 1)),
                  (0...86_400).contains(seconds) else { return .discarded }
            withAnimation(Motion.standard) { model.openTranscript(conversationID: id, at: Double(seconds)) }
            return .handled
        }
        guard ["http", "https"].contains(url.scheme?.lowercased() ?? "") else { return .discarded }
        pendingLink = url
        return .handled
    }
}

/// Every provider with the models it offers, on this Mac first; Foundation Models shows why it cannot run (the AI
/// panel's header, the global chat's bottom bar and the recording's right panel). Shows and edits `purpose`'s
/// default; for `.catchUp`, no in-app provider (the live transcriber holds the model during a recording).
struct AIModelMenu: View {
    let model: AppModel
    let purpose: AppModel.AIPurpose
    let height: CGFloat
    /// The model's name beside the chip; false: the chip only (the AI panel's narrow header), the name in the tooltip.
    var showsLabel = true
    /// nil: `CompactMenu`'s own.
    var horizontalPadding: CGFloat? = nil

    /// The side padding of the model and language menus in the narrowest headers.
    static let tightPadding: CGFloat = 8

    var body: some View {
        let providers = purpose == .catchUp ? model.aiProviders.filter { $0.kind != .inApp } : model.aiProviders
        let inApp = providers.filter { $0.kind == .inApp }
        let hosts = providers.filter { $0.kind != .inApp && !$0.leavesMac } + providers.filter(\.leavesMac)
        Menu {
            if !inApp.isEmpty {
                Section("On this Mac") {
                    ForEach(inApp) { provider in modelItem(provider, provider.defaultModel, title: provider.name) }
                }
            }
            ForEach(hosts) { provider in
                Section(provider.leavesMac ? String(localized: "\(provider.name) (leaves this Mac)") : provider.name) {
                    if provider.models.isEmpty {
                        Text("No model: choose one in Settings")
                    }
                    ForEach(provider.models, id: \.self) { id in
                        modelItem(provider, id, title: id.isEmpty ? String(localized: "Default") : id)
                    }
                }
            }
            Divider()
            Button("Choose Models…") { model.openSettings(tab: .providers, subTab: "cloud") }
        } label: {
            if showsLabel {
                AIMenuLabel(title: modelTitle, systemImage: "cpu")
            } else {
                HStack(spacing: 3) {
                    Image(systemName: "cpu")
                    MenuChevron()
                }
                .accessibilityLabel(helpText)
                .accessibilityValue(Text(verbatim: modelTitle))
            }
        }
        .modifier(CompactMenu(maxWidth: showsLabel ? 180 : nil, height: height, horizontalPadding: horizontalPadding))
        .layoutPriority(1)
        .help(showsLabel ? helpText : helpText + Text(verbatim: ": \(modelTitle)"))
    }

    private var helpText: Text {
        switch purpose {
        case .summary: Text("Model for summaries")
        case .chat: Text("Model for questions")
        case .catchUp: Text("Model for catch-ups and live chat")
        }
    }

    private func modelItem(_ provider: AppModel.AIProvider, _ id: String, title: String) -> some View {
        Toggle(isOn: Binding(
            get: { model.currentAIProvider(for: purpose)?.id == provider.id && currentModel == id },
            set: { if $0 { model.setAIModel(id, of: provider, purpose: purpose) } }
        )) {
            Text(title)
            if let reason = provider.unavailableReason { Text(reason) }
        }
        .disabled(provider.unavailableReason != nil)
    }

    private var modelTitle: String {
        guard let provider = model.currentAIProvider(for: purpose) else { return String(localized: "No AI model") }
        guard provider.kind != .inApp else { return provider.name }
        let id = currentModel ?? ""
        return id.isEmpty ? provider.name : id
    }

    /// `purpose`'s provider's picked model ("" = a CLI's own default).
    private var currentModel: String? {
        model.currentAIProvider(for: purpose).map { model.aiModel(for: $0, purpose: purpose) }
    }
}

/// The output language of summaries and answers (the AI panel's header and the global chat's bottom bar); only the
/// globe without `showsLabel`, where the header is narrow.
struct AILanguageMenu: View {
    let model: AppModel
    let height: CGFloat
    var showsLabel = true
    /// nil: `CompactMenu`'s own.
    var horizontalPadding: CGFloat? = nil

    var body: some View {
        Menu {
            Picker("Language", selection: Self.languageBinding(model)) {
                Self.options(model.settings.summaryLanguage)
            }
            .pickerStyle(.inline)
            .labelsHidden()
        } label: {
            if showsLabel {
                AIMenuLabel(title: LanguageOptions.shortCode(model.settings.summaryLanguage), systemImage: "globe")
            } else {
                HStack(spacing: 3) {
                    Image(systemName: "globe")
                    MenuChevron()
                }
                .accessibilityLabel("Language")
                .accessibilityValue(Text(verbatim: LanguageOptions.shortCode(model.settings.summaryLanguage)))
            }
        }
        .modifier(CompactMenu(maxWidth: nil, height: height, horizontalPadding: horizontalPadding))
        .help("Language of summaries and answers (Auto = the transcript's language for summaries, the question's for answers)")
    }

    /// The languages offered, then `current` when it is none of them (also Settings › AI Settings' "AI answer
    /// language").
    @ViewBuilder
    static func options(_ current: String) -> some View {
        Text("Auto").tag("auto")
        Text("English").tag("en")
        Text("French").tag("fr")
        Text("Vietnamese").tag("vi")
        if !["auto", "en", "fr", "vi"].contains(current) {
            Text(current).tag(current)
        }
    }

    static func languageBinding(_ model: AppModel) -> Binding<String> {
        Binding(get: { model.settings.summaryLanguage }, set: { value in model.updateSettings { $0.summaryLanguage = value } })
    }
}

/// The label of a bottom bar's menu: the bar's style draws no menu arrow, so the chevron says it opens a menu.
struct AIMenuLabel: View {
    let title: String
    let systemImage: String

    var body: some View {
        HStack(spacing: 6) {
            Label {
                Text(title).lineLimit(1).truncationMode(.tail)
            } icon: {
                Image(systemName: systemImage)
            }
            MenuChevron()
        }
    }
}

/// Why no model can run for `purpose`: no provider set up, and why Foundation Models is unavailable.
struct AISetupHints: View {
    let model: AppModel
    let purpose: AppModel.AIPurpose

    var body: some View {
        if model.currentAIProvider(for: purpose) == nil {
            Label("No AI model: set up a provider or download a model in Settings › AI Providers.",
                  systemImage: "exclamationmark.triangle")
            if let reason = AppModel.foundationModelsUnavailableReason {
                Label(reason, systemImage: "info.circle")
            }
        }
    }
}

/// Opens a web link from model output once the alert showing its destination is confirmed; `link` is the one asked.
struct WebLinkConfirmation: ViewModifier {
    @Binding var link: URL?

    func body(content: Content) -> some View {
        content.alert(
            link?.host().map { Text("Open \($0)?") } ?? Text("Open this link?"),
            isPresented: Binding(get: { link != nil }, set: { if !$0 { link = nil } }),
            presenting: link
        ) { url in
            Button("Open") { NSWorkspace.shared.open(url) }
            Button("Cancel", role: .cancel) {}
        } message: { url in
            Text(url.absoluteString)
        }
    }
}

/// A small capsule button of the AI pane's busy line.
struct CapsuleButton: ViewModifier {
    func body(content: Content) -> some View {
        content
            .buttonStyle(.bordered)
            .buttonBorderShape(.capsule)
        .controlSize(.small)
    }
}

/// A row of the History panel: lighter on hover, darker while pressed, dimmed when disabled. `hovered`, when set, is
/// the row's hover in place of the label's own, so a button over the row keeps it highlighted.
struct HistoryRowStyle: ButtonStyle {
    var hovered: Bool?

    func makeBody(configuration: Configuration) -> some View {
        Highlight(configuration: configuration, rowHovered: hovered)
    }

    private struct Highlight: View {
        let configuration: ButtonStyleConfiguration
        let rowHovered: Bool?
        @State private var hovered = false
        @Environment(\.isEnabled) private var isEnabled

        var body: some View {
            let shape = RoundedRectangle(cornerRadius: 8, style: .continuous)
            let highlighted = (rowHovered ?? hovered) && isEnabled
            configuration.label
                .background(shape.fill(
                    configuration.isPressed ? Palette.pressedFill : highlighted ? Palette.hoverFill : .clear))
                .contentShape(shape)
                .opacity(isEnabled ? 1 : 0.45)
                .animation(Motion.hover, value: hovered)
                .onHover { hovered = $0 }
        }
    }
}

/// The History panels' search field (the conversation's, the library chat's), under their header: filters the earlier
/// chats by title and messages as typed; Esc clears the query, then hides the field (`onHide`), as the catch-up search.
struct ChatHistorySearchField: View {
    @Binding var query: String
    @Binding var focusRequest: Bool
    let onHide: () -> Void

    var body: some View {
        HStack(spacing: 6) {
            Image(systemName: "magnifyingglass")
                .foregroundStyle(.secondary)
            SearchTextField(text: $query, focusRequest: $focusRequest,
                            accessibilityLabel: String(localized: "Search chats")) {
                if query.isEmpty {
                    onHide()
                } else {
                    query = ""
                }
            }
            if !query.isEmpty {
                Button {
                    query = ""
                } label: {
                    Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary)
                }
                .buttonStyle(.plain)
                .help("Clear search")
                .accessibilityLabel("Clear search")
            }
        }
        .padding(.horizontal, 10)
        .frame(height: MainView.headerControlHeight + 4)
        .modifier(InputCapsule())
    }
}

/// The AI panel's search marks in one text: the matches' ranges, and the index of the current one.
struct SearchMarks: Equatable {
    var ranges: [NSRange] = []
    var current: Int?

    /// As the transcript's (`SegmentFragment`): the find color, lighter for the others, full with dark text for the
    /// current one.
    static func attributes(current: Bool) -> [NSAttributedString.Key: Any] {
        current
            ? [.backgroundColor: NSColor.systemOrange.withAlphaComponent(0.8), .foregroundColor: NSColor.black]
            : [.backgroundColor: NSColor.systemYellow.withAlphaComponent(0.3)]
    }
}

/// The search field of the transcript and of the AI panel's tabs, sliding up from under their content: the query is
/// highlighted as typed, "n/m" or No results beside it; Return and the down chevron go to the next match, Shift-Return
/// and the up chevron to the previous one; Esc clears the query, then closes the field (`close`).
struct FindBar: View {
    @Binding var query: String
    @Binding var focusRequest: Bool
    /// The placeholder and the field's accessibility label.
    let label: String
    let count: Int
    let index: Int
    /// 1 for the next match, -1 for the previous one.
    let step: (Int) -> Void
    let close: () -> Void

    var body: some View {
        HStack(spacing: 6) {
            Image(systemName: "magnifyingglass")
                .foregroundStyle(.secondary)
            SearchTextField(text: $query, focusRequest: $focusRequest, accessibilityLabel: label,
                            onEscape: { if query.isEmpty { close() } else { query = "" } },
                            onReturn: { step($0 ? -1 : 1) })
            if !query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                Group {
                    if count == 0 {
                        Text("No results")
                    } else {
                        Text(verbatim: "\(index + 1)/\(count)")
                    }
                }
                .font(.caption)
                .monospacedDigit()
                .foregroundStyle(.secondary)
                .fixedSize()
                Button { step(-1) } label: {
                    Label("Previous match", systemImage: "chevron.up").labelStyle(.iconOnly)
                }
                .buttonStyle(.borderless)
                .help("Previous match")
                .disabled(count == 0)
                Button { step(1) } label: {
                    Label("Next match", systemImage: "chevron.down").labelStyle(.iconOnly)
                }
                .buttonStyle(.borderless)
                .help("Next match")
                .disabled(count == 0)
            }
            Button(action: close) {
                Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary)
            }
            .buttonStyle(.plain)
            .help("Close search")
            .accessibilityLabel("Close search")
        }
        .padding(.horizontal, 10)
        .frame(height: BottomBarButtonStyle.height)
        .modifier(InputCapsule())
    }
}

/// A button of the AI panel's header and footer and of the transcript's bar: `height` tall (a circle for an icon, with
/// no `horizontalPadding`), a capsule of the input color, or the accent color when `prominent`; lighter on hover,
/// darker while pressed, dimmed when disabled.
struct BottomBarButtonStyle: ButtonStyle {
    /// Every control of the bottom bars: buttons, menus, speaker chips and the one-line question field.
    static let height: CGFloat = 30

    var height = Self.height
    var prominent = false
    var horizontalPadding: CGFloat = 12
    /// A menu's trigger: it stays pressed while its menu is open, and shows the hover look then, not the darker one.
    var menu = false

    func makeBody(configuration: Configuration) -> some View {
        Highlight(configuration: configuration, height: height, prominent: prominent,
                  horizontalPadding: horizontalPadding, menu: menu)
    }

    private struct Highlight: View {
        let configuration: ButtonStyleConfiguration
        let height: CGFloat
        let prominent: Bool
        let horizontalPadding: CGFloat
        let menu: Bool
        @State private var hovered = false
        @Environment(\.isEnabled) private var isEnabled

        var body: some View {
            configuration.label
                .font(.callout.weight(prominent ? .semibold : .regular))
                .foregroundStyle(prominent ? AnyShapeStyle(.white) : AnyShapeStyle(.primary))
                .padding(.horizontal, horizontalPadding)
                .frame(minWidth: height, minHeight: height, maxHeight: height)
                .background(
                    Capsule()
                        .fill(prominent ? AnyShapeStyle(Color.accentColor) : AnyShapeStyle(Palette.inputSurface))
                        .overlay(Capsule().fill(
                            configuration.isPressed && !menu ? Color.black.opacity(0.2)
                                : (hovered || configuration.isPressed) && isEnabled ? Palette.hoverFill : .clear)))
                .contentShape(Capsule())
                .opacity(isEnabled ? 1 : 0.45)
                .animation(Motion.hover, value: hovered)
                .onHover { hovered = $0 }
        }
    }
}

/// The arrow of a bar's menu (the transcript's Re-transcribe too).
struct MenuChevron: View {
    var body: some View {
        Image(systemName: "chevron.down")
            .font(.system(size: 9, weight: .bold))
            .accessibilityHidden(true)
    }
}

/// A capsule menu of the AI panel's header and of the bars, `height` tall like the buttons beside it: its title's
/// width, a long one cut to `maxWidth` with an ellipsis (never cut without one).
struct CompactMenu: ViewModifier {
    let maxWidth: CGFloat?
    let height: CGFloat
    /// nil: 10 for a short menu, 12 for a full-height one.
    var horizontalPadding: CGFloat? = nil

    func body(content: Content) -> some View {
        let menu = content
            .menuStyle(.button)
            .buttonStyle(BottomBarButtonStyle(height: height,
                                              horizontalPadding: horizontalPadding
                                                  ?? (height < BottomBarButtonStyle.height ? 10 : 12),
                                              menu: true))
        if let maxWidth {
            WidthCap(maxWidth: maxWidth) { menu }
        } else {
            menu.fixedSize()
        }
    }
}

/// Its content, measured `extra` wider for a `ViewThatFits` (which asks for the ideal width): a header row whose tab
/// shows fewer buttons than the other tab picks the same step as that tab, so the tabs' look does not change with the
/// selected tab. Laid out at the width it gets, with no gap.
struct ReservedWidth: Layout {
    let extra: CGFloat

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        guard let content = subviews.first else { return .zero }
        var size = content.sizeThatFits(proposal)
        if proposal.width == nil { size.width += extra }
        return size
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        subviews.first?.place(at: bounds.origin, proposal: ProposedViewSize(width: bounds.width, height: bounds.height))
    }
}

/// Its content at the proposed width, at most `maxWidth`, and no wider than the content: unlike
/// `frame(maxWidth:)`, a short title leaves no gap after it.
struct WidthCap: Layout {
    let maxWidth: CGFloat

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        guard let content = subviews.first else { return .zero }
        let width = min(proposal.width ?? content.sizeThatFits(.unspecified).width, maxWidth)
        return content.sizeThatFits(ProposedViewSize(width: width, height: proposal.height))
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        subviews.first?.place(at: bounds.origin, proposal: ProposedViewSize(width: bounds.width, height: bounds.height))
    }
}

/// Model Markdown, line by line: `#` headings, `-`/`*` bullets (indented by their leading spaces) and paragraphs,
/// each with inline Markdown; `[mm:ss]` citations become `echodiar-time:<seconds>` links, or with `references` (a
/// library-chat answer) `[n mm:ss]` / `[n]` citations become `echodiar-ref:` links. No HTML, no web view; images
/// (`![alt](url)`) show their alt text only, nothing is fetched. A streaming answer is parsed on every update, so it is
/// formatted as it arrives. One read-only text, so a drag selects across its lines and ⌘C copies them as plain text;
/// a link click goes to the environment's `openURL`, as a SwiftUI `Text` link does.
struct MarkdownText: View {
    let text: String
    /// The sources of a library-chat answer; nil for an answer about one conversation.
    var references: [ChatLog.Reference]? = nil
    /// A search whose matches in the rendered text are highlighted (the catch-up search, `SearchText.matchRanges`);
    /// none when empty.
    var highlight = ""
    /// The AI panel's search: its matches in the rendered text (`plain`), and the current one, stronger and
    /// scrolled into view.
    var marks = SearchMarks()

    static let citationScheme = "echodiar-time"

    var body: some View {
        MarkdownTextRepresentable(string: Self.attributed(text, references: references, highlight: highlight,
                                                          marks: marks),
                                  current: marks.current.map { marks.ranges[$0] })
            .frame(maxWidth: .infinity, alignment: .leading)
    }

    /// The text as shown, where the AI panel's search looks.
    static func plain(_ text: String, references: [ChatLog.Reference]? = nil) -> String {
        attributed(text, references: references, highlight: "").string
    }

    enum Kind { case heading, bullet, paragraph }

    static func lines(_ text: String) -> [(kind: Kind, indent: Int, text: String)] {
        text.components(separatedBy: .newlines).compactMap { raw in
            let line = raw.trimmingCharacters(in: .whitespaces)
            guard !line.isEmpty else { return nil }
            let indent = raw.prefix { $0 == " " || $0 == "\t" }.count / 2
            let hashes = line.prefix { $0 == "#" }.count
            if (1...6).contains(hashes), line.dropFirst(hashes).first == " " {
                return (.heading, 0, String(line.dropFirst(hashes + 1)))
            }
            if let marker = line.first, "-*+•".contains(marker), line.dropFirst().first == " " {
                return (.bullet, indent, String(line.dropFirst(2)))
            }
            return (.paragraph, 0, line)
        }
    }

    /// The lines as one text, laid out as the old stack of lines: 4 pt between lines and 4 pt more above a heading
    /// (headline font), each bullet "•" at 14 pt per level with its wrapped lines aligned past it, the body font
    /// elsewhere. The highlight is found in the rendered characters, so Markdown marks never split a match.
    static func attributed(_ text: String, references: [ChatLog.Reference]?, highlight: String,
                           marks: SearchMarks = SearchMarks()) -> NSAttributedString {
        let bodyFont = NSFont.preferredFont(forTextStyle: .body)
        let headlineFont = NSFont.preferredFont(forTextStyle: .headline)
        let bulletWidth = ("•" as NSString).size(withAttributes: [.font: bodyFont]).width
        let result = NSMutableAttributedString()
        for (index, line) in lines(text).enumerated() {
            let font = line.kind == .heading ? headlineFont : bodyFont
            let paragraph = NSMutableParagraphStyle()
            paragraph.paragraphSpacingBefore = (index == 0 ? 0 : 4) + (line.kind == .heading ? 4 : 0)
            let start = result.length
            if line.kind == .bullet {
                let lead = CGFloat(line.indent) * 14
                paragraph.firstLineHeadIndent = lead
                paragraph.headIndent = lead + bulletWidth + 6
                paragraph.tabStops = [NSTextTab(textAlignment: .left, location: paragraph.headIndent)]
                result.append(NSAttributedString(string: "•\t", attributes: [.font: font]))
            }
            let inline = inline(line.text, references: references)
            for run in inline.runs {
                var attributes: [NSAttributedString.Key: Any] = [.font: Self.font(font, run.inlinePresentationIntent)]
                if let link = run.link { attributes[.link] = link }
                if run.inlinePresentationIntent?.contains(.strikethrough) == true {
                    attributes[.strikethroughStyle] = NSUnderlineStyle.single.rawValue
                }
                result.append(NSAttributedString(string: String(inline[run.range].characters), attributes: attributes))
            }
            result.append(NSAttributedString(string: "\n", attributes: [.font: font]))
            result.addAttributes([.paragraphStyle: paragraph, .foregroundColor: NSColor.labelColor],
                                 range: NSRange(location: start, length: result.length - start))
        }
        if result.length > 0 { result.deleteCharacters(in: NSRange(location: result.length - 1, length: 1)) }
        for (index, range) in marks.ranges.enumerated() where NSMaxRange(range) <= result.length {
            result.addAttributes(SearchMarks.attributes(current: index == marks.current), range: range)
        }
        guard !highlight.isEmpty else { return result }
        let plain = result.string
        for range in SearchText.matchRanges(of: highlight, in: plain) {
            result.addAttributes([.backgroundColor: NSColor.yellow, .foregroundColor: NSColor.black],
                                 range: NSRange(range, in: plain))
        }
        return result
    }

    /// `base` with a run's bold, italic or code (monospaced) style, as a SwiftUI `Text` draws them.
    private static func font(_ base: NSFont, _ intent: InlinePresentationIntent?) -> NSFont {
        guard let intent else { return base }
        var traits = base.fontDescriptor.symbolicTraits
        if intent.contains(.code) {
            return .monospacedSystemFont(ofSize: base.pointSize, weight: traits.contains(.bold) ? .bold : .regular)
        }
        if intent.contains(.stronglyEmphasized) { traits.insert(.bold) }
        if intent.contains(.emphasized) { traits.insert(.italic) }
        return NSFont(descriptor: base.fontDescriptor.withSymbolicTraits(traits), size: base.pointSize) ?? base
    }

    static func inline(_ text: String, references: [ChatLog.Reference]? = nil) -> AttributedString {
        let linked = references.map { LibraryCitation.linked(text, references: $0) }
            ?? TranscriptCitation.linked(text, scheme: citationScheme)
        return (try? AttributedString(
            markdown: linked, options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace))) ?? AttributedString(text)
    }
}

/// `MarkdownText`'s text view: no scroll view, no background, no insets, as tall as its text at the proposed width.
/// A new text keeps the selection (clamped), so a streaming answer can be selected while it is written.
private struct MarkdownTextRepresentable: NSViewRepresentable {
    let string: NSAttributedString
    /// The search's current match, scrolled into view each time it changes.
    let current: NSRange?

    func makeCoordinator() -> MarkdownTextCoordinator { MarkdownTextCoordinator() }

    func makeNSView(context: Context) -> MarkdownNSTextView {
        let textView = MarkdownNSTextView(usingTextLayoutManager: false)
        textView.isEditable = false
        textView.isSelectable = true
        textView.isRichText = true
        textView.allowsUndo = false
        textView.importsGraphics = false
        textView.usesFontPanel = false
        textView.drawsBackground = false
        textView.textContainerInset = .zero
        textView.isVerticallyResizable = false
        textView.isHorizontallyResizable = false
        textView.textContainer?.widthTracksTextView = true
        textView.textContainer?.lineFragmentPadding = 0
        textView.linkTextAttributes = [.foregroundColor: NSColor.controlAccentColor, .cursor: NSCursor.pointingHand]
        textView.delegate = context.coordinator
        return textView
    }

    func updateNSView(_ textView: MarkdownNSTextView, context: Context) {
        context.coordinator.openURL = context.environment.openURL
        defer {
            if current != context.coordinator.scrolled {
                context.coordinator.scrolled = current
                if let current { scroll(textView, to: current) }
            }
        }
        // Against the last string set, not the storage: font fallback (emoji, CJK) changes the storage's fonts, so
        // it would never compare equal and every SwiftUI update would reset a selection.
        guard let storage = textView.textStorage, context.coordinator.shown?.isEqual(to: string) != true else { return }
        context.coordinator.shown = string
        let selection = textView.selectedRange()
        storage.setAttributedString(string)
        let location = min(selection.location, storage.length)
        textView.setSelectedRange(NSRange(location: location, length: min(selection.length, storage.length - location)))
        textView.invalidateIntrinsicContentSize()
    }

    /// After the update's layout: the scroll view around the panel's content scrolls the match to its middle.
    private func scroll(_ textView: MarkdownNSTextView, to range: NSRange) {
        DispatchQueue.main.async {
            guard let layoutManager = textView.layoutManager, let container = textView.textContainer,
                  NSMaxRange(range) <= (textView.textStorage?.length ?? 0) else { return }
            let glyphs = layoutManager.glyphRange(forCharacterRange: range, actualCharacterRange: nil)
            let rect = layoutManager.boundingRect(forGlyphRange: glyphs, in: container)
            guard !textView.visibleRect.contains(rect) else { return }
            let margin = max(0, (textView.enclosingScrollView?.contentView.bounds.height ?? 0) / 2 - rect.height / 2)
            textView.scrollToVisible(rect.insetBy(dx: 0, dy: -margin))
        }
    }

    func sizeThatFits(_ proposal: ProposedViewSize, nsView textView: MarkdownNSTextView, context: Context) -> CGSize? {
        guard let container = textView.textContainer, let layoutManager = textView.layoutManager else { return nil }
        let width = proposal.width.flatMap { $0.isFinite ? $0 : nil }
        container.size = NSSize(width: width ?? .greatestFiniteMagnitude, height: .greatestFiniteMagnitude)
        layoutManager.ensureLayout(for: container)
        let used = layoutManager.usedRect(for: container)
        return CGSize(width: width ?? ceil(used.width), height: ceil(used.height))
    }
}

@MainActor
private final class MarkdownTextCoordinator: NSObject, NSTextViewDelegate {
    var openURL: OpenURLAction?
    /// The string last put in the text view.
    var shown: NSAttributedString?
    /// The search match last scrolled to.
    var scrolled: NSRange?

    /// Always handled here, so AppKit never opens a link itself: a handler that discards it (catch-ups, live chat)
    /// leaves it inert.
    func textView(_ textView: NSTextView, clickedOnLink link: Any, at charIndex: Int) -> Bool {
        if let url = (link as? URL) ?? (link as? String).flatMap(URL.init(string:)) { openURL?(url) }
        return true
    }
}

/// Copies the selection as plain text only (⌘C, Edit › Copy, the context menu, drags), with the read-only menu.
private final class MarkdownNSTextView: NSTextView {
    override var writablePasteboardTypes: [NSPasteboard.PasteboardType] { [.string] }

    /// NSTextView's own `writeSelection(to:type:)` writes nothing for `.string` (see `LiveCaptionsNSTextView`).
    override func writeSelection(to pboard: NSPasteboard, type: NSPasteboard.PasteboardType) -> Bool {
        guard type == .string else { return super.writeSelection(to: pboard, type: type) }
        return pboard.setString((string as NSString).substring(with: selectedRange()), forType: .string)
    }

    override func menu(for event: NSEvent) -> NSMenu? { readOnlyMenu(super.menu(for: event)) }

    /// A selection in another answer (or anywhere else) clears this one, so only one shows at a time.
    override func resignFirstResponder() -> Bool {
        guard super.resignFirstResponder() else { return false }
        setSelectedRange(NSRange(location: selectedRange().location, length: 0))
        return true
    }
}
