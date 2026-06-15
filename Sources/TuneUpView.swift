//
//  TuneUpView.swift
//  Burrow
//
//  The one-tap Tune-Up sheet (reached from Home): a pre-run plan with a
//  per-step opt-out, then section cards that track each step as it runs, and a
//  done summary. It only sequences existing tool runs (Clean + Optimize) — no
//  new engine, conservative by default.
//

import SwiftUI

struct TuneUpView: View {
    @StateObject private var runner = TuneUpRunner()
    var onClose: () -> Void
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        VStack(spacing: 0) {
            header
            Rectangle().fill(Brand.hairline).frame(height: 1)
            ScrollView {
                VStack(spacing: 12) {
                    if !runner.started { intro }
                    ForEach(runner.steps) { step in stepCard(step) }
                }
                .padding(18)
            }
            footer
        }
        .frame(width: 460, height: 540)
        .background(Brand.nearBlack)
        .environment(\.colorScheme, .dark)
    }

    private var header: some View {
        HStack(spacing: 10) {
            Image(systemName: "wand.and.stars").font(.system(size: 15)).foregroundStyle(Tool.optimize.accent)
            Text(runner.finished ? NSLocalizedString("Your Mac is in good shape", comment: "")
                                 : NSLocalizedString("Tune-Up", comment: ""))
                .font(Brand.serif(18, .medium)).foregroundStyle(Brand.textPrimary)
            Spacer()
            Button { onClose() } label: {
                Image(systemName: "xmark").font(.system(size: 12, weight: .semibold)).foregroundStyle(Brand.textSecondary)
            }.buttonStyle(.plain).accessibilityLabel(NSLocalizedString("Close", comment: ""))
        }
        .padding(.horizontal, 18).padding(.vertical, 14)
    }

    private var intro: some View {
        Text("Runs the safe maintenance steps below back to back. Each runs the engine's own real job — review the list, switch off anything you'd rather skip, then start. The elevated steps will each ask for your password once.")
            .font(Brand.sans(12)).foregroundStyle(Brand.textSecondary)
            .fixedSize(horizontal: false, vertical: true)
            .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func stepCard(_ step: TuneUpRunner.Step) -> some View {
        HStack(spacing: 12) {
            Image(systemName: step.glyph).font(.system(size: 18)).foregroundStyle(step.accent).frame(width: 26)
            VStack(alignment: .leading, spacing: 2) {
                Text(step.title).font(Brand.sans(13, .semibold)).foregroundStyle(Brand.textPrimary)
                if !step.summary.isEmpty {
                    Text(step.summary).font(Brand.mono(10)).foregroundStyle(Brand.textSecondary).lineLimit(1)
                } else if step.status == .running {
                    Text("Working…").font(Brand.mono(10)).foregroundStyle(Brand.textSecondary)
                }
            }
            Spacer(minLength: 8)
            trailing(step)
        }
        .padding(14)
        .background(RoundedRectangle(cornerRadius: 14).fill(Brand.cardFill))
        .overlay(RoundedRectangle(cornerRadius: 14)
            .strokeBorder(step.status == .running ? step.accent.opacity(0.5) : Brand.hairline, lineWidth: 1))
        .opacity(step.status == .skipped ? 0.5 : 1)
    }

    @ViewBuilder
    private func trailing(_ step: TuneUpRunner.Step) -> some View {
        if !runner.started {
            Toggle("", isOn: Binding(get: { step.included },
                                     set: { _ in runner.toggle(step.id) }))
                .labelsHidden().toggleStyle(.switch).controlSize(.mini).tint(step.accent)
                .accessibilityLabel(step.title)
        } else {
            switch step.status {
            case .running: ProgressView().controlSize(.small).tint(step.accent)
            case .done:    Image(systemName: "checkmark.circle.fill").foregroundStyle(Brand.green)
            case .failed:  Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(Brand.orange)
            case .skipped: Text("skipped").font(Brand.mono(9)).foregroundStyle(Brand.textTertiary)
            case .pending: Image(systemName: "circle").foregroundStyle(Brand.textTertiary)
            }
        }
    }

    @ViewBuilder
    private var footer: some View {
        Rectangle().fill(Brand.hairline).frame(height: 1)
        HStack {
            Spacer()
            if !runner.started {
                PillButton(title: runner.includedCount == 0 ? "Nothing selected" : "Start Tune-Up") {
                    guard runner.includedCount > 0 else { return }
                    Task { await runner.run() }
                }
            } else if runner.finished {
                PillButton(title: "Done") { onClose() }
            } else {
                Text("Running…").font(Brand.mono(11)).foregroundStyle(Brand.textSecondary)
            }
        }
        .padding(.horizontal, 18).padding(.vertical, 14)
    }
}
