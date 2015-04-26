﻿/*  
    See https://github.com/Microsoft/CardinalityEstimation.
    The MIT License (MIT)

    Copyright (c) 2015 Microsoft

    Permission is hereby granted, free of charge, to any person obtaining a copy
    of this software and associated documentation files (the "Software"), to deal
    in the Software without restriction, including without limitation the rights
    to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
    copies of the Software, and to permit persons to whom the Software is
    furnished to do so, subject to the following conditions:

    The above copyright notice and this permission notice shall be included in all
    copies or substantial portions of the Software.

    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
    IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
    FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
    AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
    LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
    OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
    SOFTWARE.
*/

namespace CardinalityEstimation
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;

    /// <summary>
    ///     A cardinality estimator for sets of some common types, which uses a HashSet for small cardinalities,
    ///     LinearCounting for medium-range cardinalities and HyperLogLog for large cardinalities.  Based off of the following:
    ///     1. Flajolet et al., "HyperLogLog: the analysis of a near-optimal cardinality estimation algorithm",
    ///     DMTCS proc. AH 2007, <see cref="http://algo.inria.fr/flajolet/Publications/FlFuGaMe07.pdf" />
    ///     2. Heule, Nunkesser and Hall 2013, "HyperLogLog in Practice: Algorithmic Engineering of a State of The Art Cardinality Estimation
    ///     Algorithm",
    ///     <see cref="http://static.googleusercontent.com/external_content/untrusted_dlcp/research.google.com/en/us/pubs/archive/40671.pdf" />
    /// </summary>
    /// <remarks>
    ///     1. This implementation is not thread-safe
    ///     2. It uses the 64-bit Fowler/Noll/Vo-0 FNV-1a hash function, <see cref="http://www.isthe.com/chongo/src/fnv/hash_64a.c" />
    ///     3. Estimation is perfect up to 100 elements, then approximate
    /// </remarks>
    [Serializable]
    public class CardinalityEstimator<T> : ICardinalityEstimator<T>
    {
        /// <summary> Number of bits for indexing HLL substreams - the number of estimators is 2^bitsPerIndex </summary>
        private readonly int bitsPerIndex;

        /// <summary> Number of bits to compute the HLL estimate on </summary>
        private readonly int bitsForHll;

        /// <summary> HLL lookup table size </summary>
        private readonly int m;

        /// <summary> Fixed bias correction factor </summary>
        private readonly double alphaM;

        /// <summary> Threshold determining whether to use LinearCounting or HyperLogLog based on an initial estimate </summary>
        private readonly double subAlgorithmSelectionThreshold;

        /// <summary> Lookup table for the dense representation </summary>
        private byte[] lookupDense;

        /// <summary> Lookup dictionary for the sparse representation </summary>
        private IDictionary<ushort, byte> lookupSparse;

        /// <summary> Max number of elements to hold in the sparse representation </summary>
        private readonly int sparseMaxElements;

        /// <summary> Indicates that the sparse representation is currently used </summary>
        private bool isSparse;

        /// <summary> Set for direct counting of elements </summary>
        private HashSet<ulong> directCount = new HashSet<ulong>();

        /// <summary> Max number of elements to hold in the direct representation </summary>
        private const int DirectCounterMaxElements = 100;

        /// <summary> Convertor used to convert the counted elements into byte[]</summary>
        private readonly IBytesConverter bytesConverter;

        /// <summary>
        ///     C'tor
        /// </summary>
        /// <param name="b">
        ///     Number of bits determining accuracy and memory consumption, in the range [4, 16] (higher = greater accuracy and memory usage).
        ///     For large cardinalities, the standard error is 1.04 * 2^(-b/2), and the memory consumption is bounded by 2^b kilobytes.
        ///     The default value of 14 typically yields 3% error or less across the entire range of cardinalities (usually much less),
        ///     and uses up to ~16kB of memory.  b=4 yields less than ~100% error and uses less than 1kB. b=16 uses up to ~64kB and usually yields 1%
        ///     error or less
        /// </param>
        public CardinalityEstimator(int b = 14, IBytesConverter bytesConverter = null)
        {
            if (b < 4 || b > 16)
            {
                throw new ArgumentOutOfRangeException("b", "Accuracy out of range, legal range is 4 <= b <= 16");
            }

            this.bytesConverter = bytesConverter ?? new DefaultBytesConverter();

            this.bitsPerIndex = b;
            this.bitsForHll = 64 - b;
            this.m = (int) Math.Pow(2, b);
            this.alphaM = GetAlphaM(this.m);
            this.subAlgorithmSelectionThreshold = GetSubAlgorithmSelectionThreshold(b);

            // Init the sparse representation
            this.isSparse = true;
            this.lookupSparse = new Dictionary<ushort, byte>();

            // Each element in the sparse representation takes 15 bytes, and there is some constant overhead
            this.sparseMaxElements = Math.Max(0, this.m/15 - 10);
            // If necessary, switch to the dense representation
            if (this.sparseMaxElements <= 0)
            {
                SwitchToDenseRepresentation();
            }
        }

        public void Add(T element)
        {
            ulong hashCode = GetHashCode(bytesConverter.GetBytes(element));
            AddElementHash(hashCode);
        }

        public ulong Count()
        {
            // If only a few elements have been seen, return the exact count
            if (this.directCount != null)
            {
                return (ulong) this.directCount.Count;
            }

            double zInverse = 0;
            double v = 0;

            if (this.isSparse)
            {
                // calc c and Z's inverse
                foreach (KeyValuePair<ushort, byte> kvp in this.lookupSparse)
                {
                    byte sigma = kvp.Value;
                    zInverse += Math.Pow(2, -sigma);
                }
                v = this.m - this.lookupSparse.Count;
                zInverse += (this.m - this.lookupSparse.Count);
            }
            else
            {
                // calc c and Z's inverse
                for (int i = 0; i < this.m; i++)
                {
                    byte sigma = this.lookupDense[i];
                    zInverse += Math.Pow(2, -sigma);
                    if (sigma == 0)
                    {
                        v++;
                    }
                }
            }

            double e = this.alphaM*this.m*this.m/zInverse;
            if (e <= 5.0*this.m)
            {
                e = BiasCorrection.CorrectBias(e, this.bitsPerIndex);
            }

            double h;
            if (v > 0)
            {
                // LinearCounting estimate
                h = this.m*Math.Log(this.m/v);
            }
            else
            {
                h = e;
            }

            if (h <= this.subAlgorithmSelectionThreshold)
            {
                return (ulong) Math.Round(h);
            }
            return (ulong) Math.Round(e);
        }

        /// <summary>
        ///     Merges the given <paramref name="other" /> CardinalityEstimator instance into this one
        /// </summary>
        /// <param name="other">another instance of CardinalityEstimator</param>
        public void Merge(CardinalityEstimator<T> other)
        {
            if (other == null)
            {
                throw new ArgumentNullException("other");
            }

            if (other.m != this.m)
            {
                throw new ArgumentOutOfRangeException("other",
                    "Cannot merge CardinalityEstimator instances with different accuracy/map sizes");
            }

            if (this.isSparse && other.isSparse)
            {
                // Merge two sparse instances
                foreach (KeyValuePair<ushort, byte> kvp in other.lookupSparse)
                {
                    ushort index = kvp.Key;
                    byte otherRank = kvp.Value;
                    byte thisRank;
                    this.lookupSparse.TryGetValue(index, out thisRank);
                    this.lookupSparse[index] = Math.Max(thisRank, otherRank);
                }

                // Switch to dense if necessary
                if (this.lookupSparse.Count > this.sparseMaxElements)
                {
                    SwitchToDenseRepresentation();
                }
            }
            else
            {
                // Make sure this (target) instance is dense, then merge
                SwitchToDenseRepresentation();
                if (other.isSparse)
                {
                    foreach (KeyValuePair<ushort, byte> kvp in other.lookupSparse)
                    {
                        ushort index = kvp.Key;
                        byte rank = kvp.Value;
                        this.lookupDense[index] = Math.Max(this.lookupDense[index], rank);
                    }
                }
                else
                {
                    for (int i = 0; i < this.m; i++)
                    {
                        this.lookupDense[i] = Math.Max(this.lookupDense[i], other.lookupDense[i]);
                    }
                }
            }

            if (other.directCount != null)
            {
                // Other instance is using direct counter. If this instance is also using direct counter, merge them.
                if (this.directCount != null)
                {
                    this.directCount.UnionWith(other.directCount);
                }
            }
            else
            {
                // Other instance is not using direct counter, make sure this instance doesn't either
                this.directCount = null;
            }
        }

        /// <summary>
        ///     Merges the given CardinalityEstimator instances and returns the result
        /// </summary>
        /// <param name="estimators">Instances of CardinalityEstimator</param>
        /// <returns>The merged CardinalityEstimator</returns>
        public static CardinalityEstimator<T> Merge(IList<CardinalityEstimator<T>> estimators)
        {
            if (!estimators.Any())
            {
                throw new ArgumentException(string.Format("Was asked to merge 0 instances of {0}", typeof (CardinalityEstimator<T>)),
                    "estimators");
            }

            CardinalityEstimator<T> ans = new CardinalityEstimator<T>(estimators[0].bitsPerIndex);
            foreach (CardinalityEstimator<T> estimator in estimators)
            {
                ans.Merge(estimator);
            }

            return ans;
        }

        /// <summary>
        ///     Returns the threshold determining whether to use LinearCounting or HyperLogLog for an estimate. Values are from the supplementary
        ///     material of Huele et al.,
        ///     <see cref="http://docs.google.com/document/d/1gyjfMHy43U9OWBXxfaeG-3MjGzejW1dlpyMwEYAAWEI/view?fullscreen#heading=h.nd379k1fxnux" />
        /// </summary>
        /// <param name="bits">Number of bits</param>
        /// <returns></returns>
        private static double GetSubAlgorithmSelectionThreshold(int bits)
        {
            switch (bits)
            {
                case 4:
                    return 10;
                case 5:
                    return 20;
                case 6:
                    return 40;
                case 7:
                    return 80;
                case 8:
                    return 220;
                case 9:
                    return 400;
                case 10:
                    return 900;
                case 11:
                    return 1800;
                case 12:
                    return 3100;
                case 13:
                    return 6500;
                case 14:
                    return 11500;
                case 15:
                    return 20000;
                case 16:
                    return 50000;
                case 17:
                    return 120000;
                case 18:
                    return 350000;
            }
            throw new ArgumentOutOfRangeException("bits", "Unexpected number of bits (should never happen)");
        }

        /// <summary>
        ///     Adds an element's hash code to the counted set
        /// </summary>
        /// <param name="hashCode">Hash code of the element to add</param>
        private void AddElementHash(ulong hashCode)
        {
            if (this.directCount != null)
            {
                this.directCount.Add(hashCode);
                if (this.directCount.Count > DirectCounterMaxElements)
                {
                    this.directCount = null;
                }
            }

            ushort substream = (ushort) (hashCode >> this.bitsForHll);
            byte sigma = GetSigma(hashCode, this.bitsForHll);
            if (this.isSparse)
            {
                byte prevRank;
                this.lookupSparse.TryGetValue(substream, out prevRank);
                this.lookupSparse[substream] = Math.Max(prevRank, sigma);
                if (this.lookupSparse.Count > this.sparseMaxElements)
                {
                    SwitchToDenseRepresentation();
                }
            }
            else
            {
                this.lookupDense[substream] = Math.Max(this.lookupDense[substream], sigma);
            }
        }

        /// <summary>
        ///     Gets the appropriate value of alpha_M for the given <paramref name="m" />
        /// </summary>
        /// <param name="m">size of the lookup table</param>
        /// <returns>alpha_M for bias correction</returns>
        private static double GetAlphaM(int m)
        {
            switch (m)
            {
                case 16:
                    return 0.673;
                case 32:
                    return 0.697;
                case 64:
                    return 0.709;
                default:
                    return 0.7213/(1 + 1.079/m);
            }
        }

        /// <summary>
        ///     Returns the base-2 logarithm of <paramref name="x" />.
        ///     This implementation is faster than <see cref="Math.Log(double,double)" /> as it avoids input checks
        /// </summary>
        /// <param name="x"></param>
        /// <returns>The base-2 logarithm of <paramref name="x" /></returns>
        public static double Log2(double x)
        {
            const double ln2 = 0.693147180559945309417232121458;
            return Math.Log(x)/ln2;
        }

        /// <summary>
        ///     Computes the 64-bit FNV-1a hash of the given <paramref name="bytes" />, see
        ///     <see cref="http://en.wikipedia.org/wiki/Fowler%E2%80%93Noll%E2%80%93Vo_hash_function" />
        ///     and <see cref="http://www.isthe.com/chongo/src/fnv/hash_64a.c" />
        /// </summary>
        /// <param name="bytes">Text to compute the hash for</param>
        /// <returns>The 64-bit fnv1a hash</returns>
        private static ulong GetHashCode(byte[] bytes)
        {
            const ulong fnv1A64Init = 14695981039346656037;
            const ulong fnv64Prime = 0x100000001b3;
            ulong hash = fnv1A64Init;

            foreach (byte b in bytes)
            {
                /* xor the bottom with the current octet */
                hash ^= b;
                /* multiply by the 64 bit FNV magic prime mod 2^64 */
                hash *= fnv64Prime;
            }

            return hash;
        }

        /// <summary>
        ///     Returns the number of leading zeroes in the <paramref name="bitsToCount" /> highest bits of <paramref name="hash" />, plus one
        /// </summary>
        /// <param name="hash">Hash value to calculate the statistic on</param>
        /// <param name="bitsToCount">Lowest bit to count from <paramref name="hash" /></param>
        /// <returns>The number of leading zeroes in the binary representation of <paramref name="hash" />, plus one</returns>
        internal static byte GetSigma(ulong hash, int bitsToCount)
        {
            byte sigma = 1;
            for (int i = bitsToCount - 1; i >= 0; --i)
            {
                if (((hash >> i) & 1) == 0)
                {
                    sigma++;
                }
                else
                {
                    break;
                }
            }
            return sigma;
        }

        /// <summary>
        ///     Converts this estimator from the sparse to the dense representation
        /// </summary>
        private void SwitchToDenseRepresentation()
        {
            if (!this.isSparse)
            {
                return;
            }

            this.lookupDense = new byte[this.m];
            foreach (KeyValuePair<ushort, byte> kvp in this.lookupSparse)
            {
                int index = kvp.Key;
                this.lookupDense[index] = kvp.Value;
            }
            this.lookupSparse = null;
            this.isSparse = false;
        }
    }
}