//
//  SettingsView.swift
//  Burrow
//
//  Settings as a first-class pane — the same header/typography pattern as
//  Home and Software (capsule segments over a hairline, full-bleed) with
//  tabs General | Maintenance | Menu Bar | Advanced. No close chrome: you
//  leave the way you arrived, via the top nav (Esc also returns to the
//  previous pane). The common path (permissions, language, login, Dock)
//  stays in the first tab; everything agentic or experimental (MCP, query
//  server, AI, telemetry, Touch ID, engine) lives in Advanced so it can't
//  ambush a newcomer. Same contract as ever: reads/writes the typed
//  `Store`, surfaces `Maintenance` status, notes which changes need a
//  relaunch.
//

import SwiftUI
import AppKit
import LocalAuthentication
import ServiceManagement

struct SettingsView: View {
    enum Tab: String, CaseIterable, Identifiable {
        case general, maintenance, menuBar, advanced
        var id: String { rawValue }
        var label: String {
            switch self {
            case .general:     return NSLocalizedString("General", comment: "")
            case .maintenance: return NSLocalizedString("Maintenance", comment: "")
            case .menuBar:     return NSLocalizedString("Menu Bar", comment: "")
            case .advanced:    return NSLocalizedString("Advanced", comment: "")
            }
        }
    }

    /// Wired by AppDelegate; the only consumer is "Run maintenance now".
    var onRunMaintenance: (() -> Void)?
    /// Esc leaves Settings (RootView returns to the previous pane). The
    /// pane has no close chrome of its own — navigation lives in TopNav.
    var onClose: (() -> Void)?

    @State private var tab: Tab = .general

    // General
    @State private var fdaGranted = Privacy.hasFullDiskAccess()
    /// FDA state when Settings opened. The running process only gains FDA
    /// on relaunch, so a false→true flip (the user granted it just now)
    /// means a relaunch is needed before scans can reach protected caches.
    private let fdaAtOpen = Privacy.hasFullDiskAccess()
    @State private var appLanguage: String = Store.appLanguage
    @State private var launchAtLogin: Bool = SMAppService.mainApp.status == .enabled
    @State private var hideDockIcon: Bool = Store.hideDockIcon
    @State private var skipIntro: Bool = Store.skipIntro
    @State private var notifyOnCompletion: Bool = Store.notifyOnCompletion
    @State private var smartReminders: Bool = Store.smartRemindersEnabled
    @State private var autoCheckUpdates: Bool = Store.autoCheckForUpdates
    @State private var cameraMicIndicator: Bool = Store.cameraMicIndicatorEnabled

    // Maintenance
    @State private var whitelistPatterns: [String] = []
    @State private var newPattern = ""
    @State private var removalMode: CacheRemovalMode = Store.cacheRemovalMode
    @State private var sampleIntervalSeconds: Int = Store.sampleIntervalSeconds
    @State private var retentionDays: Int = Store.retentionDays
    @State private var autoVacuum: Bool = Store.autoVacuum
    @State private var dbSizeText: String = "—"
    @State private var lastMaintenanceText: String = "—"

    // Menu bar
    @State private var showMenuBarIcon: Bool = Store.showMenuBarIcon
    @State private var displayMode: MenuBarDisplayMode = Store.menuBarDisplayMode
    @State private var inputLock: Bool = Store.cleanScreenInputLock
    @State private var axTrusted = CleanScreen.inputLockPermitted()

    // Advanced
    @State private var queryServerEnabled: Bool = Store.queryServerEnabled
    @State private var telemetryEnabled: Bool = Store.telemetryEnabled
    @State private var mcpActionsEnabled: Bool = Store.mcpActionsEnabled
    @State private var mcpIrreversibleEnabled: Bool = Store.mcpIrreversibleEnabled
    @State private var aiEnabled: Bool = Store.aiEnabled
    @State private var aiProvider: String = Store.aiProvider
    @State private var aiOllamaModel: String = Store.aiOllamaModel
    @State private var aiOpenAIBaseURL: String = Store.aiOpenAIBaseURL
    @State private var aiOpenAIModel: String = Store.aiOpenAIModel
    @State private var aiOpenAIKey: String = Store.aiOpenAIKey
    @State private var moleVersion: String = "—"
    @State private var moleUpdating = false
    @State private var copiedConfig = false
    @State private var touchIDStatus = "—"
    @State private var touchIDEnabled = false
    @State private var touchIDBusy = false
    @State private var touchIDAvailable = false

