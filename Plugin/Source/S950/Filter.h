#pragma once

#include <cmath>
#include <algorithm>

namespace s950
{
    /*
     * The S950's low-pass: 6th-order Butterworth, 36 dB per octave.
     *
     * Three biquad sections in cascade, each an RBJ low-pass at its own Q. The Q values are
     * what make the six poles Butterworth rather than three identical resonant sections,
     * and they are exact rather than measured: only where the cutoff sits in hertz had to be
     * found by recording (see Cal).
     *
     * A straight port of AkaiS950Engine/Filter.cs. The coefficients are recomputed every few
     * dozen samples, far finer than the ear resolves, and the state is kept across a retune
     * so a moving cutoff does not click.
     */
    class Butterworth
    {
    public:
        /// Forget the past - called when a voice starts, so no note begins mid-tail.
        void reset()
        {
            for (int s = 0; s < Sections; ++s)
                x1[s] = x2[s] = y1[s] = y2[s] = 0.0;
        }

        /// Retune to a cutoff in hertz. State is kept, so there is no click.
        void setCutoff (double fc, double fs)
        {
            const double nyq = fs * 0.5;
            const double f   = fc < 10 ? 10 : (fc > nyq * 0.995 ? nyq * 0.995 : fc);

            const double w  = 2.0 * 3.14159265358979323846 * f / fs;
            const double cw = std::cos (w);
            const double sw = std::sin (w);

            for (int s = 0; s < Sections; ++s)
            {
                const double al = sw / (2.0 * q (s));
                const double a0 = 1.0 + al;

                b0[s] = (1.0 - cw) / 2.0 / a0;
                b1[s] = (1.0 - cw) / a0;
                b2[s] = b0[s];
                a1[s] = -2.0 * cw / a0;
                a2[s] = (1.0 - al) / a0;
            }
        }

        /// One sample through all three sections.
        double process (double x)
        {
            for (int s = 0; s < Sections; ++s)
            {
                const double y = b0[s] * x + b1[s] * x1[s] + b2[s] * x2[s]
                                           - a1[s] * y1[s] - a2[s] * y2[s];
                x2[s] = x1[s]; x1[s] = x;
                y2[s] = y1[s]; y1[s] = y;
                x = y;
            }
            return x;
        }

    private:
        static constexpr int Sections = 3;

        /*
         * The Q of each section, which is what makes the cascade Butterworth.
         *
         * Worked out on the spot rather than held in a static table: three cosines, only
         * when a cutoff changes, is nothing beside the filtering itself - and a function
         * needs no thought about when a static gets initialised.
         */
        static double q (int k)
        {
            return 1.0 / (2.0 * std::cos (3.14159265358979323846 * (2 * k + 1) / 12.0));
        }

        double b0[Sections] {}, b1[Sections] {}, b2[Sections] {};
        double a1[Sections] {}, a2[Sections] {};
        double x1[Sections] {}, x2[Sections] {};
        double y1[Sections] {}, y2[Sections] {};
    };
}
