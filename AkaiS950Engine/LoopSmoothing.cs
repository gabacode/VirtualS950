using System;

namespace AkaiS950Engine
{
    /// <summary>
    /// Making a loop join without a click.
    ///
    /// A loop is a splice: the machine plays to the loop end and jumps back to the start. If
    /// the waveform at those two points does not match, the jump is a step, and a step is a
    /// click. Factory samples are usually looped carefully enough that it does not matter;
    /// anything looped in a hurry, or trimmed since, clicks once every time round.
    ///
    /// WHAT THE MACHINE DOES
    ///
    /// Nothing. The S950 splices, and if the points are bad it clicks too - so this is a
    /// departure from it, and the only one in the engine. It is off by setting the length to
    /// zero, and it is worth turning off when the question is "what did the hardware sound
    /// like" rather than "what do I want to play".
    ///
    /// WHY SNAPPING TO ZERO CROSSINGS IS OFF
    ///
    /// It seems the obvious first move: put both ends on a zero crossing and there is no
    /// step left to hear. It was tried, and across the library it moved the loop points on
    /// 272 of the 324 looped samples and by more than a fiftieth of the loop on 72 of them -
    /// BASS 2G1, whose loop is 688 frames, had its ends moved 82 and 84.
    ///
    /// Moving the two ends by different amounts changes the loop LENGTH, and for a pitched
    /// sample the loop length is what sets the pitch of the looped part. A loop holding a
    /// whole number of cycles stops holding one, so it steps at every wrap - at the loop's
    /// own rate, which is squarely in the audible range. It replaces a click with a tone,
    /// and a tone that is not related to the note is heard as a bell ringing over it.
    ///
    /// A crossfade has none of that cost. It moves nothing: it makes the two sides of the
    /// join actually equal across a few milliseconds instead of hoping they already are.
    /// So that is what is on, and the snapping is kept only because it is occasionally
    /// useful on unpitched material where the loop length means nothing.
    /// </summary>
    public static class LoopSmoothing
    {
        /// <summary>A few milliseconds: long enough to hide a corner, short enough not to smear.</summary>
        public const double DefaultCrossfadeMs = 4.0;

        /// <summary>
        /// Prepare a sound's loop so it joins cleanly.
        ///
        /// Modifies a copy of the audio, not the caller's, because the words are also what
        /// the waveform display draws and nobody wants the picture to change because a note
        /// was played.
        /// </summary>
        public static void Polish(Sound s, double crossfadeMs, bool snapToZero)
        {
            if (s == null || s.Audio == null || !s.Loops) return;
            if (s.LoopTo <= s.LoopFrom || s.LoopTo > s.Audio.Length) return;

            if (snapToZero) Snap(s);
            if (crossfadeMs > 0) Crossfade(s, crossfadeMs);
        }

        // ------------------------------------------------------------------ snapping

        /// <summary>
        /// Nudge both ends to a zero crossing that runs the same way.
        ///
        /// The search is deliberately short. Moving a loop end by a tenth of a second to find
        /// a tidy crossing changes the loop's pitch and length audibly, which is a worse
        /// problem than the one being solved.
        /// </summary>
        static void Snap(Sound s)
        {
            float[] a = s.Audio;
            int window = (int)(s.SourceRate * 0.003);       // three milliseconds
            if (window < 2) return;

            int from = NearestCrossing(a, s.LoopFrom, window, 0);
            if (from < 0) return;

            // the end has to cross the same way as the start, or the slopes do not meet
            int want = Direction(a, from);
            int to = NearestCrossing(a, s.LoopTo, window, want);
            if (to < 0 || to <= from + 16) return;          // nothing usable nearby

            s.LoopFrom = from;
            s.LoopTo = to;
        }

        /// <summary>
        /// The nearest sample where the waveform crosses zero, searching outward.
        ///
        /// <paramref name="want"/> is +1 for a rising crossing, -1 for falling, 0 for either.
        /// Returns -1 when there is none within the window, which happens on material with a
        /// DC offset or on a stretch that never gets near zero.
        /// </summary>
        static int NearestCrossing(float[] a, int at, int window, int want)
        {
            for (int d = 0; d <= window; d++)
            {
                int i = at - d;
                if (i > 0 && i < a.Length && IsCrossing(a, i, want)) return i;

                i = at + d;
                if (d > 0 && i > 0 && i < a.Length && IsCrossing(a, i, want)) return i;
            }
            return -1;
        }

        /*
         * Zero is not exactly zero.
         *
         * A sine that completes whole cycles arrives back at its start as -9.8e-16, not
         * as 0, because that is what sin(8*pi) computes to. Testing the sign strictly
         * therefore misses the one crossing that matters most - the one already sitting
         * exactly where the loop point is - and moves the loop a sample to find the next
         * one. Anything this close to zero is zero.
         */
        const float Silent = 1e-6f;

        static bool IsCrossing(float[] a, int i, int want)
        {
            float prev = a[i - 1], here = a[i];

            bool hereIsZero = here > -Silent && here < Silent;
            bool prevIsZero = prev > -Silent && prev < Silent;
            if (prevIsZero && hereIsZero) return false;             // silence is not a crossing

            if (!hereIsZero &&
                !((prev <= Silent && here >= -Silent) || (prev >= -Silent && here <= Silent)))
                return false;

            if (want == 0) return true;
            return Direction(a, i) == want;
        }

        static int Direction(float[] a, int i)
        {
            return a[i] >= a[i - 1] ? 1 : -1;
        }

        // --------------------------------------------------------------- crossfading

        /// <summary>
        /// Blend the run-up to the loop end into the run-up to the loop start.
        ///
        /// The point is what the LAST faded sample becomes. Fading towards the material just
        /// before the loop start means that by the end of the fade the audio is what would
        /// normally precede LoopFrom - so jumping to LoopFrom continues it exactly, and there
        /// is nothing to click.
        ///
        /// The weights are a raised cosine rather than a straight line. Both sides here are
        /// nearly the same waveform, so the two gains should sum to one - an equal-power fade
        /// would put a 3 dB bump in the middle of every loop - and a cosine gets there with
        /// no corner at either end of the fade either.
        /// </summary>
        static void Crossfade(Sound s, double ms)
        {
            float[] a = s.Audio;

            int n = (int)(s.SourceRate * ms / 1000.0);

            /*
             * Never longer than the run-up there is to fade towards, nor than a quarter of
             * the loop.
             *
             * The library's shortest loop is 45 frames - a millisecond - and a four
             * millisecond fade across that would be the whole loop replaced by a blend of
             * somewhere else. A quarter is the most that can be spent without the loop
             * becoming something other than what it was.
             */
            n = Math.Min(n, s.LoopFrom);
            n = Math.Min(n, (s.LoopTo - s.LoopFrom) / 4);
            if (n < 4) return;

            var copy = new float[a.Length];
            Array.Copy(a, copy, a.Length);

            for (int i = 0; i < n; i++)
            {
                double w = 0.5 - 0.5 * Math.Cos(Math.PI * i / (n - 1));   // 0 .. 1
                int at = s.LoopTo - n + i;
                int other = s.LoopFrom - n + i;
                copy[at] = (float)(a[at] * (1 - w) + a[other] * w);
            }

            s.Audio = copy;
            s.LoopSmoothed = n;
        }
    }
}
