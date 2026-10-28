using System;
using System.IO;
using System.Media;
using System.Threading;

namespace UsageQuest
{
    // 8비트 효과음 (사각파를 직접 만들어 재생)
    static class Sfx
    {
        static byte[] Wav(double[][] notes)
        {
            const int rate = 22050;
            var samples = new System.Collections.Generic.List<short>();
            foreach (var n in notes)
            {
                double f = n[0], dur = n[1];
                int count = (int)(rate * dur);
                for (int i = 0; i < count; i++)
                {
                    double t = (double)i / rate;
                    double env = Math.Exp(-5.0 * t / dur);
                    double v = Math.Sin(2 * Math.PI * f * t) >= 0 ? 1 : -1;
                    samples.Add((short)(v * env * 2600));
                }
            }
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                int dataLen = samples.Count * 2;
                w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + dataLen);
                w.Write(new[] { 'W', 'A', 'V', 'E' }); w.Write(new[] { 'f', 'm', 't', ' ' });
                w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(dataLen);
                foreach (var s in samples) w.Write(s);
                w.Flush();
                return ms.ToArray();
            }
        }

        static void Play(double[][] notes)
        {
            if (!Settings.GetBool("sound", false)) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { using (var p = new SoundPlayer(new MemoryStream(Wav(notes)))) p.PlaySync(); } catch { }
            });
        }

        public static void Blip() { Play(new[] { new[] { 880, 0.06 }, new[] { 1320, 0.08 } }); }
        public static void Heal() { Play(new[] { new[] { 523, 0.08 }, new[] { 659, 0.08 }, new[] { 784, 0.08 }, new[] { 1047, 0.16 } }); }
        public static void Hurt() { Play(new[] { new[] { 220, 0.12 }, new[] { 165, 0.18 } }); }
        public static void Force() { if (Settings.GetBool("sound", false)) Blip(); }
    }
}
