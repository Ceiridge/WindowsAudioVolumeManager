# Windows audio acceptance — revision 3 pending

The revised build has not been exercised on Windows in the preparation environment. Previous-preview reports confirmed a clean build, 42 tests, independent Discord VAIO/AUX levels and restoration after restarting Discord; reconnecting voice and first-run games were not tested. Scenarios below must be recorded for this revision; do not carry forward a blanket pass. Neither the Python SQL checks nor the C# unit harness exercise real WPF/WASAPI/Voicemeeter audio. Begin with a low physical headphone/speaker level. Do not infer safety from a percentage.

Record the Windows version, original and preview version, build report, endpoint names (redact private data), and each result without publishing user paths, configuration databases or raw logs.

| # | Scenario | Required observation | Status |
| --- | --- | --- | --- |
| 1 | Start preview with the supplied isolated launcher | Visible UI; copied data; original data and autostart entry unchanged | Pending |
| 2 | Attempt preview while original runs | Launcher refuses; no process is killed or duplicate controller started | Pending |
| 3 | Enable device mode, check VAIO and AUX | Both outputs stay managed concurrently | Pending |
| 4 | Set Discord/VAIO low and Discord/AUX independently | Effects change without changing voices or another app | Pending |
| 5 | Switch master-output display between VAIO/AUX | No session rule is reapplied merely by selecting another display output | Pending |
| 6 | Display an unchecked non-default output | No session volume write on that output | Pending |
| 7 | Restart Discord and recreate a voice session | Both endpoint overrides are restored independently | Pending |
| 8 | Reset an unlocked live volume externally | Live display updates; saved target is not silently overwritten | Pending |
| 9 | Enable Maintain after an external reset | Previously saved target is used, not the high reset volume | Pending |
| 10 | Reset a maintained volume on an existing session | Target is corrected without toggling mute | Pending |
| 11 | Introduce repeated conflicting writes at a low physical level | Maintain never suspends; correction occurs rapidly while avoiding high-frequency rewrite loops | Pending |
| 12 | Start a new app on a managed output | Default applies; separately measure first-buffer latency without claiming a universal guarantee | Pending |
| 13 | Start an app with an existing global preference | Preference is inherited; adding one device override does not modify global or other endpoint rules | Pending |
| 14 | Disconnect/reconnect managed output; change Windows default | Rules retained; only checked plus explicitly followed default outputs managed; no matching by friendly name | Pending |
| 15 | Multiple live sessions with unequal volumes on same output | Mixed range shown; explicit slider change addresses that output's group | Pending |
| 16 | Quit preview; return to original; reboot only after acceptance | No concurrent controller; original configuration available; document live-volume differences separately | Pending |

Additional revision-3 acceptance:

| # | Scenario | Required observation | Status |
| --- | --- | --- | --- |
| 17 | Briefly drag the Windows Discord/VAIO slider down, then release | Saved target returns quickly without reconnecting, refreshing or toggling Maintain | Pending |
| 18 | A writer also overrides recovery probes | Writes are paced per session (no multi-second pause); other sessions/outputs remain independent | Pending |
| 19 | Windows profile installation from the separate kit | Tested profile is preserved; normal profile/Run value backed up; normal launch has enabled autostart checkbox | Pending |
| 20 | Enable Start with Windows in the installed app; next logon | Installed EXE is registered and launches once, using the normal migrated profile | Pending |
| 21 | Roll back the installation | Prior normal profile and Run value restored; newer settings/binaries retained; no automatic audio-level restoration claimed | Pending |
| 22 | Latest build attempt fails | Preview/install/submission helpers refuse an older success report or wrong revision | Pending |

Keep the PR in draft until these observations are recorded. Test the self-contained package on a Windows machine without an existing .NET runtime as an additional deployment check. Verify dependency notices before distributing public binary releases.
