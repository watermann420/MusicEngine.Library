// MusicEngine License (MEL) - Honor-Based Commercial Support
// Copyright (c) 2025-2026 Yannis Watermann
// Description: Offline text-to-speech style instrument (simple formant synth).

using System;
using System.Collections.Generic;
using System.IO;
#if WINDOWS
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.SpeechSynthesis;
#endif
using MusicEngine.Core;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MusicEngine.Instruments;

/// <summary>
/// Offline speech-style instrument using simple formant synthesis (no external samples).
/// </summary>
public sealed class SpeechInstrument : ISynth
{
    private readonly WaveFormat _waveFormat;
    private readonly Dictionary<int, SpeechSample> _samples = new();
    private readonly List<Voice> _voices = new();
    private readonly object _voiceLock = new();
    private readonly Random _random = new();

    public string Name { get; set; } = "Speech";
    public WaveFormat WaveFormat => _waveFormat;
    public float Volume { get; set; } = 0.9f;
    public float Pan { get; set; } = 0f;
    public float ModWheel { get; set; } = 0f;
    public int Channel { get; set; } = -1;
    public float Reverb { get; set; } = 0f;
    public float Chorus { get; set; } = 0f;

    /// <summary>
    /// Speech rate multiplier (1 = normal).
    /// </summary>
    public float Rate { get; set; } = 1.15f;

    /// <summary>
    /// Alias for Rate (playback speed multiplier).
    /// </summary>
    public float Speed
    {
        get => Rate;
        set => Rate = value;
    }

    /// <summary>
    /// Pitch multiplier for the excitation signal.
    /// </summary>
    public float Pitch { get; set; } = 1.2f;

    /// <summary>
    /// Alias for Pitch (voice pitch multiplier).
    /// </summary>
    public float VoicePitch
    {
        get => Pitch;
        set => Pitch = value;
    }

    /// <summary>
    /// Formant frequency multiplier.
    /// </summary>
    public float FormantShift { get; set; } = 1f;

    /// <summary>
    /// Voiced component level.
    /// </summary>
    public float VoiceLevel { get; set; } = 0.35f;

    /// <summary>
    /// Noise component level.
    /// </summary>
    public float NoiseLevel { get; set; } = 0.18f;

    /// <summary>
    /// Max number of concurrent voices.
    /// </summary>
    public int MaxPolyphony { get; set; } = 8;

    /// <summary>
    /// Use Windows TTS if available (falls back to offline formants on failure).
    /// </summary>
    public bool UseWindowsTts { get; set; } = true;

    public SpeechInstrument()
    {
        _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(Settings.SampleRate, Settings.Channels);
    }

