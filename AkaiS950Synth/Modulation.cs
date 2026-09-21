using System;

namespace AkaiS950Synth
{
    /// <summary>
    /// The techniques that are not just a list of harmonics: ring modulation, frequency
    /// modulation, and pulse width.
    ///
    /// Each is written as a function of phase through one cycle and handed to
    /// Spectrum.Analyse, which measures what it contains and throws away what will not fit
    /// under half the sample rate. That is what makes them safe here: FM in particular
    /// produces sidebands reaching far past Nyquist, and written straight to samples they
    /// would fold back down as a whine welded into the audio.
    ///
    /// WHY THE RATIOS ARE WHOLE NUMBERS
    ///
    /// A modulator at 2.5 times the carrier does not repeat until two cycles have passed,
    /// so a wave analysed over one cycle is not the wave that would be played, and a loop
    /// one cycle long would not join. Whole ratios keep everything periodic at the
    /// fundamental - which also means every sideband lands on a harmonic, so these stay
    /// musical rather than clangorous. The inharmonic, bell-like end of ring modulation
    /// would need an analysis window several cycles long; that is a later job.
    /// </summary>
    internal static class Modulation
    {
        /// <summary>A plain sine at a whole multiple of the fundamental.</summary>
        static double Partial(double phase, double ratio)
        {
            return Math.Sin(2.0 * Math.PI * ratio * phase);
        }

        /// <summary>
        /// Ring modulation: two waves multiplied.
        ///
        /// The output holds the sum and difference of every pair of partials and none of
        /// the originals - which is why it sounds hollow and metallic rather than like
        /// either of the things that went into it.
        /// </summary>
        public static Spectrum Ring(Waveforms.Harmonic carrier, int carrierPartials,
                                    double ratio, int harmonics)
        {
            return Spectrum.Analyse(phase =>
            {
                double c = 0;
                for (int n = 1; n <= carrierPartials; n++)
                {
                    double amp = carrier(n);
                    if (amp != 0) c += amp * Partial(phase, n);
                }

                return c * Partial(phase, ratio);
            }, harmonics);
        }

        /// <summary>
        /// Frequency modulation, done as phase modulation - which is what every FM
        /// synthesiser ever built actually did, because modulating phase keeps the pitch
        /// where it was put.
        ///
        /// <paramref name="index"/> is how far the phase is pushed, in radians. It decides
        /// how many sidebands there are and so how bright the result is: zero is a pure
        /// sine, and each step up adds another pair of partials either side of the carrier.
        /// </summary>
        public static Spectrum Fm(double ratio, double index, int harmonics)
        {
            return Spectrum.Analyse(phase =>
                Math.Sin(2.0 * Math.PI * phase + index * Partial(phase, ratio)),
                harmonics);
        }

        /// <summary>
        /// Two modulators into one carrier, which is where FM starts sounding like an
        /// instrument rather than like a demonstration of FM.
        /// </summary>
        public static Spectrum Fm2(double ratioA, double indexA,
                                   double ratioB, double indexB, int harmonics)
        {
            return Spectrum.Analyse(phase =>
                Math.Sin(2.0 * Math.PI * phase
                         + indexA * Partial(phase, ratioA)
                         + indexB * Partial(phase, ratioB)),
                harmonics);
        }

        /// <summary>
        /// A rectangular wave of a given duty, analysed rather than summed.
        ///
        /// The additive form of this is exact and is in Waveforms; this exists so a duty
        /// SWEEP can be built the same way as everything else - a table of these at several
        /// widths, morphed between, is pulse width modulation.
        /// </summary>
        public static Spectrum Pulse(double duty, int harmonics)
        {
            return Spectrum.Analyse(phase => phase < duty ? 1.0 : -1.0, harmonics);
        }

        /// <summary>
        /// A carrier whose own shape is modulated - sync-like, without the sync.
        ///
        /// The phase is bent through a power curve before the wave is taken from it, which
        /// crowds the cycle into one end. Sweeping the amount is the sound of an oscillator
        /// being wound up, and it stays perfectly periodic while it happens.
        /// </summary>
        public static Spectrum Bent(Waveforms.Harmonic shape, int shapePartials,
                                    double amount, int harmonics)
        {
            double power = Math.Pow(2.0, amount);      // 1 is untouched, higher crowds it

            return Spectrum.Analyse(phase =>
            {
                double bent = Math.Pow(phase, power);

                double v = 0;
                for (int n = 1; n <= shapePartials; n++)
                {
                    double amp = shape(n);
                    if (amp != 0) v += amp * Partial(bent, n);
                }
                return v;
            }, harmonics);
        }

        /// <summary>
        /// Phase distortion: one half of the cycle narrower than the other.
        ///
        /// The wave still takes exactly one cycle and still ends where it began - what
        /// changes is how fast it gets there. Phase runs to the halfway point in the first
        /// <paramref name="skew"/> of the time and takes the rest of the cycle over the
        /// second half, so a sine squashed to one side grows a steep edge where the two
        /// rates meet, and with it a stack of harmonics that were not there before.
        ///
        /// This is how the Casio CZ line made a filter sweep without having a filter: at a
        /// skew of a half nothing is distorted, and winding it towards either end brightens
        /// the tone continuously. Sweeping it across a wavetable is that sweep, built into
        /// the sample.
        ///
        /// Pitch is untouched, which is what separates this from simply playing the wave
        /// faster: both halves still add up to one cycle.
        /// </summary>
        public static Spectrum PhaseDistort(Waveforms.Harmonic shape, int shapePartials,
                                            double skew, int harmonics)
        {
            // Never all the way to an end: at zero the first half would have no time at all
            // to happen in, which is a vertical edge and infinite harmonics.
            double d = skew < 0.04 ? 0.04 : (skew > 0.96 ? 0.96 : skew);

            return Spectrum.Analyse(phase =>
            {
                double p = phase < d
                    ? 0.5 * (phase / d)
                    : 0.5 + 0.5 * ((phase - d) / (1.0 - d));

                double v = 0;
                for (int n = 1; n <= shapePartials; n++)
                {
                    double amp = shape(n);
                    if (amp != 0) v += amp * Partial(p, n);
                }
                return v;
            }, harmonics);
        }
    }
}
