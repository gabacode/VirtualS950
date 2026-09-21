# VirtualS950 — the plugin

The S950 engine as a VST3, so a programme off a real disk can be played from a DAW.

## Where this has got to

`Source/S950` is the engine, ported from `AkaiS950Engine` and depending on nothing but the
C++ standard library — no JUCE, no SDK, no audio device. That is deliberate: it is the half
worth being sure of, and keeping it free of everything else means it can be compiled and
checked on its own, long before a plugin will load.

```
Source/S950/Cal.h        the measured constants, and the mappings from panel bytes
Source/S950/Filter.h     6th-order Butterworth, three biquads
Source/S950/Patch.h      Sound, KeygroupPatch, Patch — what a voice needs
Source/S950/Voice.h/cpp  one sounding note
Source/S950/Engine.h/cpp eight voices, the event ring, the patch hand-off
Tests/ConformanceCheck.cpp
Tests/Reference.h        GENERATED — what the C# computes, to be held to
```

**It compiles, and it agrees with the C# exactly.** For one commit it was a transcription
nobody had run, because the machine it was written on had no C++ compiler. The first build
once Visual Studio arrived was clean at `/W4`, and the conformance check passes 141 of 141 —
with the worst relative difference across the filter's 65 cutoff points at exactly 0.

## What it needs

On **macOS**: `xcode-select --install` and `brew install cmake`.

On **Windows**:

- **Visual Studio Community 2026** with "Desktop development with C++" — what this was built
  with, MSVC 14.51. Note that `cl.exe` is never on the PATH: the installer leaves it off on
  purpose, because the compiler needs INCLUDE, LIB and PATH set for one target architecture.
  `build.ps1` finds `vcvars64.bat` through vswhere and sets them; the Start menu's "Developer
  PowerShell for VS 2026" does the same thing by hand.
- **JUCE** — `git clone https://github.com/juce-framework/JUCE`. No installer, and it carries
  the VST3 SDK headers, so there is nothing to fetch from Steinberg. Drive it with JUCE's
  CMake support rather than a Projucer-generated solution: the Projucer emits project files
  for the Visual Studio versions it knows by name, and CMake does not care which one is
  installed.

JUCE 9 is **AGPLv3** unless you buy a licence — not GPL3, which is what JUCE 6 and 7 were.
`VirtualS950` is AGPLv3 to match, so there is no gap between what this repository says and
what the JUCE parts oblige. A closed-source plugin would need a commercial JUCE licence.

## Building the plugin

```
cd Plugin
cmake -S . -B build
cmake --build build --config Release --parallel
```

On macOS the build type goes in at configure time instead:

```
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build --parallel
```

The mac build is **universal** — arm64 and x86_64 — which is why it compiles everything
twice. `-DCMAKE_OSX_ARCHITECTURES=arm64` halves that while you are working.

CMake ships inside Visual Studio, so there is nothing else to install — `build.ps1`
finds it the same way it finds the compiler. That produces three things, or four on
a Mac:

- a **VST3**, installed to `%LOCALAPPDATA%\Programs\Common\VST3`. JUCE would rather put it in
  `C:\Program Files\Common Files\VST3`, but that cannot be written to — or created — without
  elevation, and building as administrator to test an audio plugin is the wrong trade. The
  per-user folder is the other location the VST3 specification names. In Ableton, add it once
  under Preferences → Plug-Ins → VST3 Plug-In Custom Folder.
- a **standalone** at `build/VirtualS950_artefacts/Release/Standalone/VirtualS950.exe`, which
  opens without a DAW. That is what makes "is the plugin broken, or is the host unhappy with
  it" answerable in one step rather than two.
- on macOS, an **AU** installed to `~/Library/Audio/Plug-Ins/Components`, because Logic and
  GarageBand load nothing else. The VST3 goes to `~/Library/Audio/Plug-Ins/VST3`, which mac
  hosts scan without being pointed at it.
- the **conformance check**, at `build/Release/ConformanceCheck.exe`, or
  `build/ConformanceCheck` on macOS.

## Checking the engine on its own

```
cd Plugin
.\build.ps1            # Windows
./build.sh             # macOS
```

It needs no JUCE, no CMake and no audio device. It puts the same inputs through the port that
`ReferenceDump` put through the C#, and insists on the same answers to nine decimal places —
the filter's cutoff at 65 points, the envelope times at 35, the LFO's rate and delay fade,
every measured constant, plus the filter's DC gain and stopband and the engine driven end to
end through its event ring.

Regenerate `Reference.h` after any change to `Cal`, `Filter` or the envelopes on the C# side:

```
.\test.ps1                                     # from the repo root, to be sure the C# is sound
csc /target:exe /main:ReferenceDump /out:%TEMP%\RefDump.exe AkaiS950Tests\ReferenceDump.cs AkaiS950Engine\*.cs
%TEMP%\RefDump.exe Plugin\Tests\Reference.h
```

## Why the port looks like the C#

Almost line for line, on purpose. Three implementations of this instrument now exist — the
web's `audio.js`, `AkaiS950Engine`, and this — and they are meant to agree to the digit,
because every number in them came off a recording of a real machine rather than out of a
manual. Keeping the shape identical is what makes a disagreement easy to find.

Two places where it could not stay identical, both in `Engine`:

- **The patch hand-off.** The C# assigned a reference and let the collector decide when the
  old programme could go. There is no collector here, and both obvious replacements free
  memory on the audio thread. The patch is moved between threads through a two-slot
  exchange instead; `Engine.cpp` says why at length. Call `collectRetiredPatch()` from the
  message thread, or the old patch is never released.
- **Sound ownership.** `Sound` is held by `shared_ptr`, which the C# did not need. A voice
  reads a sample buffer on the audio thread while the message thread may be replacing the
  programme, and shared ownership is what stops the buffer being freed underneath it.

## Still to do

1. **Reading disks.** The plugin plays a placeholder sawtooth until it can open an image.
   `AkaiS950List` is 2700 lines of byte manipulation with no dependencies; the plugin only
   needs the reading half of it.
2. **State.** A host saves the project and expects it back exactly. A file path breaks the
   moment the library moves — but an S950 image is 800×1024 bytes, so the whole disk can go
   in the plugin's state and there is never a missing file to hunt. Only the parameters are
   saved today.
3. An editor worth looking at. What is there now is a gain knob and a voice count, the
   second of which answers the first question anyone asks of a silent plugin — is it getting
   the notes? — without a debugger.

## Sample-accurate events

`noteOn`, `noteOff`, `modwheel` and `allNotesOff` each take an `at` — where in the next block
the event belongs, in samples. `render` fills the block in stretches between events rather
than applying them all at the top, so a note lands on the sample the host asked for.

This is the one place the port deliberately does *more* than the C#, and it is not a
refinement. Applying everything at the block boundary still sounds like a working
instrument — just one that quantises every note it is sent to the buffer size. At 512
samples that is 11 ms, which nobody hears as a fault; they hear a drum machine that does not
quite swing. The C# has no use for it, because a piano keyboard and a MIDI port have nothing
finer to offer, and `at` defaults to 0, which is exactly that behaviour.
