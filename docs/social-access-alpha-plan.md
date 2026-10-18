# Social Access Experiments — Alpha: implementation plan

Spec: `C:/Users/Atom/Downloads/DPI-Bypass-Akilli-Sosyal-Erisim-Codex-Prompt.md` (v3, read in full). This supplied spec is authoritative. Implementation authorization includes ordinary design choices and local changes; no push, publication or account actions.

## Intent and design

An opt-in feature in each existing app runs one reversible experiment at a time. MANUAL is the migration default; AUTO_BETA runs the same candidates through network and content observation adapters. Unknown evidence stays unknown. A browser/webview result cannot establish native-app or account success. The normal engine remains the sole packet-processing engine. No remote relay, account data collection, region change, certificate override, automatic installation or background testing.

## Requirement → code → change → verification

| Requirement | Windows | Android | Linux | Verification |
|---|---|---|---|---|
| Single owner and rollback | ProtectionService, StrategyCoordinator | VpnService / StrategyTester | daemon / proxy / strategies | fail → undo → next; conflicts; cancellation |
| Disabled by default, MANUAL migration | AppSettings, MainViewModel | SettingsRepository, settings Compose UI | config, IPC, GTK UI | old/malformed settings retain normal protection |
| Two actual modes | SocialAccess coordinator | Kotlin equivalent | Python equivalent | common JSON vectors and local integration tests |
| Target evidence | packaged browser observer | controlled WebView | packaged browser observer | target/surface/identity/document match; no HTTP-only PASS |
| Recovery and budgets | private journal, bounded transports | app-private journal, bounded transports | daemon journal, bounded transports | restart recovery; stale feedback; limits |
| Honest scope | result card and report | result card and report | result card and report | device/field NOT_RUN separately from test/build results |

## Shared contracts

Use schemaVersion 2 and the exact enum strings in spec sections 6, 7, 11, 12. Goal dimensions are independent: platformId, problemClass, identityContext, allowedSurfaces, requiredCapabilities, publicFallbackAllowed. Keep target HTTPS URL only in active memory; use a random targetRef in persisted summaries. Tri-state evidence TRUE/FALSE/UNKNOWN. Network probes alone never establish content success.

One coordinator serializes operations, verifies session/attempt/operation/context/mode/document generations, rejects replay, journals intent before side effects, rolls back before next apply, and stops on user-reported resolution. An unresolved owned resource blocks further trials. Rollback compares current values with what the operation installed. Disable/stop/restart only undoes Alpha resources. Same-account observations require explicit user-bound session evidence. Automated success requires two matching positive observations separated by at least 2 seconds, known absence of gates, exact target, supported current observer/evaluator and fresh route evidence when attributing a network repair. Media requires natural progress of at least 3 seconds with no seek/ad/preview. Unsupported/gate/observer errors remain distinct.

Budgets: 12 distinct candidates, 24 diagnostic requests, 5 MiB diagnostic data, 120 seconds active probes per group, 20 seconds per probe, 6 renderer loads, estimated renderer budget explicitly disclosed, metered renderer off without preference, 15 minute manual inactivity. No continuous polling while awaiting user input or when disabled.

## Tasks and independent ownership

1. Windows (root): C# contracts/coordinator/evaluator, real StrategyCoordinator lease integration, target-safe bounded network probe, WPF settings and trial controls, owned recovery, desktop observation integration, meaningful tests and documentation.
2. Android (android agent): all Android-only files; Kotlin contracts/coordinator/evaluator, existing-engine adapter, settings and Compose controls, controlled public-content WebView, VPN lifecycle and network-context handling, tests and platform report.
3. Linux (linux agent): all Linux-only files; Python contracts/coordinator/evaluator, daemon/IPC/config/GTK integration, existing-engine adapter, companion integration, tests and platform report.
4. Root integration: common JSON fixtures copied identically to `tests/fixtures/social-access-v2.json` in each repo. Desktop observer distributed from `browser-companion/` in Windows and Linux (source identical where applicable). Verify each implementation against the shared fixture contract, review diffs, run available full suites/builds, record remaining NOT_RUN items without claiming field success.

Each platform task starts with tests exposing missing functionality, implements through existing call paths, runs focused and full available checks, and self-reviews target/privacy/ownership boundaries. Build tool absence must be investigated and concretely reported, never treated as passed. Assess all A0–A6/N1–N6 families with IMPLEMENTED/MANUAL_GUIDED/EXCLUDED/RESEARCH_ONLY status and sources; no placeholder handlers counted as implemented.

## Review focus

Stale asynchronous completion after mode/candidate/context change; external edits during rollback; malformed settings preserving normal settings; renderer messages from wrong target/document/frame; budget exhaustion or observer failure incorrectly becoming candidate failure/success.

## Decisions and progress

- 2026-10-17: `git pull` succeeded in all three repositories before edits. Windows 8ac250f, Android 3032d6c, Linux 79e286e. Linux initially lacked .git; all 78 source files matched HEAD exactly; restored metadata without replacing sources.
- Ruling: implement in the provided checkouts on feature branches; independent repository ownership prevents overlapping edits. No worktree consent needed because none is created.
- Ruling: supplied detailed spec and explicit instruction to proceed on routine decisions supersede skill approval round trips. Device/field validation still needs real environments and is reported separately.
- 2026-10-18: tasks 1–4 have local implementations, shared contract fixtures, review fixes and final host-available verification. Windows 1,374 tests and WPF build pass; Android 18 pure-core JUnit tests/6 JS/51-file grammar pass; Linux 41 Alpha/15 stub GUI/20 JS pass. Linux full suite is host-blocked (110 tests, 7 missing-grp errors, 4 skips); Android full SDK/NDK build is not verified.
- Implementation is limited, not full prompt completion: existing Windows GUI still requiresAdministrator; complete GUI/privileged-engine separation is open. Browser handoff and native host use the user token. Device/browser/field validation remains NOT_RUN/FIELD_UNVERIFIED. See `social-access-alpha-delivery.md` for commands, acceptance scope and remaining work. No push or installation performed.

- 2026-10-18 completion: the earlier Windows privilege gap and Android full-build/Linux host gates were closed. Windows GUI now asInvoker with headless service helper; final1,424 tests/build/publish/real GUI PASS. Android full53-test/JNI/APK build and API35 emulator VPN permission/start/revoke smoke PASS. Linux318-test actual suite, GTK/netns/native and actual Chromium153 fixture lifecycle PASS. Earlier pending/host-blocked notes above are historical. Field effects and physical device conditions remain explicitly separate; see final delivery record.
