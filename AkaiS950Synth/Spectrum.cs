using System;

namespace AkaiS950Synth
{
    /// <summary>
    /// One waveform, described by what is in it rather than by its shape.
    ///
    /// Everything this program generates ends up here first: a list of harmonic amplitudes
    /// and phases. Additive shapes are written straight down; ring modulation, FM and pulse
    /// widths are worked out in the time domain and then ANALYSED into this - which is what
    /// band-limits them.
    ///
    /// WHY ANALYSE RATHER THAN JUST WRITE THE SAMPLES
    ///
    /// Frequency modulation produces sidebands. Ring modulation produces sums and
    /// differences. Both reach well past half the sample rate, and anything up there folds
    /// back down as a whine that is IN the sample and can never be filtered out. Working
    /// the wave out at high resolution, measuring what harmonics it actually contains, and
    /// then rebuilding it from the ones that fit gives exactly the intended sound minus the
    /// part that would have aliased.
    ///
    /// It is exact rather than approximate because the signal is periodic and the analysis
    /// window is a whole number of periods. There is no window function and no leakage: the
    /// correlation over one period IS the Fourier coefficient.
    /// </summary>
    internal sealed class Spectrum
    {
        /// <summary>Cosine and sine coefficients. Index 0 is unused; harmonic n is at n.</summary>
        public readonly double[] Cos;
        public readonly double[] Sin;

        public int Harmonics { get { return Cos.Length - 1; } }

        public Spectrum(int harmonics)
        {
            Cos = new double[harmonics + 1];
            Sin = new double[harmonics + 1];
        }

        /// <summary>An additive shape, written down directly. Sine phase, no analysis needed.</summary>
        public static Spectrum FromShape(Waveforms.Harmonic shape, int harmonics)
        {
            var s = new Spectrum(harmonics);
            for (int n = 1; n <= harmonics; n++) s.Sin[n] = shape(n);
            return s;
        }

        /// <summary>
        /// Measure what is actually in one cycle of a waveform.
        ///
        /// <paramref name="cycle"/> is given a phase from 0 to 1 and returns the sample at
        /// that point. It must be periodic over that range - which is why the modulator
        /// ratios in this program are whole numbers. A ratio of 2.5 would repeat every two
        /// cycles rather than every one, and analysing it over one would measure a wave
        /// that is not the one being played.
        ///
        /// Oversampled because this is a numerical integral: the finer the steps, the
        /// closer the measured coefficient is to the real one. Sixteen points per harmonic
        /// is far more than enough and costs nothing at these sizes.
        /// </summary>
        public static Spectrum Analyse(Func<double, double> cycle, int harmonics)
        {
            int steps = Math.Max(2048, harmonics * 16);
            var s = new Spectrum(harmonics);

            var sample = new double[steps];
            for (int i = 0; i < steps; i++) sample[i] = cycle(i / (double)steps);

            for (int n = 1; n <= harmonics; n++)
            {
                double c = 0, sn = 0;
                double w = 2.0 * Math.PI * n / steps;

                for (int i = 0; i < steps; i++)
                {
                    c  += sample[i] * Math.Cos(w * i);
                    sn += sample[i] * Math.Sin(w * i);
                }

                s.Cos[n] = 2.0 * c / steps;
                s.Sin[n] = 2.0 * sn / steps;
            }

            return s;
        }

        /// <summary>A blend of two spectra, which is what a morph is made of.</summary>
        public static Spectrum Blend(Spectrum a, Spectrum b, double amount)
        {
            int n = Math.Max(a.Harmonics, b.Harmonics);
            var s = new Spectrum(n);

            for (int i = 1; i <= n; i++)
            {
                double ac = i <= a.Harmonics ? a.Cos[i] : 0, as_ = i <= a.Harmonics ? a.Sin[i] : 0;
                double bc = i <= b.Harmonics ? b.Cos[i] : 0, bs = i <= b.Harmonics ? b.Sin[i] : 0;

                s.Cos[i] = ac * (1 - amount) + bc * amount;
                s.Sin[i] = as_ * (1 - amount) + bs * amount;
            }

            return s;
        }

        /// <summary>Drop everything that will not fit under half the sample rate.</summary>
        public Spectrum LimitedTo(int highest)
        {
            var s = new Spectrum(Math.Min(highest, Harmonics));
            for (int n = 1; n <= s.Harmonics; n++) { s.Cos[n] = Cos[n]; s.Sin[n] = Sin[n]; }
            return s;
        }
    }
}
