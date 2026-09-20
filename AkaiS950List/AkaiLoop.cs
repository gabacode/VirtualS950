using System;

namespace AkaiS950List
{
    /// <summary>What <see cref="AkaiDisk.FindLoop"/> settled on.</summary>
    public sealed class LoopChoice
    {
        /// <summary>Words between the restart point and the loop end.</summary>
        public int Length;

        /// <summary>The word the loop restarts at - <see cref="End"/> minus <see cref="Length"/>.</summary>
        public int From;

        /// <summary>The word the loop runs to.</summary>
        public int End;

        /// <summary>
        /// How well the two sides of the join agree, -1 to 1. Above about 0.95 the join is
        /// inaudible on sustained material; below 0.5 the sample has no repeating part and
        /// no loop of it will be clean.
        /// </summary>
        public double Match;

        public double Seconds(int sampleRate)
        {
            return sampleRate > 0 ? (double)Length / sampleRate : 0.0;
        }
    }

    public sealed partial class AkaiDisk
    {
        /// <summary>
        /// A sample's loop mode, changed properly.
        ///
        /// The mode is not just a byte in the header: it decides how many 10-byte
        /// descriptors the sample takes in the table that follows the keygroup arena - a
        /// looping sample takes one more than a one-shot, an alternating one more again -
        /// so changing it moves the descriptor pointer of every sample after it. The
        /// library bears the chain out: 1,002 of its 1,011 consecutive samples point
        /// exactly where the one before them ends.
        ///
        /// Writing 0x1A on its own, which is what the property grid used to do, leaves the
        /// rest of the disk pointing into the wrong records.
        /// </summary>
        public void SetLoopMode(AkaiEntry e, char mode)
        {
            mode = char.ToUpperInvariant(mode);
            if (mode != 'O' && mode != 'L' && mode != 'A')
                throw new ArgumentOutOfRangeException("mode", "a loop mode is O, L or A");

            char was = e.LoopMode;
            if (mode == was) return;

            PokeFile(e, 0x1A, (byte)mode);
            ShiftSampleRam(e.Slot, 0,
                10 * (LoopRecords(e.SampleCount, mode) - LoopRecords(e.SampleCount, was)));

            Modified = true;
            ParseDirectory();
        }

        /// <summary>
        /// Where a sample loops. The machine plays end-length .. end, and the loop start
        /// field is left at 0 in three quarters of the library, so that is what this
        /// writes: the end, the length, and a start of 0. Anything reading a loop honours
        /// max(start, end - length), so a 0 start leaves the length to decide.
        /// </summary>
        public void SetLoop(AkaiEntry e, long end, long length, char mode)
        {
            long n = e.SampleCount;
            if (end > n) end = n;
            if (end < 2) end = 2;
            if (length > end) length = end;
            if (length < 2) length = 2;
            if ((end & 1) != 0 || (length & 1) != 0)
                throw new ArgumentException("a loop end and length are whole words, in pairs");

            PutU32File(e, 0x1C, end);
            PutU32File(e, 0x20, 0);
            PutU32File(e, 0x24, length);
            Modified = true;
            ParseDirectory();

            AkaiEntry now = EntryAt(e.Slot);
            if (mode != '\0') SetLoopMode(now != null ? now : e, mode);
        }

        /// <summary>The entry in a slot, read back after the directory has been parsed again.</summary>
        public AkaiEntry EntryAt(int slot)
        {
            foreach (var e in Entries) if (e.Slot == slot) return e;
            return null;
        }

        void PutU32File(AkaiEntry e, int offset, long value)
        {
            for (int i = 0; i < 4; i++)
                PokeFile(e, offset + i, (byte)((value >> (8 * i)) & 0xFF));
        }

