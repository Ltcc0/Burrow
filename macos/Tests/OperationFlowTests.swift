//
//  OperationFlowTests.swift
//  BurrowTests
//
//  Boundary tests for the operation flow shell — the run-a-tool lifecycle
//  (FDA gate → optional elevation → spawn → stream → reduce → report →
//  OperationCenter) that CleanView/OptimizeView previously each owned as
//  view plumbing. The process boundary is a scripted fake; no real
//  processes, no TCC, no wall-clock.
//

import XCTest
@testable import Burrow

@MainActor
final class OperationFlowTests: XCTestCase {

    // MARK: Test adapter

    final class FakeProcessPort: ProcessPort, @unchecked Sendable {
        var script: [ProcessEvent]
        /// Keep the stream open after the script (for cancel tests).
        var holdOpen = false
        private(set) var specs: [ProcessSpec] = []
        private(set) var terminated = false

        init(script: [ProcessEvent]) { self.script = script }

        func events(_ spec: ProcessSpec) -> AsyncStream<ProcessEvent> {
            specs.append(spec)
            let s = script, hold = holdOpen
            return AsyncStream { cont in
                cont.onTermination = { @Sendable _ in self.terminated = true }
                for e in s { cont.yield(e) }
                if !hold { cont.finish() }
            }
        }
    }

    /// Canned dry-run output in mo's report shape (parseTaskReport-compatible).
    static let cannedClean: [ProcessEvent] = [
        .line("➤ Developer tools"),
        .line("  → npm cache, 191.8MB"),
        .line("Potential space: 383.8MB | Items: 372 | Categories: 20"),
        .exited(0),
    ]

    typealias CleanReport = (groups: [TaskGroup], summary: TaskSummary?)

    static func cleanOp(gate: ToolOperation<CleanReport>.Gate = .none,
                        elevated: Bool = false) -> ToolOperation<CleanReport> {
        ToolOperation(label: "Scanning caches",
                      arguments: ["clean", "--dry-run"],
                      gate: gate,
                      elevated: elevated,
                      reduce: { parseTaskReport($0) },
                      hudLine: { TaskReportText.line($0) })
    }

    private func makeFlow(_ port: FakeProcessPort, fda: @escaping () -> Bool = { true },
                          center: OperationCenter? = nil) -> OperationFlow<CleanReport> {
        OperationFlow(process: port, hasFullDiskAccess: fda,
                      resolveMo: { _ in "/usr/local/bin/mo" }, center: center ?? OperationCenter())
    }

    private func settle<R>(_ flow: OperationFlow<R>) async {
        for _ in 0..<1000 {
            if case .finished = flow.state { return }
            await Task.yield()
        }
        XCTFail("flow never finished")
    }

    // MARK: Tests

    func testGate_blocksWithoutFDAThenGrantRuns() async throws {
        let port = FakeProcessPort(script: Self.cannedClean)
        var fda = false
        let flow = makeFlow(port, fda: { fda })

        flow.start(Self.cleanOp(gate: .fullDiskAccess(adminBypass: true)))
        guard case .gated(let pending) = flow.state else { return XCTFail("expected FDA gate") }
        XCTAssertTrue(port.specs.isEmpty, "nothing spawns while gated")

        fda = true
        flow.start(pending)                       // "I've granted it" = start again
        await settle(flow)

        XCTAssertEqual(port.specs.last?.elevated, false)
        XCTAssertEqual(flow.report?.summary?.space, "383.8MB")
        XCTAssertEqual(flow.report?.groups.count, 1)
        guard case .finished(.done(exit: 0)) = flow.state else { return XCTFail("expected done(0)") }
    }

    func testGate_adminBypassRunsElevatedWithoutGate() async throws {
        let port = FakeProcessPort(script: Self.cannedClean)
        let flow = makeFlow(port, fda: { false })

        flow.start(Self.cleanOp(gate: .fullDiskAccess(adminBypass: true)))
        guard case .gated(let pending) = flow.state else { return XCTFail("expected gate") }

        flow.start(pending.elevated())            // "Scan with admin" — root dodges TCC
        await settle(flow)
        XCTAssertEqual(port.specs.last?.elevated, true)
        guard case .finished(.done) = flow.state else { return XCTFail("expected done") }
    }

