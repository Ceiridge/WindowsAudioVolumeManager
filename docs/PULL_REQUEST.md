## Summary

This draft adds opt-in volume rules keyed by output endpoint and application identity, without replacing existing global application preferences.

Use case: notification audio and voice audio from the same application are already routed to different outputs. A single global application rule must not collapse them back to the same volume.

## Implementation

- Manage explicitly selected outputs concurrently, optionally adding the current Windows multimedia default. Changing the master-output selector does not disconnect other managed outputs.
- Resolve endpoint override -> existing global application preference -> configured default.
- Keep transient instance IDs only for callback routing, with endpoint-generation checks to discard late callbacks.
- Expose mixed live volumes instead of claiming that the first session represents every session.
- Offer opt-in maintenance for explicit endpoint rules. Read live state, coalesce callbacks, tolerate quantization and use event-triggered immediate correction, a per-session 10 ms minimum write interval, and an on-demand one-shot retry timer without suspending the user-selected target; never toggle mute.
- Add schema v2 and an online SQLite backup before upgrading an existing database. Keep the v1 global table intact.
- Provide a visible retry command that reapplies the saved target, and explain why autostart is unavailable in a preview.
- Add a copied-profile preview path (`WAVM_DATA_DIR`); autostart is disabled and legacy JSON migration is skipped in isolated profiles.
- Add a portable test executable for policy, selection, correction limits and persistence, plus a Windows build workflow and independent self-contained publishing script.

## Validation status

The revised policy/persistence harness contains 58 cases, including strict-maintenance and write-failure regressions. The initial preview was reported to build and pass its previous 42 tests on Windows; independent Discord output levels and restoration after restarting Discord were also reported. This is not validation of the revision 3 build.

The original development environment could not install/run the .NET SDK and had no Windows audio hardware. Compilation and Windows acceptance must not be inferred from static checks. Any later verified build results are appended below by the publication script.

Keep this PR in draft until a real Windows audio test confirms independent volumes, session recreation, resets in place, unplug/replug, no changes on unmanaged outputs, and clean return to the original executable.

## Limits

No semantic voice detection, no persistent differentiation of multiple streams of the same application on the same output, no in-game limiter, and no guarantee that the first audio buffer is attenuated. A compiled executable is not an audio-safety certification.