        /// <summary>
        /// Where a sample can loop.
        ///
        /// The S950 holds a loop as an end point and a length and plays end-length .. end
        /// over and over, so the only thing to find is how far back it restarts. The join
        /// the ear hears is the one from the loop end back to that point, so the point to
        /// look for is the one whose approach looks like the approach to the end: a
        /// normalised cross-correlation of the window ending at the loop end against the
        /// window ending at each candidate. Normalised, so a decaying tail is compared on
        /// shape rather than on level.
        ///
        /// Two things this gets wrong if done carelessly, both found by measuring it
        /// against the loops the library shipped with:
        ///
        ///  * the coarse pass has to run on a properly decimated copy - each point the mean
        ///    of D samples, not every Dth sample. Striding aliases, and on bright material
        ///    that turns a clean loop into noise and hides it;
        ///  * the best coarse candidates have to be taken from different hills. Sorting and
        ///    taking the top few gathers up the neighbours of one peak, since adjacent
        ///    candidates score alike, and the exact pass then polishes a single region and
        ///    never looks at the rest.
        ///
        /// With both right it averages 0.881 across the library's 324 looped samples where
        /// the shipped loops average 0.793. See looptest.js in the web version, which is
        /// where this was worked out.
        /// </summary>
        /// <param name="words">The sample, as signed 12-bit words.</param>
        /// <param name="end">The word the loop should run to; the sample's length is usual.</param>
        /// <param name="minLength">The shortest loop worth having, in words.</param>
        public static LoopChoice FindLoop(short[] words, int end, int minLength)
        {
            if (words == null) return null;
            if (end > words.Length) end = words.Length;
            if (minLength < 32) minLength = 32;

            // long enough that a match means the shape agrees, not that a couple of
            // samples happen to
            int win = minLength / 2;
            if (win > 2048) win = 2048;
            if (win < 128) win = 128;

            int lowest = Math.Max(minLength, win);
            int highest = end - win;
            if (end < win * 2 || highest < lowest) return null;

            const int D = 8;
            var small = new float[end / D];
            for (int j = 0; j < small.Length; j++)
            {
                int sum = 0;
                for (int k = 0; k < D; k++) sum += words[j * D + k];
                small[j] = (float)sum / D;
            }

            int smallWin = Math.Max(64, win / D);
            int smallEnd = end / D;

            var lens = new System.Collections.Generic.List<int>();
            var scores = new System.Collections.Generic.List<double>();
            for (int len = lowest; len <= highest; len += D)
            {
                int at = (end - len) / D;
                if (at < smallWin) continue;
                lens.Add(len);
                scores.Add(Score(small, at, smallEnd, smallWin));
            }
            if (lens.Count == 0) return null;

            var order = new int[lens.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, delegate(int a, int b) { return scores[b].CompareTo(scores[a]); });

            // peaks from different hills, not sixteen neighbours of one
            int apart = Math.Max(4 * D, win / 2);
            var keep = new System.Collections.Generic.List<int>();
            for (int i = 0; i < order.Length && keep.Count < 24; i++)
            {
                int len = lens[order[i]];
                bool far = true;
                for (int q = 0; q < keep.Count; q++)
                    if (Math.Abs(keep[q] - len) < apart) { far = false; break; }
                if (far) keep.Add(len);
            }

            int bestLen = -1;
            double bestR = double.NegativeInfinity;
            foreach (int peak in keep)
            {
                for (int len = peak - D; len <= peak + D; len++)
                {
                    if (len < lowest || len > highest) continue;
                    double r = Score(words, end - len, end, win);
                    if (r > bestR) { bestR = r; bestLen = len; }
                }
            }
            if (bestLen < 0) return null;

            // A sample count is a whole number of word pairs, and so is the point a loop
            // restarts at. Round to whichever even length joins better.
            if ((bestLen & 1) != 0)
            {
                double up = bestLen + 1 <= highest ? Score(words, end - bestLen - 1, end, win) : -2;
                double down = bestLen - 1 >= lowest ? Score(words, end - bestLen + 1, end, win) : -2;
                if (up >= down) { bestLen += 1; bestR = up; }
                else { bestLen -= 1; bestR = down; }
            }

            var got = new LoopChoice();
            got.Length = bestLen;
            got.From = end - bestLen;
            got.End = end;
            got.Match = bestR;
            return got;
        }

        /// <summary>Normalised correlation of the windows ending at <paramref name="at"/> and at <paramref name="endAt"/>.</summary>
        static double Score(short[] buf, int at, int endAt, int win)
        {
            double dot = 0, ea = 0, eb = 0;
            for (int i = 0; i < win; i++)
            {
                double x = buf[endAt - win + i], y = buf[at - win + i];
                dot += x * y; ea += x * x; eb += y * y;
            }
            double d = Math.Sqrt(ea * eb);
            return d > 0 ? dot / d : -1;
        }

        static double Score(float[] buf, int at, int endAt, int win)
        {
            double dot = 0, ea = 0, eb = 0;
            for (int i = 0; i < win; i++)
            {
                double x = buf[endAt - win + i], y = buf[at - win + i];
                dot += x * y; ea += x * x; eb += y * y;
            }
            double d = Math.Sqrt(ea * eb);
            return d > 0 ? dot / d : -1;
        }
    }
}
