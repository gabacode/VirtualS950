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

**None of it has ever been compiled.** There is no C++ compiler on the machine it was
written on. Read that as it is meant: this is a careful transcription, not working code, and
the first thing to do with a compiler is run the check below — not load it in a DAW.

## What it needs

- **Visual Studio Community 2022**, "Desktop development with C++". Free for individuals and
  open source. The Build Tools package is the same compiler without the IDE, but a debugger
  you can attach to a DAW is worth having.
- **JUCE** — `git clone https://github.com/juce-framework/JUCE`. No installer, and it carries
  the VST3 SDK headers, so there is nothing to fetch from Steinberg.

JUCE is GPL3 unless you buy a licence. `VirtualS950` is a public repository, so a GPL3 plugin
is fine; a closed-source one would not be.

## First thing to run

```
cd Plugin\Tests
cl /std:c++17 /EHsc /I..\Source\S950 ConformanceCheck.cpp ..\Source\S950\Voice.cpp ..\Source\S950\Engine.cpp
ConformanceCheck.exe
```

It needs no JUCE and no audio device. It puts the same inputs through the port that
`ReferenceDump` put through the C#, and insists on the same answers to nine decimal places —
the filter's cutoff at 65 points, the envelope times at 34, the LFO's rate and delay fade,
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

1. Run the conformance check. Nothing below is worth starting until it passes.
2. The JUCE wrapper — `AudioProcessor`, `processBlock` calling `Engine::render`, MIDI in
   from the host.
3. **Sample-accurate MIDI.** The ring carries no timestamps, so events land at the start of
   the next block. At a 512-sample buffer that is 11 ms of jitter, which is audible on drum
   programming and is exactly what this instrument is for. Events need a sample offset and
   `render` needs to split at them.
4. **State.** A host saves the project and expects it back exactly. A file path breaks the
   moment the library moves — but an S950 image is 800×1024 bytes, so the whole disk can go
   in the plugin's state and there is never a missing file to hunt.
5. Reading disks. `AkaiS950List` is 2700 lines of byte manipulation with no dependencies;
   the plugin only needs the reading half of it.