    /// <summary>
    /// Generate a speech sample and map it to a MIDI note.
    /// </summary>
    public void Phrase(string text, int note = 60)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        MidiValidation.ValidateNote(note);
        var trimmed = text.Trim();
        var sample = UseWindowsTts ? GenerateSampleWinTts(trimmed) : null;
        sample ??= GenerateSampleOffline(trimmed, note);
        if (sample == null) return;
        _samples[note] = sample;
    }

    public void NoteOn(int note, int velocity)
    {
        note = Math.Clamp(note, 0, 127);
        velocity = Math.Clamp(velocity, 1, 127);
        if (!_samples.TryGetValue(note, out var sample)) return;

        var voice = new Voice(sample, velocity / 127f);
        lock (_voiceLock)
        {
            if (_voices.Count >= MaxPolyphony)
            {
                _voices.RemoveAt(0);
            }
            _voices.Add(voice);
        }
    }

    public void NoteOff(int note)
    {
        // Let the phrase finish naturally.
    }

    public void AllNotesOff()
    {
        lock (_voiceLock)
        {
            _voices.Clear();
        }
    }

    public void SetParameter(string name, float value)
    {
        name = name?.Trim() ?? string.Empty;
        if (name.Equals("Rate", StringComparison.OrdinalIgnoreCase)) Rate = value;
        else if (name.Equals("Speed", StringComparison.OrdinalIgnoreCase)) Rate = value;
        else if (name.Equals("Pitch", StringComparison.OrdinalIgnoreCase)) Pitch = value;
        else if (name.Equals("VoicePitch", StringComparison.OrdinalIgnoreCase)) Pitch = value;
        else if (name.Equals("FormantShift", StringComparison.OrdinalIgnoreCase)) FormantShift = value;
        else if (name.Equals("VoiceLevel", StringComparison.OrdinalIgnoreCase)) VoiceLevel = value;
        else if (name.Equals("NoiseLevel", StringComparison.OrdinalIgnoreCase)) NoiseLevel = value;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (count == 0) return 0;
        Array.Clear(buffer, offset, count);

        List<Voice>? voicesCopy = null;
        lock (_voiceLock)
        {
            if (_voices.Count > 0)
            {
                voicesCopy = new List<Voice>(_voices);
            }
        }

        if (voicesCopy == null || voicesCopy.Count == 0) return count;

        int channels = _waveFormat.Channels;
        float leftGain = Volume * (Pan <= 0f ? 1f : 1f - Pan);
        float rightGain = Volume * (Pan >= 0f ? 1f : 1f + Pan);

        int frames = count / channels;
        for (int i = 0; i < frames; i++)
        {
            float mix = 0f;
            for (int v = voicesCopy.Count - 1; v >= 0; v--)
            {
                var voice = voicesCopy[v];
                if (voice.Position >= voice.Sample.Data.Length)
                {
                    voicesCopy.RemoveAt(v);
                    continue;
                }

                mix += voice.Sample.Data[voice.Position++] * voice.Velocity;
            }

            int baseIndex = offset + i * channels;
            if (channels == 1)
            {
                buffer[baseIndex] += mix * Volume;
            }
            else
            {
                buffer[baseIndex] += mix * leftGain;
                buffer[baseIndex + 1] += mix * rightGain;
            }
        }

        lock (_voiceLock)
        {
            _voices.Clear();
            _voices.AddRange(voicesCopy);
        }

        return count;
    }

    private SpeechSample? GenerateSampleWinTts(string text)
    {
#if WINDOWS
        try
        {
            var synth = new SpeechSynthesizer();
            var ssml = BuildSsml(text);
            var stream = synth.SynthesizeSsmlToStreamAsync(ssml).AsTask().GetAwaiter().GetResult();
            using var reader = new WaveFileReader(stream.AsStreamForRead());
            ISampleProvider provider = reader.ToSampleProvider();

            if (provider.WaveFormat.SampleRate != _waveFormat.SampleRate)
            {
                provider = new WdlResamplingSampleProvider(provider, _waveFormat.SampleRate);
            }

            if (provider.WaveFormat.Channels != _waveFormat.Channels)
            {
                if (provider.WaveFormat.Channels == 1 && _waveFormat.Channels == 2)
                {
                    provider = new MonoToStereoSampleProvider(provider);
                }
                else if (provider.WaveFormat.Channels == 2 && _waveFormat.Channels == 1)
                {
                    provider = new StereoToMonoSampleProvider(provider);
                }
            }

            var samples = ReadAllSamples(provider);
            if (samples.Length == 0) return null;
            Normalize(samples);
            return new SpeechSample(samples);
        }
        catch
        {
            return null;
        }
#else
        return null;
#endif
    }

    private SpeechSample? GenerateSampleOffline(string text, int note)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        float basePitch = NoteToFrequency(note) * Pitch;
        int sampleRate = _waveFormat.SampleRate;
        var tokens = Tokenize(text);
        int totalSamples = EstimateSampleLength(tokens, sampleRate);
        if (totalSamples <= 0) return null;

        var data = new float[totalSamples];
        int writePos = 0;

        foreach (var token in tokens)
        {
            var segment = BuildSegment(token, basePitch, sampleRate);
            if (segment.Length == 0) continue;
            if (writePos + segment.Length > data.Length) break;
            Array.Copy(segment, 0, data, writePos, segment.Length);
            writePos += segment.Length;
        }

        if (writePos < data.Length)
        {
            Array.Clear(data, writePos, data.Length - writePos);
        }

        Normalize(data);
        return new SpeechSample(data);
    }

    private int EstimateSampleLength(List<PhonemeSpec> tokens, int sampleRate)
    {
        float rate = Math.Clamp(Rate, 0.2f, 4f);
        float totalSeconds = 0f;
        foreach (var token in tokens)
        {
            totalSeconds += token.Duration;
        }

        totalSeconds /= rate;
        return Math.Max(1, (int)(sampleRate * totalSeconds));
    }

    private float[] BuildSegment(PhonemeSpec phoneme, float basePitch, int sampleRate)
    {
        if (phoneme.Silence)
        {
            int silenceSamples = (int)(sampleRate * phoneme.Duration / Math.Clamp(Rate, 0.2f, 4f));
            return silenceSamples > 0 ? new float[silenceSamples] : Array.Empty<float>();
        }

        float duration = phoneme.Duration / Math.Clamp(Rate, 0.2f, 4f);
        int samples = Math.Max(1, (int)(sampleRate * duration));
        var data = new float[samples];

        float nyquist = sampleRate * 0.5f;
        float f1Start = Math.Clamp(phoneme.F1Start * FormantShift, 60f, nyquist * 0.45f);
        float f2Start = Math.Clamp(phoneme.F2Start * FormantShift, 90f, nyquist * 0.45f);
        float f3Start = Math.Clamp(phoneme.F3Start * FormantShift, 120f, nyquist * 0.45f);
        float f1End = Math.Clamp(phoneme.F1End * FormantShift, 60f, nyquist * 0.45f);
        float f2End = Math.Clamp(phoneme.F2End * FormantShift, 90f, nyquist * 0.45f);
        float f3End = Math.Clamp(phoneme.F3End * FormantShift, 120f, nyquist * 0.45f);

        var res1 = new Resonator(f1Start, phoneme.Bw1, sampleRate);
        var res2 = new Resonator(f2Start, phoneme.Bw2, sampleRate);
        var res3 = new Resonator(f3Start, phoneme.Bw3, sampleRate);

        float attack = 0.004f;
        float release = 0.03f;
        int attackSamples = Math.Max(1, (int)(sampleRate * attack));
        int releaseSamples = Math.Max(1, (int)(sampleRate * release));

        float phase = (float)_random.NextDouble();
        float phaseStep = basePitch / sampleRate;

        for (int i = 0; i < samples; i++)
        {
            float env = 1f;
            if (i < attackSamples) env = i / (float)attackSamples;
            else if (i > samples - releaseSamples)
            {
                env = Math.Max(0f, (samples - i) / (float)releaseSamples);
            }

            float voiced = 0f;
            if (phoneme.Voiced)
            {
                phase += phaseStep;
                if (phase >= 1f) phase -= 1f;
                float ph = phase * MathF.PI * 2f;
                voiced = (MathF.Sin(ph) + 0.45f * MathF.Sin(ph * 2f) + 0.2f * MathF.Sin(ph * 3f)) / 1.65f;
                voiced *= VoiceLevel;
            }

            float noise = 0f;
            if (phoneme.Noise > 0f)
            {
                noise = ((float)_random.NextDouble() * 2f - 1f) * NoiseLevel * phoneme.Noise;
            }

            float t = i / (float)Math.Max(1, samples - 1);
            float f1 = Lerp(f1Start, f1End, t);
            float f2 = Lerp(f2Start, f2End, t);
            float f3 = Lerp(f3Start, f3End, t);

            res1.SetFrequency(f1, phoneme.Bw1, sampleRate);
            res2.SetFrequency(f2, phoneme.Bw2, sampleRate);
            res3.SetFrequency(f3, phoneme.Bw3, sampleRate);

            float excitation = voiced + noise;
            float filtered = res1.Process(excitation) + res2.Process(excitation) + res3.Process(excitation);
            float output = filtered * 0.6f;

            if (phoneme.Noise >= 0.6f)
            {
                output += noise * 0.35f;
            }

            data[i] = output * env;
        }

        return data;
    }

    private static List<PhonemeSpec> Tokenize(string text)
    {
        var normalized = text
            .Replace("\u00E4", "ae")
            .Replace("\u00F6", "oe")
            .Replace("\u00FC", "ue")
            .Replace("\u00C4", "Ae")
            .Replace("\u00D6", "Oe")
            .Replace("\u00DC", "Ue")
            .Replace("\u00DF", "ss");

        var tokens = new List<PhonemeSpec>();
        int i = 0;
        while (i < normalized.Length)
        {
            if (char.IsWhiteSpace(normalized[i]))
            {
                tokens.Add(PhonemeSpec.SilenceSpec(0.05f));
                i++;
                continue;
            }

            string tri = i + 2 < normalized.Length ? normalized.Substring(i, 3).ToLowerInvariant() : string.Empty;
            string bi = i + 1 < normalized.Length ? normalized.Substring(i, 2).ToLowerInvariant() : string.Empty;
            string uni = normalized[i].ToString().ToLowerInvariant();

            if (tri == "sch")
            {
                tokens.Add(PhonemeSpec.Fricative(0.09f, 300f, 2600f, 4500f, 1f));
                i += 3;
                continue;
            }

            if (bi == "ch")
            {
                tokens.Add(PhonemeSpec.Fricative(0.08f, 400f, 1800f, 3600f, 0.9f));
                i += 2;
                continue;
            }

            if (bi == "sh")
            {
                tokens.Add(PhonemeSpec.Fricative(0.08f, 300f, 2400f, 4200f, 1f));
                i += 2;
                continue;
            }

            if (bi == "th")
            {
                tokens.Add(PhonemeSpec.Fricative(0.07f, 350f, 2000f, 3800f, 0.9f));
                i += 2;
                continue;
            }

            if (bi == "ng")
            {
                tokens.Add(PhonemeSpec.Nasal(0.09f, 300f, 1200f, 2500f));
                i += 2;
                continue;
            }

            if (bi == "ei" || bi == "ai")
            {
                tokens.Add(PhonemeSpec.Diphthong(0.16f, 800f, 1150f, 2900f, 350f, 2000f, 2800f));
                i += 2;
                continue;
            }

            if (bi == "au")
            {
                tokens.Add(PhonemeSpec.Diphthong(0.16f, 800f, 1150f, 2900f, 325f, 700f, 2700f));
                i += 2;
                continue;
            }

            if (bi == "eu" || bi == "oi")
            {
                tokens.Add(PhonemeSpec.Diphthong(0.16f, 450f, 800f, 2830f, 350f, 2000f, 2800f));
                i += 2;
                continue;
            }

            if (bi == "ie")
            {
                tokens.Add(PhonemeSpec.Vowel(0.14f, 350f, 2000f, 2800f));
                i += 2;
                continue;
            }

            if (bi == "aa")
            {
                tokens.Add(PhonemeSpec.Vowel(0.16f, 800f, 1150f, 2900f));
                i += 2;
                continue;
            }

            if (bi == "oo")
            {
                tokens.Add(PhonemeSpec.Vowel(0.16f, 325f, 700f, 2700f));
                i += 2;
                continue;
            }

            tokens.Add(CharToPhoneme(uni[0]));
            i++;
        }

        return tokens;
    }

    private static PhonemeSpec CharToPhoneme(char c)
    {
        if (char.IsWhiteSpace(c))
        {
            return PhonemeSpec.SilenceSpec(0.05f);
        }

        c = char.ToLowerInvariant(c);
        return c switch
        {
            'a' => PhonemeSpec.Vowel(0.12f, 800f, 1150f, 2900f),
            'e' => PhonemeSpec.Vowel(0.11f, 500f, 1700f, 2500f),
            'i' => PhonemeSpec.Vowel(0.10f, 350f, 2000f, 2800f),
            'o' => PhonemeSpec.Vowel(0.12f, 450f, 800f, 2830f),
            'u' => PhonemeSpec.Vowel(0.12f, 325f, 700f, 2700f),
            'y' => PhonemeSpec.Vowel(0.11f, 300f, 2000f, 2800f),
            's' => PhonemeSpec.Fricative(0.07f, 300f, 2300f, 4200f, 1f),
            'f' => PhonemeSpec.Fricative(0.07f, 400f, 1200f, 3000f, 0.8f),
            'h' => PhonemeSpec.Fricative(0.06f, 700f, 1100f, 2500f, 0.6f),
            'm' => PhonemeSpec.Nasal(0.08f, 250f, 1200f, 2100f),
            'n' => PhonemeSpec.Nasal(0.08f, 300f, 1300f, 2200f),
            'l' => PhonemeSpec.Approximant(0.09f, 400f, 1300f, 2400f),
            'r' => PhonemeSpec.Approximant(0.09f, 350f, 1400f, 2400f),
            't' => PhonemeSpec.Fricative(0.05f, 400f, 1700f, 3000f, 0.9f),
            'k' => PhonemeSpec.Fricative(0.05f, 300f, 1800f, 3200f, 0.9f),
            'p' => PhonemeSpec.Fricative(0.05f, 500f, 1500f, 2800f, 0.9f),
            'b' => PhonemeSpec.VoicedConsonant(0.05f, 400f, 1200f, 2600f),
            'd' => PhonemeSpec.VoicedConsonant(0.05f, 450f, 1700f, 2600f),
            'g' => PhonemeSpec.VoicedConsonant(0.05f, 350f, 1500f, 2600f),
            _ => PhonemeSpec.VoicedConsonant(0.08f, 500f, 1500f, 2500f)
        };
    }

    private static float NoteToFrequency(int note)
    {
        return 440f * (float)Math.Pow(2.0, (note - 69) / 12.0);
    }

    private static float[] ReadAllSamples(ISampleProvider provider)
    {
        var samples = new List<float>();
        var buffer = new float[provider.WaveFormat.SampleRate * provider.WaveFormat.Channels];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                samples.Add(buffer[i]);
            }
        }

        return samples.ToArray();
    }

    private string BuildSsml(string text)
    {
        var rate = Math.Clamp(Rate, 0.5f, 2.5f);
        var ratePercent = (int)Math.Clamp(rate * 100f, 50f, 200f);
        var semitone = (float)(12.0 * Math.Log(Pitch <= 0f ? 1f : Pitch, 2.0));
        semitone = Math.Clamp(semitone, -12f, 12f);
        var pitchText = semitone >= 0 ? $"+{semitone:0.#}st" : $"{semitone:0.#}st";
        const string lang = "de-DE";

        return $"""
            <speak version="1.0" xml:lang="{lang}">
              <prosody rate="{ratePercent}%" pitch="{pitchText}">{EscapeSsml(text)}</prosody>
            </speak>
            """;
    }

    private static string EscapeSsml(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }

    private static void Normalize(float[] data)
    {
        float max = 0f;
        for (int i = 0; i < data.Length; i++)
        {
            float abs = MathF.Abs(data[i]);
            if (abs > max) max = abs;
        }

        if (max <= 0f) return;
        float scale = 0.8f / max;
        for (int i = 0; i < data.Length; i++)
        {
            data[i] *= scale;
        }
    }

    private sealed class SpeechSample
    {
        public SpeechSample(float[] data)
        {
            Data = data;
        }

        public float[] Data { get; }
    }

    private sealed class Voice
    {
        public Voice(SpeechSample sample, float velocity)
        {
            Sample = sample;
            Velocity = velocity;
            Position = 0;
        }

        public SpeechSample Sample { get; }
        public float Velocity { get; }
        public int Position { get; set; }
    }

    private readonly struct PhonemeSpec
    {
        public PhonemeSpec(bool voiced, float duration, float f1Start, float f2Start, float f3Start, float f1End,
            float f2End, float f3End, float bw1, float bw2, float bw3, float noise, bool silence = false)
        {
            Voiced = voiced;
            Duration = duration;
            F1Start = f1Start;
            F2Start = f2Start;
            F3Start = f3Start;
            F1End = f1End;
            F2End = f2End;
            F3End = f3End;
            Bw1 = bw1;
            Bw2 = bw2;
            Bw3 = bw3;
            Noise = noise;
            Silence = silence;
        }

        public bool Voiced { get; }
        public float Duration { get; }
        public float F1Start { get; }
        public float F2Start { get; }
        public float F3Start { get; }
        public float F1End { get; }
        public float F2End { get; }
        public float F3End { get; }
        public float Bw1 { get; }
        public float Bw2 { get; }
        public float Bw3 { get; }
        public float Noise { get; }
        public bool Silence { get; }

        public static PhonemeSpec SilenceSpec(float duration)
        {
            return new PhonemeSpec(false, duration, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, true);
        }

        public static PhonemeSpec Vowel(float duration, float f1, float f2, float f3)
        {
            return new PhonemeSpec(true, duration, f1, f2, f3, f1, f2, f3, 90f, 120f, 160f, 0.05f);
        }

        public static PhonemeSpec Diphthong(float duration, float f1Start, float f2Start, float f3Start, float f1End,
            float f2End, float f3End)
        {
            return new PhonemeSpec(true, duration, f1Start, f2Start, f3Start, f1End, f2End, f3End, 90f, 120f, 160f,
                0.08f);
        }

        public static PhonemeSpec Fricative(float duration, float f1, float f2, float f3, float noise)
        {
            return new PhonemeSpec(false, duration, f1, f2, f3, f1, f2, f3, 200f, 320f, 500f, noise);
        }

        public static PhonemeSpec Nasal(float duration, float f1, float f2, float f3)
        {
            return new PhonemeSpec(true, duration, f1, f2, f3, f1, f2, f3, 120f, 180f, 220f, 0.1f);
        }

        public static PhonemeSpec Approximant(float duration, float f1, float f2, float f3)
        {
            return new PhonemeSpec(true, duration, f1, f2, f3, f1, f2, f3, 110f, 170f, 210f, 0.08f);
        }

        public static PhonemeSpec VoicedConsonant(float duration, float f1, float f2, float f3)
        {
            return new PhonemeSpec(true, duration, f1, f2, f3, f1, f2, f3, 140f, 220f, 280f, 0.35f);
        }
    }

    private sealed class Resonator
    {
        private float _r;
        private float _c;
        private float _y1;
        private float _y2;

        public Resonator(float frequency, float bandwidth, int sampleRate)
        {
            float freq = Math.Clamp(frequency, 30f, sampleRate * 0.45f);
            float bw = Math.Clamp(bandwidth, 20f, sampleRate * 0.2f);
            float w0 = 2f * MathF.PI * freq / sampleRate;
            _r = MathF.Exp(-MathF.PI * bw / sampleRate);
            _c = 2f * _r * MathF.Cos(w0);
        }

        public void SetFrequency(float frequency, float bandwidth, int sampleRate)
        {
            float freq = Math.Clamp(frequency, 30f, sampleRate * 0.45f);
            float bw = Math.Clamp(bandwidth, 20f, sampleRate * 0.2f);
            float w0 = 2f * MathF.PI * freq / sampleRate;
            float r = MathF.Exp(-MathF.PI * bw / sampleRate);
            if (_r > 0f)
            {
                _y1 *= r / _r;
                _y2 *= (r * r) / (_r * _r);
            }
            _r = r;
            _c = 2f * _r * MathF.Cos(w0);
        }

        public float Process(float x)
        {
            float y = (1f - _r) * x + _c * _y1 - _r * _r * _y2;
            _y2 = _y1;
            _y1 = y;
            return y;
        }
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
