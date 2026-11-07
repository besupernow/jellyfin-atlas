// Atlas Server: a small menu bar app that runs the bundled Jellyfin server.
// Built by .github/workflows/atlas-build.yml with swiftc (no Xcode project).
//
// Layout inside the app:
//   Contents/MacOS/AtlasServer          this app
//   Contents/Resources/server/          self-contained server, jellyfin-web inside
//                                       (not "jellyfin/": codesign treats a folder holding a
//                                       same-named executable as a bundle and fails on the .dlls)
//   Contents/Resources/ffmpeg/          jellyfin-ffmpeg (ffmpeg, ffprobe)
//
// Data stays where the stock Jellyfin app keeps it (~/Library/Application Support/jellyfin),
// because that's the server's own default on macOS.

import AppKit
import ServiceManagement

private let serverURL = URL(string: "http://localhost:8096")!
private let keepAwakeKey = "KeepAwake"

// Atlas ABR defaults. Anything already set in the environment wins.
private let atlasDefaults: [String: String] = [
    "ATLAS_ABR_ENABLED": "true",
    "ATLAS_ABR_GRID_SECONDS": "2",
    "ATLAS_ABR_IDLE_LEVEL_SECONDS": "20",
]

final class AppDelegate: NSObject, NSApplicationDelegate {
    private var statusItem: NSStatusItem!
    private let statusLine = NSMenuItem(title: "Starting…", action: nil, keyEquivalent: "")
    private var loginItem: NSMenuItem!
    private var awakeItem: NSMenuItem!

    private var server: Process?
    private var quitting = false
    private var restartsInARow = 0
    private var lastStart = Date.distantPast
    private var awakeActivity: NSObjectProtocol?