    func testCancel_terminatesChildAndMarksCancelled() async throws {
        let port = FakeProcessPort(script: [.line("➤ Working")])
        port.holdOpen = true                      // process never exits on its own
        let flow = makeFlow(port)

        flow.start(Self.cleanOp())
        XCTAssertTrue(flow.canCancel)
        flow.cancel()

        guard case .finished(.cancelled) = flow.state else { return XCTFail("expected cancelled") }
        for _ in 0..<1000 { if port.terminated { break }; await Task.yield() }
        XCTAssertTrue(port.terminated, "cancelling the flow terminates the child")
    }

    func testElevatedRunCannotCancel() {
        let port = FakeProcessPort(script: [.line("x")])
        port.holdOpen = true
        let flow = makeFlow(port)
        flow.start(Self.cleanOp(elevated: true))
        XCTAssertFalse(flow.canCancel,
                       "SIGTERMing the osascript messenger would orphan the root child")
    }

    func testStdinTimeoutAndPathExecutableReachSpec() async throws {
        let port = FakeProcessPort(script: [.exited(0)])
        let flow = OperationFlow<String>(process: port, hasFullDiskAccess: { true },
                                         resolveMo: { _ in nil }, center: OperationCenter())
        // Uninstall-style blocking run: canned confirmations + long timeout.
        let answers = String(repeating: "y\n", count: 16)
        flow.start(ToolOperation(label: nil,
                                 executable: .path("/opt/homebrew/bin/brew"),
                                 arguments: ["upgrade", "wget"],
                                 stdin: answers,
                                 timeout: 300,
                                 reduce: { $0.joined(separator: "\n") }))
        await settle(flow)
        let spec = try XCTUnwrap(port.specs.first)
        XCTAssertEqual(spec.executable, "/opt/homebrew/bin/brew")
        XCTAssertEqual(spec.stdin, answers)
        XCTAssertEqual(spec.timeout, 300)
    }

    func testOperationCenter_beginDetailEnd() async throws {
        let port = FakeProcessPort(script: Self.cannedClean)
        let center = OperationCenter()
        let flow = makeFlow(port, center: center)

        flow.start(Self.cleanOp())
        await settle(flow)

        let op = try XCTUnwrap(center.ops.first)
        XCTAssertEqual(op.label, "Scanning caches")
        XCTAssertEqual(op.phase, .done)
        XCTAssertFalse(op.detail.isEmpty, "HUD detail fed from the stream")
        XCTAssertFalse(op.notifiesOnEnd, "preview-style ops never notify")
    }

    // The real-clean shape: notifyOnEnd rides into the OperationCenter op
    // and the parsed summary replaces the last streamed line as the final
    // detail — that's the body a completion notification carries.
    func testNotifyOnEnd_andFinalDetail_reachOperationCenter() async throws {
        let port = FakeProcessPort(script: Self.cannedClean)
        let center = OperationCenter()
        let flow = OperationFlow<TaskRunReport>(process: port, hasFullDiskAccess: { true },
                                                resolveMo: { _ in "/usr/local/bin/mo" }, center: center)

        flow.start(.moleStream(["clean"], label: "Cleaning caches", notifyOnEnd: true))
        await settle(flow)

        let op = try XCTUnwrap(center.ops.first)
        XCTAssertTrue(op.notifiesOnEnd)
        XCTAssertEqual(op.phase, .done)
        XCTAssertEqual(op.detail, "Cleaned 383.8MB · 372 items",
                       "final detail is the parsed summary, not the last raw line")
    }

    func testAuthCancelledFromThePort_failsTheFlow() async throws {
        // Auth-cancel is classified by the RUNNER now (issue #48) — the flow
        // just renders the failure.
        let port = FakeProcessPort(script: [.authCancelled])
        let flow = makeFlow(port)
        flow.start(Self.cleanOp(elevated: true))
        await settle(flow)
        guard case .finished(.failed) = flow.state else { return XCTFail("expected failed") }
    }

    func testMissingExecutableFailsBeforeSpawn() {
        let port = FakeProcessPort(script: [])
        let flow = OperationFlow<CleanReport>(process: port, hasFullDiskAccess: { true },
                                              resolveMo: { _ in nil }, center: OperationCenter())
        flow.start(Self.cleanOp())
        guard case .finished(.failed) = flow.state else { return XCTFail("expected failed") }
        XCTAssertTrue(port.specs.isEmpty)
    }
}

