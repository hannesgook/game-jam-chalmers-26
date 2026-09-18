using UnityEngine;

namespace SparvagnRush.Gameplay
{
    public sealed class TramAudio : MonoBehaviour
    {
        private TramController tram;
        private AudioSource engine;
        private AudioSource ambience;
        private AudioClip successClip;

        public void Initialize(TramController controller)
        {
            tram = controller;
            engine = gameObject.AddComponent<AudioSource>();
            engine.clip = MakeEngineLoop();
            engine.loop = true;
            engine.volume = 0.07f;
            engine.spatialBlend = 0.35f;
            engine.Play();

            ambience = gameObject.AddComponent<AudioSource>();
            ambience.clip = MakeAmbientLoop();
            ambience.loop = true;
            ambience.volume = 0.1f;
            ambience.Play();
            successClip = MakeSuccessChime();
        }

        public void PlaySuccess() => ambience.PlayOneShot(successClip, 0.65f);

        private void Update()
        {
            if (tram == null || engine == null) return;
            float speed01 = Mathf.Clamp01(Mathf.Abs(tram.Speed) / 24f);
            engine.pitch = Mathf.Lerp(0.7f, 1.65f, speed01);
            engine.volume = Mathf.Lerp(0.035f, 0.13f, speed01);
        }

        private static AudioClip MakeEngineLoop()
        {
            const int sampleRate = 22050;
            var samples = new float[sampleRate];
            for (int i = 0; i < samples.Length; i++)
            {
                float t = i / (float)sampleRate;
                samples[i] = (Mathf.Sin(t * 2f * Mathf.PI * 52f) + Mathf.Sin(t * 2f * Mathf.PI * 104f) * 0.35f) * 0.18f;
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
                foreach (float note in notes) value += Mathf.Sin(t * 2f * Mathf.PI * note);
                samples[i] = value * envelope * 0.025f;
            }
            return CreateClip("CityAmbience", samples, sampleRate);
        }

        private static AudioClip MakeSuccessChime()
        {
            const int sampleRate = 22050;
            var samples = new float[sampleRate];
            for (int i = 0; i < samples.Length; i++)
            {
                float t = i / (float)sampleRate;
                float frequency = t < 0.25f ? 523.25f : 783.99f;
                samples[i] = Mathf.Sin(t * 2f * Mathf.PI * frequency) * Mathf.Exp(-4f * t) * 0.25f;
            }
            return CreateClip("SuccessChime", samples, sampleRate);
        }

        private static AudioClip CreateClip(string name, float[] samples, int sampleRate)
        {
            AudioClip clip = AudioClip.Create(name, samples.Length, 1, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
