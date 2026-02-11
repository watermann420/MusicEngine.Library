# MusicEngine.Library

Community-driven instruments, presets, and tools that plug into MusicEngine.

Rules:
- Free content only (no paid packs or license-walled assets).
- Keep dependencies minimal and offline-friendly.
- Instruments should be self-contained and safe to load in scripts.

What goes here:
- Sample-based instruments and kits.
- Procedural instruments (synths, drums, generators).
- Utilities that build instruments from local files.

How to use in scripts:
```csharp
// List available library instruments
var list = LibraryTools.List();

// Create a library instrument by type name
var speech = LibraryApi("MusicEngine.Instruments.SpeechInstrument");
```

## Add A New Instrument

1) Create a class in `MusicEngine.Library/Instruments` that implements `MusicEngine.Core.ISynth`.
2) Keep it stable: no UI, no network, no blocking work in `Read`.
3) Use `Settings.SampleRate` and `Settings.Channels` for consistency.

Minimal skeleton:
```csharp
using System;
using MusicEngine.Core;
using NAudio.Wave;

namespace MusicEngine.Instruments;

public sealed class MyInstrument : ISynth
{
    private readonly WaveFormat _waveFormat =
        WaveFormat.CreateIeeeFloatWaveFormat(Settings.SampleRate, Settings.Channels);

    public string Name { get; set; } = "MyInstrument";
    public float Volume { get; set; } = 1f;
    public float Pan { get; set; } = 0f;
    public float ModWheel { get; set; } = 0f;
    public int Channel { get; set; } = -1;
    public float Reverb { get; set; } = 0f;
    public float Chorus { get; set; } = 0f;
    public WaveFormat WaveFormat => _waveFormat;

    public void NoteOn(int note, int velocity) { }
    public void NoteOff(int note) { }
    public void AllNotesOff() { }
    public void SetParameter(string name, float value) { }

    public int Read(float[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        return count;
    }
}
```

## Sample-Based Instruments

Use the built-in `SamplerInstrument` inside your library class to keep code small.

```csharp
using System;
using MusicEngine.Core;
using MusicEngine.Instruments;
using NAudio.Wave;

namespace MusicEngine.Instruments;

public sealed class MySampleKit : ISynth
{
    private readonly SamplerInstrument _sampler = new();
    public WaveFormat WaveFormat => _sampler.WaveFormat;
    public string Name { get; set; } = "MySampleKit";
    public float Volume { get => _sampler.Volume; set => _sampler.Volume = value; }
    public float Pan { get => _sampler.Pan; set => _sampler.Pan = value; }
    public float ModWheel { get => _sampler.ModWheel; set => _sampler.ModWheel = value; }
    public int Channel { get => _sampler.Channel; set => _sampler.Channel = value; }
    public float Reverb { get => _sampler.Reverb; set => _sampler.Reverb = value; }
    public float Chorus { get => _sampler.Chorus; set => _sampler.Chorus = value; }

    public MySampleKit()
    {
        // Map samples to notes (example paths)
        _sampler.MapSample(36, "Samples/Kick.wav");
        _sampler.MapSample(38, "Samples/Snare.wav");
        _sampler.MapSample(42, "Samples/Hat.wav");
    }

    public void NoteOn(int note, int velocity) => _sampler.NoteOn(note, velocity);
    public void NoteOff(int note) => _sampler.NoteOff(note);
    public void AllNotesOff() => _sampler.AllNotesOff();
    public void SetParameter(string name, float value) => _sampler.SetParameter(name, value);
    public int Read(float[] buffer, int offset, int count) => _sampler.Read(buffer, offset, count);
}
```

Tips:
- Keep samples local to the library repo (e.g. `MusicEngine.Library/Samples/...`).
- Use short, trimmed files to avoid large memory spikes.
- If you move samples into a new folder, update the paths in your instrument constructor.

Example folder restructure:
```
MusicEngine.Library/
  Samples/
    Drums/
      Kick.wav
      Snare.wav
```

```csharp
_sampler.MapSample(36, "Samples/Drums/Kick.wav");
_sampler.MapSample(38, "Samples/Drums/Snare.wav");
```

## Full Instruments With MIDI

Library instruments are normal `ISynth` instances. In scripts, route MIDI just like built-ins.

```csharp
var inst = LibraryApi("MusicEngine.Instruments.MySampleKit");
midi.Device(0).to(inst);

// Layer with other instruments
var synth = CreateSynth();
midi.Device(0).to(inst, synth);

// Priority/fallback
midi.Device(0).to(inst, < synth);
```

If you need custom control:
- Expose properties for parameters (cutoff, decay, etc).
- Support `SetParameter(name, value)` for quick mapping.
- Use `Channel` if you want a single MIDI channel only.
