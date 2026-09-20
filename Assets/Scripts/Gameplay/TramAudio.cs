using UnityEngine;

namespace SparvagnRush.Gameplay
{
    public sealed class TramAudio : MonoBehaviour
    {
        [Header("Volume")]
        [Range(0f, 1f)] public float masterVolume = 1f;
        [Range(0f, 1f)] public float engineIdleVolume = 0.12f;
        [Range(0f, 1f)] public float engineMaxVolume = 0.4f;
        [Range(0f, 1f)] public float ambienceVolume = 0.25f;
        [Range(0f, 1f)] public float successVolume = 0.6f;
        [Range(0f, 1f)] public float explosionVolume = 0.85f;
        [Range(0f, 1f)] public float hornVolume = 0.55f;

        [Header("Optional recorded impact sounds (generated sounds used when empty)")]
        [SerializeField] private AudioClip explosionSound;
        private readonly System.Collections.Generic.List<AudioClip> generatedImpactClips = new();
        private AudioSource[] impactVoices;
        private int nextImpactVoice;

        private TramController tram;
        private AudioSource engine;
        private AudioSource ambience;
        private AudioSource sfx;
        private AudioClip successClip;
        private AudioClip hornClip;
        private bool initialized;

        public void Initialize(TramController controller)
        {
            tram = controller;
            if (initialized) return; // safe to call more than once
            initialized = true;

            engine = CreateSource(MakeEngineLoop(), true);
            ambience = CreateSource(MakeAmbientLoop(), true);
            sfx = CreateSource(null, false);
            successClip = MakeSuccessChime();
            hornClip = MakeHorn();
            generatedImpactClips.Add(hornClip);
            InitializeImpactSounds();

            ApplyMix(); // set volumes before playing so there's no burst at full volume
            engine.Play();
            ambience.Play();
        }

        public void PlaySuccess()
        {
            if (sfx == null || successClip == null) return;
            sfx.PlayOneShot(successClip, successVolume * masterVolume);
        }

        public void PlayHorn()
        {
            if (sfx == null || hornClip == null) return;
            sfx.PlayOneShot(hornClip, hornVolume * masterVolume);
        }

        // Two reed tones a fourth apart with their odd harmonics left in, which is
        // what makes a horn read as brass rather than as a sine beep.
        private static AudioClip MakeHorn()
        {
            const int rate = 22050;
            const float duration = 0.6f;
            var samples = new float[(int)(rate * duration)];
            float[] roots = { 311.13f, 415.30f };
            for (int i = 0; i < samples.Length; i++)
            {
                float t = i / (float)rate;
                float attack = Mathf.Clamp01(t / 0.018f);
                float release = Mathf.Clamp01((duration - t) / 0.1f);
                float value = 0f;
                foreach (float root in roots)
                    for (int harmonic = 1; harmonic <= 8; harmonic++)
                        value += Mathf.Sin(2f * Mathf.PI * root * harmonic * t) / harmonic;
                samples[i] = Mathf.Clamp(value * 0.13f * attack * release, -0.95f, 0.95f);
            }
            return CreateClip("TramHorn", samples, rate);
        }

        private void InitializeImpactSounds()
        {
            impactVoices = new AudioSource[8];
            for (int i = 0; i < impactVoices.Length; i++) impactVoices[i] = CreateSource(null, false);
            if (explosionSound == null)
            {
                explosionSound = MakeExplosion();
                generatedImpactClips.Add(explosionSound);
            }
        }

        public void PlayExplosion() => PlayImpact(explosionSound, explosionVolume, 1f);

        private void PlayImpact(AudioClip clip, float volume, float pitch)
        {
            if (clip == null || impactVoices == null) return;
            // Bound simultaneous impacts instead of stacking unlimited one-shots.
            // Prefer an idle voice before replacing one.
            AudioSource voice = impactVoices[nextImpactVoice];
            for (int i = 0; i < impactVoices.Length; i++)
                if (!impactVoices[i].isPlaying) { voice = impactVoices[i]; break; }
            nextImpactVoice = (nextImpactVoice + 1) % impactVoices.Length;
            voice.Stop();
            voice.clip = clip;
            voice.pitch = pitch;
            voice.volume = volume * masterVolume;
            voice.Play();
        }

        private static AudioClip MakeExplosion()
        {
            const int rate = 22050;
            var samples = new float[(int)(rate * 1.4f)];
            var noise = new System.Random(718);
            float rumble = 0f, phase = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                float t = i / (float)rate;
                float white = (float)noise.NextDouble() * 2f - 1f;
                rumble += 0.055f * (white - rumble);
                phase += 2f * Mathf.PI * (42f + 95f * Mathf.Exp(-14f * t)) / rate;
                float attack = Mathf.Clamp01(t / 0.003f);
                float tail = Mathf.Clamp01((1.4f - t) / 0.15f);
                samples[i] = Mathf.Clamp(attack * tail * (
                    white * 0.55f * Mathf.Exp(-15f * t) +
                    rumble * 2.2f * Mathf.Exp(-3.5f * t) +
                    Mathf.Sin(phase) * 0.5f * Mathf.Exp(-5f * t)), -0.95f, 0.95f);
            }
            return CreateClip("BuildingExplosion", samples, rate);
        }

