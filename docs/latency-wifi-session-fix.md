# Wi-Fi ping optimization correction — 2026-09-18

The reported run measured its baseline and then skipped its sole available
Wi-Fi streaming candidate because a methodology-4 profile had rejected it.
The profile contains summaries, not the original paired samples, so it cannot
establish whether that earlier run had any real gain.

The setter had a separate functional bug: `WlanSetInterface` enabled streaming
mode and read it back while its WLAN client was alive, then closed that client
in `finally`. Windows owns this request per client; closing it releases the
request. Thus a successful return did not mean the setting was still in place
when the optimizer measured it.

References:

- [Microsoft WLAN opcodes](https://learn.microsoft.com/en-us/windows/win32/api/wlanapi/ne-wlanapi-wlan_intf_opcode): streaming is aggregated across clients and reset on disconnect.
- [Chromium's scoped Wi-Fi settings](https://chromium.googlesource.com/chromium/src/+/d273eaaf76f75d2c38f0b9665ba03ddc28602b91/net/base/network_interfaces_win.cc#280): retains the WLAN client until the option scope ends; documents automatic restoration on close.

## Correction

- Retain one client per interface GUID after a successful enable/readback.
- Reassert on repeated apply, including after a reconnect clears the request.
- Release only our client on restore. Another program keeping streaming enabled
  is not a failed restore of our own request. Crash recovery without an owned
  client is already restored; Windows closed the terminated process's client.
- Preserve ownership after a close failure so rollback can retry.
- Cap the baseline-relative median effect floor at 1 ms for non-CPU-intensive
  active-probe changes. Preserve CPU/unknown-load multipliers, game-display
  thresholds, instrument resolution, regression checks, noise checks and the
  confidence interval. Newly eligible small gains need four cycles in production
  and the independent bundle check uses the same rule.
- Bump methodology to 5, retiring earlier accepted/rejected profiles so the
  fixed setting actually gets measured again on upgrade.

## Verification and limits

The regression tests first reproduced the immediate-client-close behavior,
1 ms rejections at 29/60/150 ms baselines, and reuse of methodology-4 profiles.
New tests cover retained state, reconnect/reapply, independent adapters and
clients, failed readback, close retries, cache migration, loss/contradiction
guards, four-cycle evidence, and an optimizer-to-headline-to-rollback scenario.
Native WLAN operations in these tests use a fake session backend; no test
changes the connected adapter or the running Vodafone mode.

No live A/B benchmark was run on the user's connection: Vodafone Unlimited must
remain active and the installed application must keep running. Unit tests prove
the control flow and decision rules, not a reduction on that physical path.
The released executable still needs the user's on-device measurement.