// MARK: - Production adapter against real (tiny) processes

final class SystemProcessPortTests: XCTestCase {
    private enum Terminal: Sendable, Equatable {
        case exited(Int32)
        case authCancelled
    }

    private struct Collected: Sendable, Equatable {
        var lines: [String] = []
        var terminal: Terminal?
    }

    private final class LockedBox<Value>: @unchecked Sendable {
        private let lock = NSLock()
        private var storage: Value

        init(_ value: Value) { storage = value }

        func withValue<T>(_ body: (inout Value) -> T) -> T {
            lock.lock(); defer { lock.unlock() }
            return body(&storage)
        }

        var value: Value {
            lock.lock(); defer { lock.unlock() }
            return storage
        }
    }

    private static func collect(_ spec: ProcessSpec,
                                port: SystemProcessPort = SystemProcessPort()) async -> Collected {
        var result = Collected()
        for await event in port.events(spec) {
            switch event {
            case .line(let line): result.lines.append(line)
            case .exited(let code): result.terminal = .exited(code)
            case .authCancelled: result.terminal = .authCancelled
            }
        }
        return result
    }

    private func run(_ spec: ProcessSpec) async -> (lines: [String], exit: Int32?) {
        var lines: [String] = []
        var exit: Int32?
        for await e in SystemProcessPort().events(spec) {
            switch e {
            case .line(let l): lines.append(l)
            case .exited(let c): exit = c
            case .authCancelled: XCTFail("un-elevated specs never classify as auth-cancel")
            }
        }
        return (lines, exit)
    }

    private static func elevatedSpec(arguments: [String] = ["clean"],
                                     timeout: TimeInterval? = nil) -> ProcessSpec {
        ProcessSpec(executable: "/usr/local/bin/mo", arguments: arguments,
                    stdin: nil, elevated: true, timeout: timeout)
    }

    func testStreamsLinesAndExitCode() async {
        let r = await run(ProcessSpec(executable: "/bin/sh",
                                      arguments: ["-c", "printf 'a\\nb\\n'; exit 3"],
                                      stdin: nil, elevated: false, timeout: nil))
        XCTAssertEqual(r.lines, ["a", "b"])
        XCTAssertEqual(r.exit, 3)
    }

    func testStdinIsFedAndClosed() async {
        let r = await run(ProcessSpec(executable: "/bin/cat", arguments: [],
                                      stdin: "hello\n", elevated: false, timeout: nil))
        XCTAssertEqual(r.lines, ["hello"])
        XCTAssertEqual(r.exit, 0)
    }

    func testTimeoutKillsTheChild() async {
        let started = Date()
        let r = await run(ProcessSpec(executable: "/bin/sleep", arguments: ["10"],
                                      stdin: nil, elevated: false, timeout: 0.3))
        XCTAssertLessThan(Date().timeIntervalSince(started), 5,
                          "the kill timer must fire, not the 10 s sleep")
        XCTAssertNotEqual(r.exit, 0)
    }

    func testSpawnFailureYieldsExit127() async {
        let r = await run(ProcessSpec(executable: "/nonexistent/binary", arguments: [],
                                      stdin: nil, elevated: false, timeout: nil))
        XCTAssertEqual(r.exit, 127)
    }

    // MARK: Pathname-free elevated output transport (BUR-89)

