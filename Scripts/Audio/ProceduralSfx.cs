using System;
using Godot;

namespace DungeonCrawler.Audio;

/// <summary>
/// The project ships no imported audio assets. Rather than add binary sound
/// files, short gameplay cues are synthesized as simple enveloped tones at
/// startup — a mono sine (or, for "harsh" cues, a square-ish wave with an
/// overtone) that pitch-slides from a start to an end frequency over a fixed
/// duration, with a fast attack / exponential-ish release envelope so it
/// reads as a percussive blip rather than clicking on/off.
/// </summary>
public static class ProceduralSfx
{
    private const int MixRate = 22050;

    public static AudioStreamWav Tone(float startFrequencyHz, float endFrequencyHz, float durationSeconds, float volume = 0.5f, bool harsh = false)
    {
        int sampleCount = Math.Max(1, (int)(MixRate * durationSeconds));
        var data = new byte[sampleCount * 2]; // 16-bit mono PCM

        for (int i = 0; i < sampleCount; i++)
        {
            float t = i / (float)MixRate;
            float progress = i / (float)Math.Max(sampleCount - 1, 1);
            float frequency = Mathf.Lerp(startFrequencyHz, endFrequencyHz, progress);
            float phase = 2f * Mathf.Pi * frequency * t;

            float sample = harsh
                ? (Mathf.Sign(Mathf.Sin(phase)) * 0.6f) + (Mathf.Sin(phase * 3f) * 0.2f)
                : Mathf.Sin(phase);

            float attack = Mathf.Clamp(progress / 0.05f, 0f, 1f);
            float release = Mathf.Clamp((1f - progress) / 0.3f, 0f, 1f);
            float envelope = Mathf.Min(attack, release);

            short pcm = (short)Mathf.Clamp(sample * envelope * volume * short.MaxValue, short.MinValue, short.MaxValue);
            data[i * 2] = (byte)(pcm & 0xFF);
            data[(i * 2) + 1] = (byte)((pcm >> 8) & 0xFF);
        }

        return new AudioStreamWav
        {
            Data = data,
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = MixRate,
            Stereo = false
        };
    }
}

/// <summary>
/// One synthesized cue per gameplay signal (docs/PROJECT_STATE_AND_NOVELTY.md
/// requested audio "for everything — healing, attack, trap"). Generated once
/// at class load and reused for every play — see GameManager.PlaySfx.
/// </summary>
public static class Sfx
{
    public static readonly AudioStreamWav Attack = ProceduralSfx.Tone(520, 380, 0.09f, 0.35f);
    public static readonly AudioStreamWav Hit = ProceduralSfx.Tone(220, 160, 0.08f, 0.4f);
    public static readonly AudioStreamWav PlayerHurt = ProceduralSfx.Tone(180, 90, 0.18f, 0.45f, harsh: true);
    public static readonly AudioStreamWav EnemyDied = ProceduralSfx.Tone(300, 80, 0.35f, 0.4f);
    public static readonly AudioStreamWav PlayerDeath = ProceduralSfx.Tone(300, 60, 0.7f, 0.5f, harsh: true);
    public static readonly AudioStreamWav Heal = ProceduralSfx.Tone(520, 880, 0.3f, 0.35f);
    public static readonly AudioStreamWav Trap = ProceduralSfx.Tone(200, 140, 0.25f, 0.45f, harsh: true);
    public static readonly AudioStreamWav Chest = ProceduralSfx.Tone(660, 1320, 0.22f, 0.4f);
    public static readonly AudioStreamWav Exit = ProceduralSfx.Tone(440, 1320, 0.6f, 0.45f);
}