    func applicationDidFinishLaunching(_ notification: Notification) {
        UserDefaults.standard.register(defaults: [keepAwakeKey: true])

        if alreadyRunning() {
            NSWorkspace.shared.open(serverURL)
            NSApp.terminate(nil)
            return
        }

        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        if let button = statusItem.button {
            let image = NSImage(systemSymbolName: "play.tv", accessibilityDescription: "Atlas Server")
            image?.isTemplate = true
            button.image = image
        }

        let menu = NSMenu()
        statusLine.isEnabled = false
        menu.addItem(statusLine)
        menu.addItem(.separator())
        menu.addItem(item("Open Atlas Server", #selector(openServer), "o"))
        menu.addItem(item("Show Logs", #selector(openLogs), "l"))
        menu.addItem(item("Restart Server", #selector(restartServer), "r"))
        menu.addItem(.separator())
        loginItem = item("Start at Login", #selector(toggleLogin), "")
        menu.addItem(loginItem)
        awakeItem = item("Keep Mac Awake", #selector(toggleAwake), "")
        menu.addItem(awakeItem)
        menu.addItem(.separator())
        menu.addItem(item("Quit Atlas Server", #selector(quit), "q"))
        statusItem.menu = menu

        refreshToggles()
        startServer()
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        quitting = true
        stopServer()
        return .terminateNow
    }

    // MARK: Server

    private var resources: URL { Bundle.main.resourceURL! }

    private func startServer() {
        let binary = resources.appendingPathComponent("server/jellyfin")
        let ffmpeg = resources.appendingPathComponent("ffmpeg/ffmpeg")

        let process = Process()
        process.executableURL = binary
        process.currentDirectoryURL = binary.deletingLastPathComponent()
        process.arguments = ["--ffmpeg", ffmpeg.path]

        var env = ProcessInfo.processInfo.environment
        for (key, value) in atlasDefaults where env[key] == nil {
            env[key] = value
        }
        process.environment = env

        // Jellyfin writes its own log files; the console copy isn't needed.
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice

        process.terminationHandler = { [weak self] proc in
            DispatchQueue.main.async { self?.serverExited(proc) }
        }

        do {
            try process.run()
            server = process
            lastStart = Date()
            setStatus("Running on port 8096")
            updateAwake()
        } catch {
            setStatus("Couldn't start: \(error.localizedDescription)")
        }
    }

    private func stopServer() {
        guard let process = server, process.isRunning else { return }
        server = nil
        process.terminationHandler = nil
        process.terminate() // SIGTERM: Jellyfin shuts down cleanly.

        let deadline = Date().addingTimeInterval(20)
        while process.isRunning && Date() < deadline {
            RunLoop.current.run(until: Date().addingTimeInterval(0.2))
        }
        if process.isRunning {
            kill(process.processIdentifier, SIGKILL)
        }
        updateAwake()
    }

    private func serverExited(_ process: Process) {
        guard !quitting, process === server else { return }
        server = nil
        updateAwake()

        // A crash right after starting usually means it will keep crashing (port taken, bad data).
        restartsInARow = Date().timeIntervalSince(lastStart) < 60 ? restartsInARow + 1 : 0
        if restartsInARow >= 3 {
            setStatus("Stopped (exit \(process.terminationStatus)). Check the logs.")
            return
        }

        setStatus("Server exited, restarting…")
        DispatchQueue.main.asyncAfter(deadline: .now() + 5) { [weak self] in
            guard let self, !self.quitting, self.server == nil else { return }
            self.startServer()
        }
    }

    private func alreadyRunning() -> Bool {
        let mine = Bundle.main.bundleIdentifier ?? ""
        return NSRunningApplication.runningApplications(withBundleIdentifier: mine)
            .contains { $0 != NSRunningApplication.current }
    }

    // MARK: Menu actions

    @objc private func openServer() { NSWorkspace.shared.open(serverURL) }

    @objc private func openLogs() {
        let logs = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/jellyfin/log")
        NSWorkspace.shared.open(logs)
    }

    @objc private func restartServer() {
        restartsInARow = 0
        setStatus("Restarting…")
        stopServer()
        startServer()
    }

    @objc private func toggleLogin() {
        do {
            if SMAppService.mainApp.status == .enabled {
                try SMAppService.mainApp.unregister()
            } else {
                try SMAppService.mainApp.register()
            }
        } catch {
            let alert = NSAlert()
            alert.messageText = "Couldn't change Start at Login"
            alert.informativeText = "\(error.localizedDescription)\n\nYou can also add Atlas Server in System Settings > General > Login Items."
            alert.runModal()
        }
        refreshToggles()
    }

    @objc private func toggleAwake() {
        let value = !UserDefaults.standard.bool(forKey: keepAwakeKey)
        UserDefaults.standard.set(value, forKey: keepAwakeKey)
        refreshToggles()
        updateAwake()
    }

    @objc private func quit() { NSApp.terminate(nil) }

    // MARK: Helpers

    // Keeps the Mac from idle sleeping while the server runs, so streams don't drop.
    private func updateAwake() {
        let wanted = UserDefaults.standard.bool(forKey: keepAwakeKey) && server?.isRunning == true
        if wanted, awakeActivity == nil {
            awakeActivity = ProcessInfo.processInfo.beginActivity(
                options: [.idleSystemSleepDisabled, .suddenTerminationDisabled],
                reason: "Atlas Server is running")
        } else if !wanted, let activity = awakeActivity {
            ProcessInfo.processInfo.endActivity(activity)
            awakeActivity = nil
        }
    }

    private func refreshToggles() {
        loginItem.state = SMAppService.mainApp.status == .enabled ? .on : .off
        awakeItem.state = UserDefaults.standard.bool(forKey: keepAwakeKey) ? .on : .off
    }

    private func setStatus(_ text: String) {
        statusLine.title = text
        statusItem?.button?.toolTip = "Atlas Server: \(text)"
    }

    private func item(_ title: String, _ action: Selector, _ key: String) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: key)
        item.target = self
        return item
    }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()