    func testElevatedTransport_hostileLegacySymlinkIsNeverOpened() async throws {
        let fm = FileManager.default
        let nonce = UUID().uuidString.filter(\.isLetter)
        let arguments = ["BURHostileSymlink\(nonce)"]
        let safe = arguments.map { $0.filter(\.isLetter) }.joined(separator: "-")
        let legacyURL = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("burrow-op-\(safe).log")
        let fixtureDir = fm.temporaryDirectory
            .appendingPathComponent("burrow-bur89-\(UUID().uuidString)")
        let sentinelURL = fixtureDir.appendingPathComponent("sentinel")
        try fm.createDirectory(at: fixtureDir, withIntermediateDirectories: true)
        try Data("DO NOT TOUCH".utf8).write(to: sentinelURL)
        try? fm.removeItem(at: legacyURL)
        try fm.createSymbolicLink(at: legacyURL, withDestinationURL: sentinelURL)
        defer {
            try? fm.removeItem(at: legacyURL)
            try? fm.removeItem(at: fixtureDir)
        }

        let script = LockedBox<String?>(nil)
        let cleanupCount = LockedBox(0)
        let port = SystemProcessPort(elevatedInvocation: { capture in
            script.withValue { $0 = capture.source }
            return SystemProcessPort.Invocation(
                executableURL: URL(fileURLWithPath: "/usr/bin/printf"),
                arguments: ["%s", "safe output\n\(capture.exitMarker)0\n"]
            )
        }, didCleanup: {
            cleanupCount.withValue { $0 += 1 }
        })

        let result = await Self.collect(Self.elevatedSpec(arguments: arguments), port: port)

        XCTAssertEqual(result.lines, ["safe output"])
        XCTAssertEqual(result.terminal, .exited(0))
        XCTAssertEqual(String(decoding: try Data(contentsOf: sentinelURL), as: UTF8.self),
                       "DO NOT TOUCH")
        XCTAssertEqual(try fm.destinationOfSymbolicLink(atPath: legacyURL.path), sentinelURL.path)
        XCTAssertFalse(try XCTUnwrap(script.value).contains(legacyURL.path))
        XCTAssertFalse(try XCTUnwrap(script.value).contains("burrow-op-"))
        XCTAssertEqual(cleanupCount.value, 1)
    }

    func testElevatedTransport_concurrentIdenticalOperationsHaveIndependentState() async {
        let markers = LockedBox<[String]>([])
        let cleanupCount = LockedBox(0)
        let port = SystemProcessPort(elevatedInvocation: { capture in
            markers.withValue { $0.append(capture.exitMarker) }
            return SystemProcessPort.Invocation(
                executableURL: URL(fileURLWithPath: "/usr/bin/printf"),
                arguments: ["%s", "run-\(capture.exitMarker)\n\(capture.exitMarker)0\n"]
            )
        }, didCleanup: {
            cleanupCount.withValue { $0 += 1 }
        })

        let results = await withTaskGroup(of: Collected.self, returning: [Collected].self) { group in
            for _ in 0..<8 {
                group.addTask {
                    await Self.collect(Self.elevatedSpec(arguments: ["clean", "--dry-run"]),
                                       port: port)
                }
            }
            var all: [Collected] = []
            for await result in group { all.append(result) }
            return all
        }

        XCTAssertEqual(results.count, 8)
        XCTAssertTrue(results.allSatisfy { $0.terminal == .exited(0) })
        XCTAssertEqual(Set(results.compactMap(\.lines.first)).count, 8)
        XCTAssertEqual(Set(markers.value).count, 8)
        XCTAssertEqual(cleanupCount.value, 8)
    }

    func testElevatedTransport_capturesMergedOutputBeforeFailedExit() async throws {
        let source = LockedBox<String?>(nil)
        let port = SystemProcessPort(elevatedInvocation: { capture in
            source.withValue { $0 = capture.source }
            let envelope = "stdout line\nstderr line\nfinal partial\n\(capture.exitMarker)7\n"
            return SystemProcessPort.Invocation(
                executableURL: URL(fileURLWithPath: "/usr/bin/printf"),
                arguments: ["%s", envelope]
            )
        })

        let result = await Self.collect(Self.elevatedSpec(), port: port)

        XCTAssertEqual(result.lines, ["stdout line", "stderr line", "final partial"])
        XCTAssertEqual(result.terminal, .exited(7),
                       "all complete lines must precede the ordinary failed exit")
        XCTAssertTrue(try XCTUnwrap(source.value).contains("2>&1"),
                      "the root command's stderr is merged without a log pathname")
    }

