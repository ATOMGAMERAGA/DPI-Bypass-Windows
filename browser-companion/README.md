# Social Observer — Alpha

This unpacked Chromium companion observes only the selected YouTube `/watch` target. Install the published native host with `install-windows.ps1`, load this folder as an unpacked extension, enable Alpha in the desktop app, and explicitly bind the target while an automatic observation is running. If binding before Start reports no active attempt, Start then bind again during its observation window. Keep the test tab visible. Same-account observation requires your explicit session declaration in the popup; it does not authenticate your identity.

The navigation checkbox permits one companion-owned trial tab. Other selected tabs are borrowed: the companion never reloads, navigates, closes, or pauses their media. A borrowed tab may need a user navigation to obtain fresh route evidence. No observation claims native-app repair, and the desktop conservatively keeps network-strategy browser attribution unverified.

Owned trial tabs use a 20-second observation lease, capped by the session's 120-second window. A content timer pauses owned media at the deadline, and a one-shot `chrome.alarms` alarm releases the trial tab if the desktop, content script, or worker disappears. Browser scheduling and suspended devices can delay timers/alarms; this is not an exact browser-byte or real-time transport cutoff. Native IPC has its own four-second response timeout. There are no periodic background probes or external polling jobs. Existing content ticks may retry local IPC during a transient desktop rollback/apply, only within their existing observation lease.

Explicit companion Stop, desktop Stop detected by the bounded local handshake, or an expired lease closes only the owned trial. On automatic success or transition to manual testing, the companion first pauses owned media, retains the visible result tab, and transfers that tab to the user. Sampling and the renderer lease then end. Later Stop restores desktop resources but does not close that transferred user tab; you may resume playback yourself. A subsequent trial may create one new owned tab. Previously transferred tabs are ordinary user tabs and are never managed again.

All state writes run through one worker queue. Bind and Stop immediately invalidate outstanding work; a persisted lifecycle generation guards continuation writes and navigation. Binding changes compare the complete session/attempt/operation/context/mode/document/goal/target tuple. Per-session renderer-load counts survive Stop/rebind and never reset merely because the popup binds again. Freshness is tied to the current main-frame request and document. State is ephemeral `chrome.storage.session` data, including the active target URL; no URL or account data is written to durable extension storage/history.

The desktop's `social.bind` response uses `{ok:true,binding}` while bindable. A temporary transition returns `{ok:false,reason:"NO_ACTIVE_ATTEMPT",finished:false}`. Terminal/non-observing states use `finished:true`, with `keepActive:true` only for a kept result or an active manual trial. `social.observe` success returns `{ok:true,finished:true,keepActive:true}`. Unsupported commands and malformed observation fields remain rejected by the native bridge.

Run all local detector, worker lifecycle, and content lease regressions:

```powershell
node --test browser-companion/*.test.cjs
```

These Node VM tests exercise the packaged scripts with deterministic browser API fixtures. Real Chromium installation, timer scheduling, DOM compatibility, driver routing, and field effectiveness still require device testing.