    /// Drop-in MCP config for Claude Code / Cursor / Codex / Cline — they
    /// all share the same `{command, args}` stdio shape, so one snippet
    /// covers every agent.
    private let mcpConfigJSON = """
    {
      "mcpServers": {
        "burrow": {
          "command": "/Applications/Burrow.app/Contents/MacOS/Burrow",
          "args": ["--mcp"]
        }
      }
    }
    """

    var body: some View {
        VStack(spacing: 0) {
            header
                .padding(.horizontal, 18).padding(.top, 8).padding(.bottom, 10)
            Rectangle().fill(Brand.hairline).frame(height: 1)
            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    switch tab {
                    case .general:     generalTab
                    case .maintenance: maintenanceTab
                    case .menuBar:     menuBarTab
                    case .advanced:    advancedTab
                    }
                }
                // Full-bleed pane, readable column: the cards cap at a
                // comfortable measure and stay centered in wide windows.
                .frame(maxWidth: 680)
                .padding(.horizontal, 20)
                .padding(.top, 14)
                .padding(.bottom, 22)
                .frame(maxWidth: .infinity)
            }
            .scrollIndicators(.hidden)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .onExitCommand { onClose?() }
        .onAppear {
            refreshStatusLabels(); loadMoleVersion(); loadTouchIDStatus()
            whitelistPatterns = MoleWhitelist.live.patterns()
        }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            fdaGranted = Privacy.hasFullDiskAccess()
            axTrusted = CleanScreen.inputLockPermitted()
        }
    }

    // MARK: - Header (capsule segments, same pattern as Home/Software)

    private var header: some View {
        HStack(spacing: 12) {
            segmented
            Spacer()
        }
    }

    private var segmented: some View {
        HStack(spacing: 2) {
            ForEach(Tab.allCases) { t in
                let on = t == tab
                Button { withAnimation(.easeOut(duration: 0.14)) { tab = t } } label: {
                    Text(t.label)
                        .font(Brand.mono(12, on ? .semibold : .regular))
                        .foregroundStyle(on ? Color.black : Brand.textSecondary)
                        .padding(.horizontal, 12).padding(.vertical, 5)
                        .background { if on { Capsule().fill(.white) } }
                        .contentShape(Capsule())
                }
                .buttonStyle(.plain)
                .accessibilityAddTraits(on ? .isSelected : [])
            }
        }
        .padding(3)
        .background(Capsule().fill(Color.black.opacity(0.22)))
        .overlay(Capsule().strokeBorder(Brand.hairline, lineWidth: 1))
    }

    // MARK: - General

    private var generalTab: some View {
        Group {
            section("Permissions", "lock.shield") {
                PermissionRow(
                    title: NSLocalizedString("Full Disk Access", comment: ""),
                    benefit: fdaGranted
                        ? NSLocalizedString("On. Burrow can reach system and app caches.", comment: "")
                        : NSLocalizedString("Off. Safe scan in use — most system caches stay out of reach.", comment: ""),
                    granted: fdaGranted,
                    onOpenSettings: { Privacy.openFullDiskAccessSettings() },
                    onCheck: { fdaGranted = Privacy.hasFullDiskAccess() },
                    // Granted this session → macOS only hands FDA to a fresh
                    // process, so offer a one-click relaunch instead of the
                    // system's "quit it yourself" prompt.
                    onRelaunch: (fdaGranted && !fdaAtOpen) ? { Privacy.relaunch() } : nil)
            }

            section("Language", "globe") {
                HStack {
                    Text(NSLocalizedString("App language", comment: "")).font(Brand.sans(12)).foregroundStyle(Brand.textPrimary)
                    Spacer()
                    Picker("", selection: $appLanguage) {
                        Text(NSLocalizedString("System", comment: "")).tag("")
                        Text(verbatim: "English").tag("en")
                        Text(verbatim: "简体中文").tag("zh-Hans")
                        Text(verbatim: "繁體中文").tag("zh-Hant")
                    }
                    .labelsHidden().pickerStyle(.menu).tint(Brand.textSecondary).fixedSize()
                    .onChange(of: appLanguage) { _, v in
                        Store.appLanguage = v
                        promptRelaunch()
                    }
                }
                footnote("Burrow ships English, 简体中文, and 繁體中文. A language change takes effect after a relaunch.")
            }

            section("Startup & window", "macwindow") {
                toggleRow("Launch at Login", isOn: $launchAtLogin) { on in
                    do {
                        if on { try SMAppService.mainApp.register() }
                        else { try SMAppService.mainApp.unregister() }
                    } catch {
                        launchAtLogin = SMAppService.mainApp.status == .enabled
                    }
                }
                footnote("Starts Burrow quietly at login so sampling and the menu-bar monitor are always on.")
                toggleRow("Hide Dock Icon when window closes", isOn: $hideDockIcon) { on in
                    Store.hideDockIcon = on
                }
                .disabled(!showMenuBarIcon)
                footnote("On: Burrow retreats to the menu bar when you close the window. Off: it stays in the Dock. With the menu-bar icon hidden, the Dock icon always stays — otherwise the app would be unreachable.")
                toggleRow("Skip Intro Screens", isOn: $skipIntro) { Store.skipIntro = $0 }
                footnote("Jumps past the tools' idle screens where a read-only preview can start right away (Clean starts its scan when you open the tab).")
                HStack {
                    Text(NSLocalizedString("Onboarding", comment: "")).font(Brand.sans(12)).foregroundStyle(Brand.textPrimary)
                    Spacer()
                    PillButton(title: "Replay onboarding", filled: false) {
                        if #available(macOS 14, *) { AppDelegate.shared?.replayOnboarding() }
                    }
                }
                footnote("Shows the first-run slides again (permissions, what's included). Finishing them marks onboarding done as usual.")
            }

            section("Notifications", "bell.badge") {
                toggleRow("Notify when long operations finish", isOn: $notifyOnCompletion) {
                    Store.notifyOnCompletion = $0
                }
                footnote("Clean, Optimize and Uninstall post a notice with the result (e.g. space freed) when they finish while Burrow is in the background. macOS asks for notification permission the first time one fires.")
                toggleRow("Smart reminders", isOn: $smartReminders) { Store.smartRemindersEnabled = $0 }
                footnote("Occasional, throttled nudges: it's been a couple of weeks since your last clean, free disk space dropped under 10%, or the Trash is holding more than 5 GB. Each fires at most once a week, never while you're in the app. Off by default.")
            }

            section("About", "info.circle") {
                infoRow("Version", appVersionText)
                toggleRow("Check for updates automatically", isOn: $autoCheckUpdates) { on in
                    Store.autoCheckForUpdates = on
                    if on { AppUpdate.shared.checkNow() }
                }
                Text("Burrow checks GitHub for new releases on launch and about once a day, and shows a banner if one is found. It never installs anything on its own.")
                    .font(Brand.sans(11)).foregroundStyle(Brand.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
                HStack(spacing: 10) {
                    PillButton(title: "Check for Updates", filled: false) { UpdateCheck.checkNow() }
                    PillButton(title: "About Burrow", filled: false) { AppDelegate.shared?.showAboutPanel() }
                    Spacer()
                    Link(NSLocalizedString("Source on GitHub", comment: ""),
                         destination: URL(string: "https://github.com/caezium/Burrow")!)
                        .font(Brand.sans(11, .semibold)).foregroundStyle(Brand.green)
                }
            }
        }
    }

    private var appVersionText: String {
        let v = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "?"
        let b = Bundle.main.infoDictionary?["CFBundleVersion"] as? String ?? "?"
        return "v\(v) (\(b))"
    }

    // MARK: - Maintenance

    private var maintenanceTab: some View {
        Group {
            section("Protected Items", "shield.lefthalf.filled") {
                Text("Paths and glob patterns Mole never cleans — `mo clean` and `mo optimize` skip anything matching them. \u{201C}Always skip this\u{201D} in the Clean review writes here too.")
                    .font(Brand.sans(11)).foregroundStyle(Brand.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
                if whitelistPatterns.isEmpty {
                    Text("No protected items yet.").font(Brand.mono(10)).foregroundStyle(Brand.textTertiary)
                } else {
                    ForEach(whitelistPatterns, id: \.self) { pattern in
                        HStack {
                            Text(pattern).font(Brand.mono(10)).foregroundStyle(Brand.textPrimary)
                                .lineLimit(1).truncationMode(.middle)
                            Spacer()
                            Button {
                                try? MoleWhitelist.live.remove(pattern)
                                whitelistPatterns = MoleWhitelist.live.patterns()
                            } label: {
                                Image(systemName: "minus.circle").font(.system(size: 11))
                                    .foregroundStyle(Brand.textTertiary)
                            }
                            .buttonStyle(.plain)
                            .accessibilityLabel(String(format: NSLocalizedString("Remove %@ from protected items", comment: ""), pattern))
                        }
                    }
                }
                HStack(spacing: 8) {
                    TextField(NSLocalizedString("Add a path or glob pattern", comment: ""), text: $newPattern)
                        .textFieldStyle(.plain).font(Brand.mono(11))
                    Button(NSLocalizedString("Add", comment: "")) {
                        let p = newPattern.trimmingCharacters(in: .whitespaces)
                        guard !p.isEmpty else { return }
                        try? MoleWhitelist.live.add(p)
                        newPattern = ""
                        whitelistPatterns = MoleWhitelist.live.patterns()
                    }
                    .buttonStyle(.plain).font(Brand.sans(11, .semibold)).foregroundStyle(Brand.green)
                }
                .padding(.top, 2)
            }

            section("Cache removal", "trash") {
                HStack {
                    Text(NSLocalizedString("Removal mode", comment: "")).font(Brand.sans(12)).foregroundStyle(Brand.textPrimary)
                    Spacer()
                    Picker("", selection: $removalMode) {
                        Text(NSLocalizedString("Permanent", comment: "")).tag(CacheRemovalMode.permanent)
                        Text(NSLocalizedString("Trash", comment: "")).tag(CacheRemovalMode.trash)
                    }
                    .labelsHidden().pickerStyle(.segmented).frame(width: 200)
                    .onChange(of: removalMode) { _, v in Store.cacheRemovalMode = v }
                }
                footnote("Permanent (default): the engine removes caches outright — freed space is real, immediately. Trash: reviewed, ticked paths go to the Trash instead — recoverable, but space frees only when Trash empties, and the run won't appear in `mo history`.")
            }

            section("Storage", "internaldrive") {
                infoRow("Currently using", dbSizeText)
                infoRow("Last maintenance", lastMaintenanceText)
                HStack {
                    Spacer()
                    PillButton(title: "Run maintenance now", filled: false) {
                        onRunMaintenance?(); refreshStatusLabels()
                    }
                }
                footnote("History lives at ~/Library/Application Support/Burrow/burrow.db. Rows past the retention window are pruned hourly.")
            }

            section("History retention", "calendar") {
                pickerRow("Keep history for", selection: $retentionDays,
                          options: [(1, "1 day"), (7, "7 days"), (14, "14 days"),
                                    (30, "30 days"), (90, "90 days"), (180, "180 days"), (365, "1 year")]) {
                    Store.retentionDays = $0
                }
                toggleRow("Vacuum DB after large prunes", isOn: $autoVacuum) { Store.autoVacuum = $0 }
            }

            section("Sampling", "waveform.path.ecg") {
                pickerRow("Sample every", selection: $sampleIntervalSeconds,
                          options: [(5, "5 sec"), (15, "15 sec"), (30, "30 sec"),
                                    (60, "60 sec"), (120, "2 min"), (300, "5 min")]) {
                    Store.sampleIntervalSeconds = $0
                }
                footnote("Burrow runs `mo status --json` at this cadence. 60 s is plenty for charts; tighter intervals give finer detail at the cost of more subprocess churn.")
            }
        }
    }

    // MARK: - Menu bar

    private var menuBarTab: some View {
        Group {
            section("Menu bar", "menubar.rectangle") {
                toggleRow("Show menu bar icon", isOn: $showMenuBarIcon) { on in
                    Store.showMenuBarIcon = on
                    AppDelegate.shared?.applyMenuBarVisibility(on)
                }
                footnote("Applies immediately. When off, Burrow shows a Dock icon instead so it stays reachable — a Dock click reopens the window.")
                HStack {
                    Text(NSLocalizedString("Display", comment: "")).font(Brand.sans(12)).foregroundStyle(Brand.textPrimary)
                    Spacer()
                    Picker("", selection: $displayMode) {
                        Text(NSLocalizedString("Icon", comment: "")).tag(MenuBarDisplayMode.icon)
                        Text(NSLocalizedString("Metrics", comment: "")).tag(MenuBarDisplayMode.metrics)
                    }
                    .labelsHidden().pickerStyle(.segmented).frame(width: 200)
                    .onChange(of: displayMode) { _, v in
                        Store.menuBarDisplayMode = v
                        AppDelegate.shared?.applyMenuBarVisibility(Store.showMenuBarIcon)
                    }
                }
                footnote("Metrics shows live CPU and memory next to the mark, refreshed with the sampler.")
                toggleRow("Show camera & mic in-use indicator", isOn: $cameraMicIndicator) {
                    Store.cameraMicIndicatorEnabled = $0
                }
                footnote("A small \u{201C}in use\u{201D} chip in the popover when the camera or microphone is active — the same system signal as Control Center, so it also lights for Siri, dictation and Continuity Camera. Off by default.")
            }

            section("Keyboard shortcuts", "keyboard") {
                shortcutRow("Open Burrow", action: .openBurrow)
                shortcutRow("Keep Screen On", action: .keepScreenOn)
                shortcutRow("Clean Screen", action: .cleanScreen)
                footnote("System-wide. Click a chip, press a combination with ⌃, ⌥ or ⌘; Esc cancels, × clears.")
            }

            section("Clean Screen", "rectangle.inset.filled") {
                toggleRow("Block keys while wiping", isOn: $inputLock) { on in
                    Store.cleanScreenInputLock = on
                }
                if inputLock, !axTrusted {
                    PermissionRow(
                        title: NSLocalizedString("Accessibility", comment: ""),
                        benefit: NSLocalizedString("Needed to swallow key presses while you wipe. Esc always exits.", comment: ""),
                        granted: axTrusted,
                        onOpenSettings: { CleanScreen.openAccessibilitySettings() },
                        onCheck: { axTrusted = CleanScreen.inputLockPermitted() })
                }
                footnote("Off: Clean Screen still works, keys just aren't blocked. Esc always exits either way.")
            }
        }
    }

    private func shortcutRow(_ label: String, action: HotKeyAction) -> some View {
        HStack {
            Text(NSLocalizedString(label, comment: "")).font(Brand.sans(12)).foregroundStyle(Brand.textPrimary)
            Spacer()
            ShortcutRecorder(action: action)
        }
    }

    // MARK: - Advanced

    private var advancedTab: some View {
        Group {
            section("Ask your AI about your Mac (MCP)", "sparkles") {
                Text("Burrow exposes your Mac's recorded history to coding agents — Claude Code, Cursor, Codex, Cline — over MCP. Add the config below, then ask in plain language. The server starts on demand over stdio; there's no port and no always-on listener.")
                    .font(Brand.sans(12)).foregroundStyle(Brand.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)

                codeBlock(mcpConfigJSON)

                infoRow("Read tools", "snapshot · history · top_processes · process_usage · info · analyze")
                infoRow("Cleanup tools", "clean · optimize · uninstall · purge · installer")

                subLabel("Try asking")
                promptRow("What's my Mac's CPU and memory usage right now?")
                promptRow("What's taking up space in my home folder?")
                promptRow("Preview what a cleanup would free, then clean it up.")
                promptRow("Uninstall Slack and remove its leftovers.")

                Divider().padding(.vertical, 2)
                toggleRow("Let agents run cleanups for real", isOn: $mcpActionsEnabled) {
                    Store.mcpActionsEnabled = $0
                }
                footnote("OFF by default. Agents can always read metrics and run dry-run previews. With this on, an agent can run a real `mo clean` / `optimize` — but ONLY when it also passes an explicit confirm flag, so a deletion is never one stray sentence away. Turn it off and agents are read-only again. Data stays on this Mac.")
                toggleRow("Also allow uninstalls & permanent deletes", isOn: $mcpIrreversibleEnabled) {
                    Store.mcpIrreversibleEnabled = $0
                }
                .disabled(!mcpActionsEnabled)
                footnote("A second key for what the Trash can't undo: real `mo uninstall`, and `permanent:true` deletes. Needs the cleanup switch above too; an uninstall also aborts unless mo matches exactly the requested apps.")
            }

            section("Local HTTP query server", "antenna.radiowaves.left.and.right") {
                toggleRow("Enable HTTP query server", isOn: $queryServerEnabled) { Store.queryServerEnabled = $0 }
                infoRow("Endpoint", "127.0.0.1:\(Store.queryServerPort)")
                footnote("Optional REST surface for dashboards or curl: /health, /info, /snapshot, /metrics over localhost. Separate from the MCP stdio server above; toggle + port changes take effect after a relaunch.")
            }

            section("Explain (AI) — experimental", "sparkles") {
                toggleRow("Enable the Explain lens", isOn: $aiEnabled) { Store.aiEnabled = $0 }
                HStack {
                    Text("Backend").font(Brand.sans(12)).foregroundStyle(Brand.textPrimary)
                    Spacer()
                    Picker("", selection: $aiProvider) {
                        Text("Local · Ollama").tag("ollama")
                        Text("LM Studio / API").tag("openai")
                    }
                    .labelsHidden().pickerStyle(.segmented).frame(width: 230)
                    .onChange(of: aiProvider) { _, v in Store.aiProvider = v }
                }
                if aiProvider == "ollama" {
                    aiField("Ollama model", placeholder: "llama3.2", text: $aiOllamaModel) { Store.aiOllamaModel = $0 }
                    footnote("Runs against a local Ollama model — nothing leaves this Mac. Start it with `ollama run <model>`.")
                } else {
                    aiField("Base URL", placeholder: "http://127.0.0.1:1234/v1", text: $aiOpenAIBaseURL) { Store.aiOpenAIBaseURL = $0 }
                    aiField("Model", placeholder: "local-model", text: $aiOpenAIModel) { Store.aiOpenAIModel = $0 }
                    aiField("API key (optional)", placeholder: "blank for LM Studio", text: $aiOpenAIKey, secure: true) { Store.aiOpenAIKey = $0 }
                    footnote("Any OpenAI-compatible server. For LM Studio: load a model, open Developer ▸ Start Server, and leave the key blank — the default URL is already LM Studio's. A hosted endpoint (e.g. OpenAI) needs a key and sends the metrics summary off-device (never file contents).")
                }
                footnote("Adds an \u{201C}Explain\u{201D} button to Status that reads your latest snapshot and explains it in plain English, optionally suggesting Clean/Purge/Installers.")
            }

            section("Anonymous usage", "chart.bar") {
                toggleRow("Share anonymous usage & crash reports", isOn: $telemetryEnabled) { on in
                    Telemetry.setEnabled(on)
                }
                footnote("Sends anonymous product analytics (PostHog) and crash reports (Sentry): a random install id (not tied to you or your hardware), the app + macOS version, CPU type, and which features you use — with sizes and counts bucketed. Never file names, contents, paths, or your metrics. It helps gauge retention and catch crashes. On by default; turn it off and both stop. Full list in TELEMETRY.md.")
            }

            section("Touch ID for sudo", "touchid") {
                infoRow("Status", touchIDStatus)
                if touchIDAvailable {
                    HStack {
                        Spacer()
                        if touchIDBusy { ProgressView().controlSize(.small).padding(.trailing, 4) }
                        PillButton(title: touchIDEnabled ? "Disable" : "Enable", filled: false) { toggleTouchID() }
                    }
                }
                footnote("Lets `sudo` in a terminal accept your fingerprint instead of a password — including `mo` commands you run yourself. It does NOT change Burrow's own admin prompts: those go through macOS authorization, which asks for your password regardless. Configured via `mo touchid` (pam_tid); turning it on or off needs your password once.")
            }

            section("Mole engine", "shippingbox") {
                infoRow("Version", moleVersion)
                HStack {
                    Spacer()
                    if moleUpdating { ProgressView().controlSize(.small).padding(.trailing, 4) }
                    PillButton(title: moleUpdating ? "Updating…" : "Update Mole", filled: false) { updateMole() }
                }
                footnote("Runs `mo update` to update the Mole CLI engine Burrow drives. This is separate from Burrow's own app updates. If it needs a password or a confirmation it can't show here, run `mo update` in a terminal instead.")
            }
        }
    }

    // MARK: - Mole engine

    private func loadMoleVersion() {
        DispatchQueue.global(qos: .userInitiated).async {
            let v = MoleCLI.version()
            DispatchQueue.main.async { moleVersion = v.map { "v\($0)" } ?? "not found" }
        }
    }

    private func updateMole() {
        guard !moleUpdating else { return }
        moleUpdating = true
        DispatchQueue.global(qos: .userInitiated).async {
            let res = try? MoleCLI.run(args: ["update"], timeout: 600)
            let newVersion = MoleCLI.version()
            DispatchQueue.main.async {
                moleUpdating = false
                if let v = newVersion { moleVersion = "v\(v)" }
                let ok = (res?.exitCode ?? 1) == 0
                let alert = NSAlert()
                alert.messageText = ok ? "Mole is up to date" : "Update didn't complete"
                alert.informativeText = ok
                    ? "Now on \(moleVersion)."
                    : (res?.stderr.isEmpty == false ? String(res!.stderr.prefix(300))
                                                    : "`mo update` exited non-zero. Try running it in a terminal.")
                alert.runModalQuiet()
            }
        }
    }

    // MARK: - Touch ID for sudo

    /// Whether this Mac actually has a Touch ID sensor. `mo touchid status`
    /// only reports configured-vs-not (never hardware presence), so we ask
    /// LocalAuthentication directly — biometryType is .touchID only on Macs
    /// with the sensor (set after a canEvaluatePolicy probe, even if no
    /// finger is enrolled yet).
    private func touchIDHardwarePresent() -> Bool {
        let ctx = LAContext()
        var err: NSError?
        _ = ctx.canEvaluatePolicy(.deviceOwnerAuthenticationWithBiometrics, error: &err)
        return ctx.biometryType == .touchID
    }

    private func loadTouchIDStatus() {
        let available = touchIDHardwarePresent()
        DispatchQueue.global(qos: .userInitiated).async {
            let res = try? MoleCLI.run(args: ["touchid", "status"], timeout: 15)
            // Strip ANSI colour codes Mole wraps the status line in before matching.
            let out = Ansi.strip(res?.stdout ?? "").lowercased()
            let enabled = out.contains("is enabled")
            DispatchQueue.main.async {
                touchIDAvailable = available
                touchIDEnabled = enabled
                touchIDStatus = !available ? "Not available on this Mac"
                    : (out.isEmpty ? "Unknown" : (enabled ? "Enabled" : "Disabled"))
            }
        }
    }

    private func toggleTouchID() {
        guard !touchIDBusy else { return }
        touchIDBusy = true
        let cmd = touchIDEnabled ? "disable" : "enable"
        DispatchQueue.global(qos: .userInitiated).async {
            let code = MoleCLI.runElevated(args: ["touchid", cmd])
            DispatchQueue.main.async {
                touchIDBusy = false
                loadTouchIDStatus()
                if code != 0 {
                    let alert = NSAlert()
                    alert.messageText = NSLocalizedString("Couldn't update Touch ID for sudo", comment: "")
                    alert.informativeText = String(format: NSLocalizedString("`mo touchid %@` didn't complete (the password prompt may have been cancelled). You can also run it in a terminal.", comment: ""), cmd)
                    alert.runModalQuiet()
                }
            }
        }
    }

    // MARK: - Language relaunch

    private func promptRelaunch() {
        let alert = NSAlert()
        alert.messageText = NSLocalizedString("Relaunch to change language?", comment: "")
        alert.informativeText = NSLocalizedString("Burrow needs to relaunch to apply the new language.", comment: "")
        alert.addButton(withTitle: NSLocalizedString("Relaunch Now", comment: ""))
        alert.addButton(withTitle: NSLocalizedString("Later", comment: ""))
        guard alert.runModalQuiet() == .alertFirstButtonReturn else { return }
        let config = NSWorkspace.OpenConfiguration()
        config.createsNewApplicationInstance = true
        NSWorkspace.shared.openApplication(at: Bundle.main.bundleURL, configuration: config) { _, _ in }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { NSApp.terminate(nil) }
    }

    // MARK: - Section + row helpers

    private func section<C: View>(_ title: String, _ glyph: String, @ViewBuilder content: @escaping () -> C) -> some View {
        GlassCard {
            VStack(alignment: .leading, spacing: 10) {
                Eyebrow(text: title, glyph: glyph, color: Brand.textSecondary)
                content()
            }
        }
    }

    private func infoRow(_ label: String, _ value: String) -> some View {
        HStack {
            Text(NSLocalizedString(label, comment: "")).font(Brand.sans(12)).foregroundStyle(Brand.textSecondary)
            Spacer()
            Text(value).font(Brand.mono(11)).foregroundStyle(Brand.textPrimary)
        }
    }

    private func footnote(_ text: String) -> some View {
        Text(NSLocalizedString(text, comment: "")).font(Brand.mono(9)).foregroundStyle(Brand.textTertiary)
            .fixedSize(horizontal: false, vertical: true)
    }

    /// Small all-caps section sub-heading (e.g. "TRY ASKING").
    private func subLabel(_ text: String) -> some View {
        Text(text.uppercased()).font(Brand.mono(9, .bold)).tracking(0.6)
            .foregroundStyle(Brand.textTertiary).padding(.top, 2)
    }

    /// One example prompt with a one-click copy button (the text isn't
    /// selectable, so the button is the only way to grab it).
    private func promptRow(_ text: String) -> some View { PromptRow(text: text) }

    private struct PromptRow: View {
        let text: String
        @State private var copied = false
        @State private var hovering = false

        var body: some View {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Image(systemName: "arrow.right").font(.system(size: 8, weight: .bold))
                    .foregroundStyle(Brand.green).accessibilityHidden(true)
                Text("\u{201C}\(text)\u{201D}").font(Brand.sans(12)).foregroundStyle(Brand.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 6)
                Button {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(text, forType: .string)
                    copied = true
                    DispatchQueue.main.asyncAfter(deadline: .now() + 1.4) { copied = false }
                } label: {
                    Image(systemName: copied ? "checkmark" : "doc.on.doc")
                        .font(.system(size: 10))
                        .foregroundStyle(copied ? Brand.green : (hovering ? Brand.textSecondary : Brand.textTertiary))
                }
                .buttonStyle(.plain)
                .help("Copy prompt")
                .accessibilityLabel("Copy prompt")
            }
            .contentShape(Rectangle())
            .onHover { hovering = $0 }
        }
    }

    /// Monospace config block with a one-click copy button.
    private func codeBlock(_ text: String) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(text).font(Brand.mono(10)).foregroundStyle(Brand.textPrimary)
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
            HStack {
                Spacer()
                Button {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(text, forType: .string)
                    copiedConfig = true
                    // Reset the label so a later copy confirms again.
                    DispatchQueue.main.asyncAfter(deadline: .now() + 1.6) { copiedConfig = false }
                } label: {
                    Label(copiedConfig ? "Copied" : "Copy config",
                          systemImage: copiedConfig ? "checkmark" : "doc.on.doc")
                        .font(Brand.mono(10)).foregroundStyle(Brand.green)
                }
                .buttonStyle(.plain)
            }
        }
        .padding(11)
        .background(RoundedRectangle(cornerRadius: 10).fill(Color.black.opacity(0.25)))
        .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(Brand.hairline, lineWidth: 1))
    }

    private func toggleRow(_ label: String, isOn: Binding<Bool>, onChange: @escaping (Bool) -> Void) -> some View {
        Toggle(isOn: isOn) {
            Text(NSLocalizedString(label, comment: "")).font(Brand.sans(12)).foregroundStyle(Brand.textPrimary)
        }
        .toggleStyle(.switch)
        .tint(Brand.green)
        .onChange(of: isOn.wrappedValue) { _, n in onChange(n) }
    }

    /// Trailing-aligned text-field row that persists on every keystroke, so
    /// closing Settings never loses an in-progress edit.
    // LocalizedStringKey (not String): a String label/placeholder reaches the
    // verbatim Text/TextField overload and skips localization. Example-value
    // placeholders (model ids, the LM Studio URL) simply have no translation
    // and fall back to themselves.
    private func aiField(_ label: LocalizedStringKey, placeholder: LocalizedStringKey,
                         text: Binding<String>, secure: Bool = false,
                         onChange: @escaping (String) -> Void) -> some View {
        HStack {
            Text(label).font(Brand.sans(12)).foregroundStyle(Brand.textPrimary)
            Spacer()
            Group {
                // Secrets (the hosted-API key) render masked so a screen
                // share or screenshot doesn't leak them.
                if secure {
                    SecureField(placeholder, text: text)
                } else {
                    TextField(placeholder, text: text)
                }
            }
            .textFieldStyle(.plain).font(Brand.mono(11))
            .multilineTextAlignment(.trailing).frame(width: 200)
            .onChange(of: text.wrappedValue) { _, v in onChange(v) }
        }
    }

    private func pickerRow(_ label: String, selection: Binding<Int>,
                           options: [(Int, String)], onChange: @escaping (Int) -> Void) -> some View {
        HStack {
            Text(NSLocalizedString(label, comment: "")).font(Brand.sans(12)).foregroundStyle(Brand.textPrimary)
            Spacer()
            Picker("", selection: selection) {
                ForEach(options, id: \.0) { Text(NSLocalizedString($0.1, comment: "")).tag($0.0) }
            }
            .labelsHidden()
            .pickerStyle(.menu)
            .tint(Brand.textSecondary)
            .fixedSize()
            .onChange(of: selection.wrappedValue) { _, n in onChange(n) }
        }
    }

    // MARK: - Status labels

    private func refreshStatusLabels() {
        let support = FileManager.default.urls(for: .applicationSupportDirectory,
                                               in: .userDomainMask).first!
            .appendingPathComponent("Burrow", isDirectory: true)
        var total: Int64 = 0
        if let enumerator = FileManager.default.enumerator(at: support,
                                                           includingPropertiesForKeys: [.fileSizeKey]) {
            for case let url as URL in enumerator {
                if let size = (try? url.resourceValues(forKeys: [.fileSizeKey]))?.fileSize {
                    total += Int64(size)
                }
            }
        }
        self.dbSizeText = Fmt.bytes(total)

        if let last = AppDelegate.shared?.maintenance?.lastRunAt {
            let delta = Int(Date().timeIntervalSince(last))
            self.lastMaintenanceText = String(format: NSLocalizedString("%ds ago · pruned %d rows", comment: ""),
                                              delta, AppDelegate.shared?.maintenance?.lastPruneDeleted ?? 0)
        } else {
            self.lastMaintenanceText = NSLocalizedString("not yet run", comment: "")
        }
    }
}