    func testElevatedTransport_cleansSuccessFailureTimeoutSpawnFailureAndCancellation() async {
        let cleaned = LockedBox<[String: Int]>([:])
        func record(_ name: String) -> @Sendable () -> Void {
            { cleaned.withValue { $0[name, default: 0] += 1 } }
        }
        func envelopePort(name: String, status: Int32) -> SystemProcessPort {
            SystemProcessPort(elevatedInvocation: { capture in
                SystemProcessPort.Invocation(
                    executableURL: URL(fileURLWithPath: "/usr/bin/printf"),
                    arguments: ["%s", "\(name)\n\(capture.exitMarker)\(status)\n"]
                )
            }, didCleanup: record(name))
        }

        _ = await Self.collect(Self.elevatedSpec(), port: envelopePort(name: "success", status: 0))
        _ = await Self.collect(Self.elevatedSpec(), port: envelopePort(name: "failure", status: 9))

        let timeoutPort = SystemProcessPort(elevatedInvocation: { _ in
            SystemProcessPort.Invocation(executableURL: URL(fileURLWithPath: "/bin/sleep"),
                                         arguments: ["5"])
        }, didCleanup: record("timeout"))
        _ = await Self.collect(Self.elevatedSpec(timeout: 0.05), port: timeoutPort)

        let spawnFailurePort = SystemProcessPort(elevatedInvocation: { _ in
            SystemProcessPort.Invocation(executableURL: URL(fileURLWithPath: "/not/a/binary"),
                                         arguments: [])
        }, didCleanup: record("spawnFailure"))
        _ = await Self.collect(Self.elevatedSpec(), port: spawnFailurePort)

        let cancelPort = SystemProcessPort(elevatedInvocation: { _ in
            SystemProcessPort.Invocation(executableURL: URL(fileURLWithPath: "/bin/sleep"),
                                         arguments: ["5"])
        }, didCleanup: record("cancellation"))
        let cancelled = Task { await Self.collect(Self.elevatedSpec(), port: cancelPort) }
        try? await Task<Never, Never>.sleep(nanoseconds: 50_000_000)
        cancelled.cancel()
        _ = await cancelled.value
        for _ in 0..<100 where cleaned.value["cancellation"] == nil {
            try? await Task<Never, Never>.sleep(nanoseconds: 10_000_000)
        }

        for name in ["success", "failure", "timeout", "spawnFailure", "cancellation"] {
            XCTAssertEqual(cleaned.value[name], 1, "\(name) must clean exactly once")
        }
    }

    func testElevatedTransport_authorizationCancellationAndRealFailureStayDistinct() async {
        let authCancelPort = SystemProcessPort(elevatedInvocation: { _ in
            SystemProcessPort.Invocation(executableURL: URL(fileURLWithPath: "/usr/bin/false"),
                                         arguments: [])
        })
        let cancelled = await Self.collect(Self.elevatedSpec(), port: authCancelPort)
        XCTAssertEqual(cancelled.lines, [])
        XCTAssertEqual(cancelled.terminal, .authCancelled)

        let failurePort = SystemProcessPort(elevatedInvocation: { capture in
            SystemProcessPort.Invocation(
                executableURL: URL(fileURLWithPath: "/usr/bin/printf"),
                arguments: ["%s", "command failed\n\(capture.exitMarker)12\n"]
            )
        })
        let failed = await Self.collect(Self.elevatedSpec(), port: failurePort)
        XCTAssertEqual(failed.lines, ["command failed"])
        XCTAssertEqual(failed.terminal, .exited(12))
    }

    // MARK: Auth-cancel classification — an engine rule, not view folklore

    /// "osascript exits nonzero having produced nothing" used to be an
    /// OperationFlow heuristic; the runner owns it now, as a pure rule.
    func testFinalEvent_classifiesAuthCancel() {
        // The previously-untestable path: elevated, failed, silent.
        guard case .authCancelled = SystemProcessPort.finalEvent(
            exitCode: 1, elevated: true, sawOutput: false) else {
            return XCTFail("elevated + nonzero + no output = dismissed auth prompt")
        }
        // Output means the run really happened — a real failure, not cancel.
        guard case .exited(1) = SystemProcessPort.finalEvent(
            exitCode: 1, elevated: true, sawOutput: true) else {
            return XCTFail("an elevated run that produced output failed on its own terms")
        }
        // Un-elevated runs have no auth prompt to cancel.
        guard case .exited(2) = SystemProcessPort.finalEvent(
            exitCode: 2, elevated: false, sawOutput: false) else {
            return XCTFail("no elevation, no auth-cancel")
        }
        // Success is success even when silent.
        guard case .exited(0) = SystemProcessPort.finalEvent(
            exitCode: 0, elevated: true, sawOutput: false) else {
            return XCTFail("exit 0 is never a cancel")
        }
    }
}
