using System;

namespace AkaiS950Engine
{
    /// <summary>
    /// The S950's low-pass: 6th-order Butterworth, 36 dB per octave.
    ///
    /// Three biquad sections in cascade, each an RBJ low-pass at its own Q. The Q values are
    /// what make the six poles Butterworth rather than three identical resonant sections,
    /// and they are exact rather than measured: only where the cutoff sits in hertz had to
    /// be found by recording (see Cal).
    ///
    /// The web version renders this offline into a buffer, because no Web Audio node will
    /// run a cascade with a cutoff that moves. Here it runs a sample at a time, which is
    /// both simpler and closer to the machine - the coefficients are recomputed every few
    /// dozen samples, far finer than the ear resolves.
    /// </summary>
    public sealed class Butterworth
    {
        const int Sections = 3;

        // coefficients, per section
        readonly double[] _b0 = new double[Sections];
        readonly double[] _b1 = new double[Sections];
        readonly double[] _b2 = new double[Sections];
        readonly double[] _a1 = new double[Sections];
        readonly double[] _a2 = new double[Sections];

        // state, per section: two inputs and two outputs back
        readonly double[] _x1 = new double[Sections];
        readonly double[] _x2 = new double[Sections];
        readonly double[] _y1 = new double[Sections];
        readonly double[] _y2 = new double[Sections];

        /// <summary>The Q of each section, which is what makes the cascade Butterworth.</summary>
        static readonly double[] Q = BuildQ();

        static double[] BuildQ()
        {
            var q = new double[Sections];
            for (int k = 0; k < Sections; k++)
                q[k] = 1.0 / (2.0 * Math.Cos(Math.PI * (2 * k + 1) / 12.0));
            return q;
        }

        /// <summary>Forget the past - called when a voice starts, so no note begins mid-tail.</summary>
        public void Reset()
        {
            for (int s = 0; s < Sections; s++)
            {
                _x1[s] = _x2[s] = _y1[s] = _y2[s] = 0;
            }
        }

        /// <summary>Retune to a cutoff in hertz. State is kept, so there is no click.</summary>
        public void SetCutoff(double fc, double fs)
        {
            double nyq = fs * 0.5;
            double f = fc < 10 ? 10 : (fc > nyq * 0.995 ? nyq * 0.995 : fc);

            double w = 2.0 * Math.PI * f / fs;
            double cw = Math.Cos(w), sw = Math.Sin(w);

            for (int s = 0; s < Sections; s++)
            {
                double al = sw / (2.0 * Q[s]);
                double a0 = 1.0 + al;

                _b0[s] = (1.0 - cw) / 2.0 / a0;
                _b1[s] = (1.0 - cw) / a0;
                _b2[s] = _b0[s];
                _a1[s] = -2.0 * cw / a0;
                _a2[s] = (1.0 - al) / a0;
            }
        }

        /// <summary>One sample through all three sections.</summary>
        public double Process(double x)
        {
            for (int s = 0; s < Sections; s++)
            {
                double y = _b0[s] * x + _b1[s] * _x1[s] + _b2[s] * _x2[s]
                                      - _a1[s] * _y1[s] - _a2[s] * _y2[s];
                _x2[s] = _x1[s]; _x1[s] = x;
                _y2[s] = _y1[s]; _y1[s] = y;
                x = y;
            }
            return x;
        }
    }
}
