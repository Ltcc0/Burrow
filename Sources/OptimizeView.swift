//
//  OptimizeView.swift
//  Burrow
//
//  The Optimize tab. "Optimize" runs the safe maintenance tasks
//  (elevated through a single auth prompt); "Preview" is a no-auth
//  `--dry-run`. The live view is the TaskTicker (design 2.5): current
//  task + a sliding panel of completions; the detailed TaskReport stays
//  as the receipt when the run finishes. If the stream doesn't parse
//  into ticker completions, the raw report view streams instead — no
//  blank screens on format drift.
//

import SwiftUI

/// Groups + summary for the receipt, ticker state for the live view —
/// one reduce pass over the same lines.
typealias OptimizeReport = (groups: [TaskGroup], summary: TaskSummary?, ticker: TaskTickerState)

struct OptimizeView: View {
    @StateObject private var flow = OperationFlow<OptimizeReport>()
    @State private var preview = false

    var body: some View {
        switch flow.state {
        case .idle:
            ToolHero(tool: .optimize, title: "Optimize", subtitle: Tool.optimize.tagline) {
                PillButton(title: "Optimize") { runOptimize() }
                PillButton(title: "Preview", filled: false) { runPreview() }
            }
        case .gated(let pending):
            FullDiskAccessRequired(
                accent: Tool.optimize.accent,
                onRecheck: {
                    flow.start(pending)
                    if case .gated = flow.state { return false }
                    return true
                },
                onRunAnyway: { flow.start(pending.elevated()) },
                onCancel: { flow.reset() })
        case .running:
            VStack(spacing: 0) {
                statusBar
                Spacer()
                if let ticker = flow.report?.ticker, ticker.count > 0 {
                    TaskTickerView(state: ticker, accent: Tool.optimize.accent,
                                   headline: preview ? NSLocalizedString("Previewing…", comment: "")
                                                     : NSLocalizedString("Refreshing…", comment: ""))
                } else if let groups = flow.report?.groups, !groups.isEmpty {
                    // Ticker found nothing it recognizes — stream the raw
                    // report rather than showing an empty panel.
                    TaskReportView(groups: groups, accent: Tool.optimize.accent)
                } else {
                    TaskTickerView(state: TaskTickerState(), accent: Tool.optimize.accent,
                                   headline: preview ? NSLocalizedString("Previewing…", comment: "")
                                                     : NSLocalizedString("Refreshing…", comment: ""))
                }
                Spacer(); Spacer()
            }
        case .finished:
            VStack(spacing: 0) {
                statusBar
                Rectangle().fill(Brand.hairline).frame(height: 1)
                if case .finished(.done) = flow.state, !preview {
                    DoneBanner(accent: Tool.optimize.accent, title: "Maintenance complete",
                               detail: String(format: NSLocalizedString("%d areas refreshed", comment: ""),
                                              flow.report?.groups.count ?? 0))
                }
                TaskReportView(groups: flow.report?.groups ?? [], accent: Tool.optimize.accent)
                ViewLogDisclosure(log: flow.rawLog)
            }
        }
    }

    private var statusBar: some View {
        HStack(spacing: 10) {
            if case .running = flow.state {
                ProgressView().controlSize(.small).tint(Tool.optimize.accent)
            }
            Text(statusText).font(Brand.mono(12)).foregroundStyle(Brand.textSecondary)
            Spacer()
            if flow.canCancel {
                Button { flow.cancel() } label: {
                    Label("Stop", systemImage: "stop.fill")
                        .font(Brand.mono(11)).foregroundStyle(Brand.red)
                }.buttonStyle(.plain)
            }
            if case .finished = flow.state {
                Button { flow.reset() } label: {
                    Label("Back", systemImage: "chevron.left")
                        .font(Brand.mono(11)).foregroundStyle(Brand.textSecondary)
                }.buttonStyle(.plain)
            }
        }
        .padding(.horizontal, 18).padding(.top, 4).padding(.bottom, 12)
    }

    private var statusText: String {
        switch flow.state {
        case .running:
            return preview ? NSLocalizedString("Previewing maintenance…", comment: "")
                           : NSLocalizedString("Running maintenance…", comment: "")
        case .finished(.cancelled):
            return NSLocalizedString("Stopped.", comment: "")
        case .finished(.done):
            return preview ? NSLocalizedString("Preview complete.", comment: "")
                           : NSLocalizedString("Maintenance complete.", comment: "")
        case .finished(.failed(let m)):
            return String(format: NSLocalizedString("Failed: %@", comment: ""), m)
        case .idle, .gated:
            return ""
        }
    }

    private func operation(_ args: [String], gate: ToolOperation<OptimizeReport>.Gate,
                           elevated: Bool, label: String,
                           notify: Bool = false) -> ToolOperation<OptimizeReport> {
        // Final detail = the figure the done-banner shows; previews keep
        // the last streamed line instead ("refreshed" would be a lie for
        // a dry run).
        var finalDetail: (@Sendable (OptimizeReport) -> String)?
        if notify {
            finalDetail = { report in
                String(format: NSLocalizedString("%d areas refreshed", comment: ""), report.groups.count)
            }
        }
        return ToolOperation(label: label, arguments: args, gate: gate, elevated: elevated,
                             reduce: { lines in
                                 let (groups, summary) = parseTaskReport(lines)
                                 return (groups, summary, TaskTicker.reduce(lines))
                             },
                             hudLine: { TaskReportText.line($0) },
                             notifyOnEnd: notify,
                             finalDetail: finalDetail)
    }

    /// Optimize already runs elevated (root) → no flood, no gate.
    private func runOptimize() {
        preview = false
        flow.start(operation(["optimize"], gate: .none, elevated: true,
                             label: NSLocalizedString("Optimizing", comment: ""),
                             notify: true))
    }

    private func runPreview() {
        preview = true
        flow.start(operation(["optimize", "--dry-run"],
                             gate: .fullDiskAccess(adminBypass: true), elevated: false,
                             label: NSLocalizedString("Optimize preview", comment: "")))
    }
}
