# Windows user GUI / privileged adapter completion

Spec: adopted DPI-Bypass-Akilli-Sosyal-Erisim-Codex-Prompt.md, section 15, and user's explicit request to finish remaining implementation. Existing authorization covers routine architectural choices and execution without approval round trips.

The normal WPF process and browser run as the desktop user. A separately invoked headless instance owns the existing ProtectionService and every driver/DNS/NIC mutation. Preserve the current API through IProtectionController and a remote facade; never create a local engine in the GUI. The Alpha coordinator/observer/journal stay in the GUI and only the operation-bound strategy control crosses IPC.

- [x] Define exact public controller contract, snapshot and strict bounded RPC transport. Fixed contract methods only; same-user ACL, verified peer PID/executable, medium-integrity pipe, concurrent cancellation. Snapshot DTOs must preserve counters, IP addresses and tuple results.
- [x] Implement remote facade with stable mutable settings, ordered event delivery, bounded startup/UAC and calls. Helper monitors owning GUI death, releases Alpha ownership and disposes engine/DNS before exit.
- [x] Wire headless helper before GUI/single-instance startup, asInvoker UI manifest, user autostart and explicit elevation for direct privileged CLI housekeeping. Never show an elevated GUI; existing installer headless paths remain bounded.
- [x] Retain normal latency/hotspot/DNS/domain/verification controls and existing CLI control channel through the controller contract. Start DNS recovery watchdog from the privileged owner.
- [x] Test actual local broker IPC, malformed/unauthorized/oversized messages, serialization roundtrips, cancellation, stale operation recovery, normal facade calls and settings merge. Build/test full application and existing setup/resource scripts. Device/field tests must use actual capabilities; fixtures do not create field success.

Review focus: a GUI crash cannot leave an experiment active; a late response cannot overwrite fresh settings; a helper must never accept methods outside the fixed controller contract; cancel/Stop must not queue behind a long probe; elevated old autostart must hand off to the user without creating a window.

Verification: 1,424 Windows tests PASS; WPF build/publish zero warnings/errors; Debug and published Release GUI self-tests exit 0; all four existing PowerShell scripts and20 companion tests PASS. Production owner-death teardown was reviewed; a physical privileged driver/UAC/network field scenario was not executed on the user host.
