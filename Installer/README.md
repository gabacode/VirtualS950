# Installer

One installer for both halves: **Akai S950 Studio**, which opens disk images and edits
programmes, and the **VST3 plugin**, which plays those same disks in a DAW. They share the
engine — the measured constants in `Cal.cs` and `Cal.h` agree to the digit — so they are one
instrument with two front ends rather than two programs.

## Building it

```
cd Installer
.\build-installer.ps1
```

That builds the editor, builds the plugin, checks that every file the script names actually
exists, and only then compiles the installer into `Output\`. A packaging step that quietly
ships a stale binary, or quietly leaves one out, is worse than one that stops.

```
.\build-installer.ps1 -CheckOnly    # just list what would be packaged, and when it was built
.\build-installer.ps1 -SkipBuild    # package what is already built
```

## What it needs

**Inno Setup 6.3 or newer** — free, about 6 MB, from <https://jrsoftware.org/isdl.php>. It is
the only thing in the way; everything else builds with what is already on the machine. 6.3
is the floor because the script uses the `x64compatible` architecture name.

## What it installs

| | Where | Chooseable |
|---|---|---|
| `AkaiS950Studio.exe` | `{autopf}\VirtualS950` | yes |
| `VirtualS950.vst3` | `{autocf}\VST3` | yes |
| `VirtualS950.exe` (standalone) | `{autopf}\VirtualS950` | yes |

The installer asks at the start whether it is for everyone or just you, and both
destinations follow that answer. A VST3 has two homes on Windows — the machine-wide one
under Common Files, which needs administrator rights, and the per-user one under
LocalAppData, which does not — and Inno's `{autocf}` resolves to whichever was chosen. That
is why it asks rather than assuming: requiring administrator to install an audio plugin is a
poor trade when Windows offers a perfectly good folder that does not.

After a per-user install it says where the plugin went, because hosts differ on which
folders they scan and that one usually has to be pointed out once. In Ableton:
Preferences → Plug-Ins → VST3 Plug-In Custom Folder.

The editor needs the .NET Framework 4, which Windows 10 from 1903 and every Windows 11 ship
in the box. The installer checks anyway and offers to carry on with just the plugin, because
"nothing happens when I run it" is a miserable way to find out.

## Before giving it to anyone else

**The repository has no licence file, and the plugin links JUCE, which is GPL3 unless you
have bought a licence.** Distributing a binary built against GPL3 JUCE obliges you to offer
the source under GPL3. The repository being public goes most of the way, but until there is
a `LICENSE` in it the terms are not actually stated — and with no licence at all, nobody
else has permission to use what they download. Worth settling before this installer goes
anywhere.
