//
//  TuneUpRunner.swift
//  Burrow
//
//  One-tap "Tune-Up": a sequencer that runs the safe subset of Burrow's
//  tools back to back, reusing each tool's existing OperationFlow rather than
//  any new process path. Conservative by default — only Clean + Optimize, both
//  shown in a pre-run plan with a per-step opt-out before anything runs. Each
//  step is the engine's own real run (mo's whitelist/safety still apply); the
//  elevated steps each take their own auth prompt (pooling them would need the
//  backlogged privileged helper).
//

import SwiftUI

@MainActor
final class TuneUpRunner: ObservableObject {
    enum Status: Equatable { case pending, running, done, failed, skipped }

    struct Step: Identifiable {
        let id: String
        let title: String
        let glyph: String
        let accent: Color
        var included: Bool
        var status: Status = .pending
        var summary: String = ""
    }

    @Published var steps: [Step]
    @Published var started = false
    @Published var finished = false

    init() {
        steps = [
            Step(id: "clean", title: NSLocalizedString("Clean caches", comment: ""),
                 glyph: Tool.clean.glyph, accent: Tool.clean.accent, included: true),
            Step(id: "optimize", title: NSLocalizedString("Run maintenance", comment: ""),
                 glyph: Tool.optimize.glyph, accent: Tool.optimize.accent, included: true),
        ]
    }

    var includedCount: Int { steps.filter(\.included).count }

    func toggle(_ id: String) {
        guard !started, let i = steps.firstIndex(where: { $0.id == id }) else { return }
        steps[i].included.toggle()
    }

    private func op(for id: String) -> ToolOperation<TaskRunReport> {
        switch id {
        case "clean":
            return .moleStream(["clean"], elevated: true,
                               label: NSLocalizedString("Cleaning caches", comment: ""))
        default:
            return .moleStream(["optimize"], elevated: true,
                               label: NSLocalizedString("Optimizing", comment: ""))
        }
    }

    /// Run the included steps one at a time, awaiting each flow's finish
    /// before starting the next. Status drives the section cards; the live
    /// per-step progress shows in the OperationCenter HUD (each op is labelled).
    func run() async {
        started = true
        finished = false
        for index in steps.indices {
            guard steps[index].included else { steps[index].status = .skipped; continue }
            steps[index].status = .running
            let flow = OperationFlow<TaskRunReport>()
            flow.start(op(for: steps[index].id))
            for await state in flow.$state.values {
                if case .finished(let outcome) = state {
                    switch outcome {
                    case .done:
                        steps[index].status = .done
                        steps[index].summary = flow.report?.summary?.completionLine
                            ?? NSLocalizedString("Done", comment: "")
                    case .cancelled:
                        steps[index].status = .failed
                        steps[index].summary = NSLocalizedString("Stopped", comment: "")
                    case .failed(let m):
                        steps[index].status = .failed
                        steps[index].summary = m
                    }
                    break
                }
            }
        }
        finished = true
    }
}
