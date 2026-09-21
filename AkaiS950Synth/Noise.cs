using System;

namespace AkaiS950Synth
{
    /// <summary>
    /// Noise, in two quite different jobs: as a sound, and as something to shake a sound
    /// with.
    ///
    /// WHY NOISE CAN LOOP AT ALL
    ///
    /// Noise is the one thing that does not obviously belong in a looped sample - it is
    /// defined by not repeating, and a loop is nothing but repetition. What makes it work
    /// here is that a spectrum of harmonics with RANDOM PHASES is noise to the ear while
    /// being perfectly periodic to the machine. Pick amplitudes with the right slope and
    /// phases at random, and the result hisses, loops seamlessly, and is band-limited by
    /// construction - the same three properties every other wave in this program has.
    ///
    /// THE COLOURS
    ///
    /// White is equal power per hertz, so the amplitude of every harmonic is the same.
    /// Pink is equal power per octave - amplitude falling as the square root of frequency -
    /// which is why it sounds balanced rather than hissy. Brown falls twice as fast again
    /// and is the rumble of it.
    /// </summary>
    internal static class Noise
    {
        public enum Colour { White, Pink, Brown }

        /// <summary>Amplitude of harmonic n, for a colour. Power falls as 1/f^k; amplitude as its root.</summary>
        static double Slope(Colour c, int n)
        {
            switch (c)
            {
                case Colour.Pink:  return 1.0 / Math.Sqrt(n);     // power 1/f
                case Colour.Brown: return 1.0 / n;                // power 1/f^2
                default:           return 1.0;                    // flat
            }
        }

        /// <summary>
        /// Noise as a wave: every harmonic present, at the colour's slope, with a random
        /// phase. Periodic, so it loops; band-limited, so it does not alias.
        /// </summary>
        public static Spectrum Spectrum(Colour colour, int seed, int harmonics)
        {
            var rng = new Random(seed);
            var s = new Spectrum(harmonics);

            for (int n = 1; n <= harmonics; n++)
            {
                double amp = Slope(colour, n);
                double phase = rng.NextDouble() * 2.0 * Math.PI;

                s.Cos[n] = amp * Math.Cos(phase);
                s.Sin[n] = amp * Math.Sin(phase);
            }

            return s;
        }

        /// <summary>
        /// A wave with noise stirred into it, at a given amount.
        ///
        /// Not a mix of two sounds - the noise is added to the harmonics themselves, so it
        /// roughens the tone from the inside rather than sitting behind it. At a little it
        /// is the grain of an analogue oscillator; at a lot the pitch is still there but
        /// barely.
        /// </summary>
        public static Spectrum Dusted(Waveforms.Harmonic shape, Colour colour,
                                      double amount, int seed, int harmonics)
        {
            var clean = AkaiS950Synth.Spectrum.FromShape(shape, harmonics);
            var dirt = Spectrum(colour, seed, harmonics);

            // Scaled against the fundamental so "amount" means the same thing whichever
            // shape it is stirred into - a sine and a sawtooth have very different totals.
            double reference = Math.Abs(clean.Sin[1]) + Math.Abs(clean.Cos[1]);
            if (reference <= 0) reference = 1;

            var s = new Spectrum(harmonics);
            for (int n = 1; n <= harmonics; n++)
            {
                s.Cos[n] = clean.Cos[n] + dirt.Cos[n] * amount * reference;
                s.Sin[n] = clean.Sin[n] + dirt.Sin[n] * amount * reference;
            }

            return s;
        }

        /// <summary>
        /// Slow random movement that comes back to where it started.
        ///
        /// For shaking a wave rather than being one: a handful of random control points
        /// smoothed into a wandering line, with the last point being the first, so applying
        /// it across a sample leaves the loop joining exactly as it did before.
        ///
        /// This is what makes noise usable as a MODULATOR here. A modulator that did not
        /// return would put a step in the loop once per pass, which is a click - and the
        /// whole reason these waves are worth generating is that they have none.
        /// </summary>
        public static double[] Wander(int seed, int points, Colour colour, int length)
        {
            var rng = new Random(seed);
            var control = new double[points];

            // Brown wanders by accumulating, which is what makes it drift rather than
            // jitter; white and pink are drawn independently each time.
            double walk = 0;
            for (int i = 0; i < points; i++)
            {
                double r = rng.NextDouble() * 2.0 - 1.0;

                if (colour == Colour.Brown) { walk = walk * 0.75 + r * 0.25; control[i] = walk; }
                else if (colour == Colour.Pink) { walk = walk * 0.45 + r * 0.55; control[i] = walk; }
                else control[i] = r;
            }

            // Come home, or the loop steps.
            control[points - 1] = control[0];

            double peak = 0;
            foreach (double v in control) peak = Math.Max(peak, Math.Abs(v));
            if (peak > 0) for (int i = 0; i < points; i++) control[i] /= peak;

            var outp = new double[length];
            for (int i = 0; i < length; i++)
            {
                double pos = i / (double)length * (points - 1);
                int at = (int)pos;
                double frac = pos - at;

                double a = control[at];
                double b = control[Math.Min(at + 1, points - 1)];

                // Cosine interpolation: no corners, so the modulation has no edges of its
                // own to be heard as ticks.
                double m = (1.0 - Math.Cos(frac * Math.PI)) * 0.5;
                outp[i] = a * (1.0 - m) + b * m;
            }

            return outp;
        }
    }

    /// <summary>
    /// How a wave is shaken as it is rendered.
    ///
    /// Amplitude and pitch, separately or together. Both are driven by a wandering line
    /// that returns to its start, so neither disturbs the loop.
    /// </summary>
    internal sealed class Shake
    {
        public double AmDepth;              // 0 to 1, how far the level moves
        public double PitchDepth;           // in radians of phase, at the fundamental
        public int Points = 24;             // how many random turns across the sample
        public Noise.Colour Colour = Noise.Colour.Pink;
        public int Seed = 1;

        public bool Any { get { return AmDepth > 0 || PitchDepth > 0; } }
    }
}
