# Device-volume preview — unofficial implementation

This is a proposed change to Ceiridge/WindowsAudioVolumeManager, based on commit
`332ab6fbcc06adc2af514f3cc600029edc53c722`. It is not an upstream-approved release.

## Set up the two outputs

Keep your existing routing: notification audio to Voicemeeter Input (VAIO), voice
audio to Voicemeeter AUX Input. This application does not configure that routing.

Enable **Separate volumes by output device**, then check VAIO and AUX. The
**Follow Windows default device** setting adds the current multimedia default;
disable it to manage only the explicitly checked list. Selecting an output in the
master-output dropdown does not add that output to the managed list.

The list should now show distinct Discord rows under the two outputs. Set the VAIO
row to a low target, enable **Override**, and enable **Maintain** only if you want
changes from Discord or the Windows mixer to be corrected. Set and save the AUX
row separately. Existing game settings remain fallback rules; applications without
any matching rule receive the configured default on managed outputs only.

Removing an override restores that output's inherited setting. It does not delete
the other output's rule. Switching back to legacy mode reapplies the legacy rules.
Use a low physical output level while testing those transitions.

## Saved versus Maintain

An override without Maintain is restored when a session appears; external live
volume changes do not overwrite its saved target. Maintain also corrects changes
on an existing session. Edit the slider in this manager to change a maintained
target. Enabling Maintain preserves the saved target even if the displayed live
volume has since been reset. The first mismatch is handled immediately when the Core Audio change event
arrives on the worker. Echoes at the target are ignored. When another writer
changes the volume repeatedly, writes are spaced by at least 10 ms per live
session; a one-shot timer performs the next correction even after the last
external event. Maintain is **never suspended by an external volume writer**.
There is no periodic 100 Hz scanner, and separate VAIO/AUX sessions have
independent rate guards. A failed Core Audio operation gets a 150 ms retry, and
persistent failures are logged. Use the context menu **Reapply saved volume
now** to force an explicit reapplication of the saved target.

Core Audio callbacks, Windows scheduling and device buffering introduce
unmeasured latency. This is not a hardware limiter and cannot guarantee
attenuation of the very first audio frame. Do not rely on this program as a
sole hearing-protection mechanism. Keep physical output level conservative.

Multiple sessions on the same output remain an application group. Mixed volumes
are shown as a range; moving the slider deliberately sets the whole group. There
is no claim of semantic identification of voice versus notifications.

## Preview data isolation

The provided launcher copies the original application-data directory only after
holding the shared single-instance mutex. It supplies the new `WAVM_DATA_DIR`
environment variable to the preview. In this mode, the original SQLite database
and startup registry entry are not changed, the main window starts visible, and
legacy JSON migration is skipped. A fresh isolated profile defaults to 5%, while a
copied profile retains its configured default and global preferences.

An isolated profile does NOT isolate live Windows audio: the preview intentionally
changes current session volumes. Closing it does not automatically restore those
live volumes. To return, close the preview and start the original executable;
relaunch an application or adjust its Windows mixer volume if necessary. No
original configuration or executable needs to be replaced.

Running the built EXE directly, without WAVM_DATA_DIR, uses the original normal
configuration location. For that path, back up the full data directory first and
review autostart settings. The separate kit installer preserves the previous normal
profile and copies the tested preview profile; autostart remains deliberately
disabled for isolated profiles. `--show` forces a visible window when launching
the installed application without changing its saved Start minimized preference. Schema v2 is additive; a consistent pre-upgrade SQLite
backup is also created alongside an existing database. Never copy only a live .db
while ignoring WAL contents.

## Independent use

`scripts/Publish-Windows.ps1` builds, runs the policy/persistence tests and publishes
an unsigned self-contained Windows x64 package. It does not launch the application
or publish anything online. Keep the package and the matching complete source
archive locally: an upstream merge is not required to use that build.

Runtime and GUI acceptance are separate from compilation. Keep physical volume
low for first-run tests. This is neither a limiter nor a guarantee against the
very first loud audio buffer.