        private void OnDestroy()
        {
            foreach (AudioClip clip in generatedImpactClips) if (clip != null) Destroy(clip);
        }

        private void Start()
        {
            // Fallback in case nothing called Initialize: hook up to the TramController on this object.
            if (!initialized && TryGetComponent(out TramController controller))
                Initialize(controller);

            EnsureAudioListener();
        }

        private void Update()
        {
            if (!initialized) return;

            // Restart the loops if something stopped them (audio device change, focus loss, etc.).
            if (!engine.isPlaying) engine.Play();
            if (!ambience.isPlaying) ambience.Play();

            ApplyMix();
        }

        private void ApplyMix()
        {
            float speed01 = tram != null ? Mathf.Clamp01(Mathf.Abs(tram.Speed) / 24f) : 0f;
            engine.pitch = Mathf.Lerp(0.7f, 1.65f, speed01);
            engine.volume = Mathf.Lerp(engineIdleVolume, engineMaxVolume, speed01) * masterVolume;
            ambience.volume = ambienceVolume * masterVolume;
        }

        private AudioSource CreateSource(AudioClip clip, bool loop)
        {
            AudioSource source = gameObject.AddComponent<AudioSource>();
            source.clip = clip;
            source.loop = loop;
            source.playOnAwake = false;
            // Fully 2D: the follow camera sits ~50 units from the tram, so 3D distance falloff
            // can make everything far quieter than the volume settings suggest.
            source.spatialBlend = 0f;
            return source;
        }

        // Without an AudioListener in the scene Unity plays nothing at all.
        private void EnsureAudioListener()
        {
#if UNITY_2022_2_OR_NEWER
            bool hasListener = FindFirstObjectByType<AudioListener>() != null;
#else
            bool hasListener = FindObjectOfType<AudioListener>() != null;
#endif
            if (hasListener) return;

            GameObject host = Camera.main != null ? Camera.main.gameObject : gameObject;
            host.AddComponent<AudioListener>();
        }

        private static AudioClip MakeEngineLoop()
        {
            const int sampleRate = 22050;
            // Exactly 1 second, and every partial is a whole number of Hz, so the loop is seamless.
            var samples = new float[sampleRate];
            for (int i = 0; i < samples.Length; i++)
            {
                float tau = i / (float)sampleRate * 2f * Mathf.PI;
                float hum = Mathf.Sin(tau * 90f) * 0.40f
                          + Mathf.Sin(tau * 180f) * 0.30f
                          + Mathf.Sin(tau * 270f) * 0.22f
                          + Mathf.Sin(tau * 450f) * 0.12f;
                float whine = Mathf.Sin(tau * 900f) * 0.06f * (0.6f + 0.4f * Mathf.Sin(tau * 6f));
                samples[i] = (hum + whine) * 0.55f;
            }
            return CreateClip("TramMotor", samples, sampleRate);
        }

        private static AudioClip MakeAmbientLoop()
        {
            const int sampleRate = 22050;
            const int seconds = 8;
            var samples = new float[sampleRate * seconds];
            float[] notes = { 110f, 138.59f, 164.81f };
            for (int i = 0; i < samples.Length; i++)
            {
                float t = i / (float)sampleRate;
                float envelope = 0.5f - 0.5f * Mathf.Cos(Mathf.PI * 2f * t / seconds);
                float value = 0f;
                foreach (float note in notes)
                {
                    float tau = t * 2f * Mathf.PI * note;
                    // The octave partial keeps the pad audible on small speakers.
                    value += Mathf.Sin(tau) + 0.5f * Mathf.Sin(tau * 2f);
                }
                samples[i] = value * envelope * 0.06f;
            }
            return CreateClip("CityAmbience", samples, sampleRate);
        }

        private static AudioClip MakeSuccessChime()
        {
            const int sampleRate = 22050;
            const float seconds = 1.2f;
            var samples = new float[(int)(sampleRate * seconds)];
            for (int i = 0; i < samples.Length; i++)
            {
                float t = i / (float)sampleRate;
                samples[i] = (ChimeNote(t, 0f, 523.25f) + ChimeNote(t, 0.18f, 783.99f)) * 0.4f;
            }
            return CreateClip("SuccessChime", samples, sampleRate);
        }

        // One bell-like note: quick attack, exponential decay, a soft octave overtone.
        private static float ChimeNote(float t, float start, float frequency)
        {
            float local = t - start;
            if (local < 0f) return 0f;
            float attack = Mathf.Clamp01(local / 0.005f);
            float tone = Mathf.Sin(local * 2f * Mathf.PI * frequency)
                       + 0.3f * Mathf.Sin(local * 4f * Mathf.PI * frequency);
            return tone * attack * Mathf.Exp(-5f * local);
        }

        private static AudioClip CreateClip(string name, float[] samples, int sampleRate)
        {
            AudioClip clip = AudioClip.Create(name, samples.Length, 1, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
