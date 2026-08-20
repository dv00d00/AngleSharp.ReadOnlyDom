using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace AngleSharp.ReadOnlyDom.Streaming.Tokenization;

/// <summary>
/// Stop-byte search for raw text and script data the tokenizer is discarding - no query captures
/// it, so the only thing worth finding is the byte where the discard has to end.
/// </summary>
/// <remarks>
/// Every stop begins with '&lt;', but most '&lt;' bytes in CSS and JavaScript are comparisons
/// rather than markup, so a plain memchr surfaces far more positions than it should. The scan
/// is two unconditional stages over each 128-byte block:
/// <list type="number">
/// <item>
/// Compare against '&lt;', OR the four vectors and take one <c>vpmovmskb</c>. A block with no
/// candidate stops here, and that is what most discarded text is.
/// </item>
/// <item>
/// Otherwise read the same bytes again one position later and compare against the bytes that
/// can follow a stop. ANDing the two comparisons leaves only real prefixes, so
/// <c>x&lt;y</c> is discarded in vector registers rather than one branch at a time.
/// </item>
/// </list>
/// Nothing here is predicted, so no input density is pathological: an empty block always costs
/// stage one, and a block full of comparisons always costs both stages and no scalar work.
/// </remarks>
internal static class Utf8DiscardedTextScanner
{
    private const Int32 BlockSize = 4 * 32;

    /// <summary>
    /// Finds the first '&lt;' that ends discarded raw text (<c>&lt;title&gt;</c>,
    /// <c>&lt;style&gt;</c>, <c>&lt;textarea&gt;</c>), or -1 when the span holds none.
    /// </summary>
    internal static Int32 IndexOfRawTextStop(ReadOnlySpan<Byte> utf8) => IndexOfStop<RawTextStop>(utf8);

    /// <summary>
    /// Finds the first '&lt;' that ends discarded script data, or -1 when the span holds none.
    /// </summary>
    internal static Int32 IndexOfScriptDataStop(ReadOnlySpan<Byte> utf8) => IndexOfStop<ScriptDataStop>(utf8);

    /// <summary>
    /// Decides which '&lt;' bytes are stops. Static abstracts keep the decision inlined into the
    /// bit walk: the JIT specialises the scan per policy, so no branch on text mode survives.
    /// </summary>
    private interface IStopPolicy
    {
        /// <summary>Whether the '&lt;' at <paramref name="position"/> ends the discard.</summary>
        static abstract Boolean IsStop(ReadOnlySpan<Byte> utf8, Int32 position);

        /// <summary>
        /// Lanes whose byte can follow '&lt;' in a stop, read from <paramref name="offset"/> + 1 so
        /// each lane lines up with the candidate it follows. Reads 33 bytes from
        /// <paramref name="offset"/>, so the caller must keep one byte of headroom.
        /// </summary>
        static abstract Vector256<Byte> FollowerMatches(ref Byte source, Int32 offset);

        /// <summary>
        /// Whether a survivor of the follower filter still has to be confirmed. Raw text is fully
        /// decided by the follower, so its survivors are stops; script data still has to separate
        /// "&lt;!--" from a bare "&lt;!".
        /// </summary>
        static abstract Boolean ConfirmSurvivors { get; }

        /// <summary>
        /// Where the scalar scan resumes after a '&lt;' that is not a stop. Only the bytes this
        /// skips over are known not to be '&lt;', so the vector scan can ignore it and test every
        /// candidate instead.
        /// </summary>
        static abstract Int32 ResumeOffset(ReadOnlySpan<Byte> utf8, Int32 position);
    }

    private readonly struct RawTextStop : IStopPolicy
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Boolean IsStop(ReadOnlySpan<Byte> utf8, Int32 position) =>
            // A trailing '<' may complete "</" in the next chunk; the per-byte machine holds it.
            position + 1
                == utf8.Length
            || utf8[position + 1] == (Byte)'/';

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Int32 ResumeOffset(ReadOnlySpan<Byte> utf8, Int32 position) => position + 1;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<Byte> FollowerMatches(ref Byte source, Int32 offset) =>
            Avx2.CompareEqual(Vector256.LoadUnsafe(ref source, (UIntPtr)(offset + 1)), Vector256.Create((Byte)'/'));

        public static Boolean ConfirmSurvivors => false;
    }

    private readonly struct ScriptDataStop : IStopPolicy
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Boolean IsStop(ReadOnlySpan<Byte> utf8, Int32 position)
        {
            if (position + 1 == utf8.Length)
            {
                return true;
            }
            var next = utf8[position + 1];
            if (next == (Byte)'/')
            {
                return true;
            }
            if (next != (Byte)'!')
            {
                return false;
            }
            // "<!" only matters when it completes "<!--"; a split candidate defers to the
            // per-byte machine, which can wait for the next chunk.
            if (position + 2 == utf8.Length)
            {
                return true;
            }
            if (utf8[position + 2] != (Byte)'-')
            {
                return false;
            }
            return position + 3 == utf8.Length || utf8[position + 3] == (Byte)'-';
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Int32 ResumeOffset(ReadOnlySpan<Byte> utf8, Int32 position) =>
            // Reached only for a non-stop, so the follower is known: not '!' skips one byte,
            // "<!" without "--" skips the bytes already read.
            utf8[position + 1] != (Byte)'!'
                ? position + 1
            : utf8[position + 2] != (Byte)'-' ? position + 2
            : position + 3;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<Byte> FollowerMatches(ref Byte source, Int32 offset)
        {
            var follower = Vector256.LoadUnsafe(ref source, (UIntPtr)(offset + 1));
            return Avx2.Or(
                Avx2.CompareEqual(follower, Vector256.Create((Byte)'/')),
                Avx2.CompareEqual(follower, Vector256.Create((Byte)'!'))
            );
        }

        // "<!" is only a stop when "--" follows, so the filter is a superset and each survivor
        // still goes through IsStop.
        public static Boolean ConfirmSurvivors => true;
    }

    private static Int32 IndexOfStop<TStop>(ReadOnlySpan<Byte> utf8)
        where TStop : struct, IStopPolicy
    {
        if (!Avx2.IsSupported || utf8.Length < Vector256<Byte>.Count)
        {
            return IndexOfStopScalar<TStop>(utf8, 0);
        }

        ref var source = ref MemoryMarshal.GetReference(utf8);
        var lessThan = Vector256.Create((Byte)'<');
        // The follower load reaches one byte past the block, so the wide loop stops one byte
        // early and the trailing '<' - which is a stop precisely because nothing follows it -
        // is left to the tail.
        var wideEnd = utf8.Length - BlockSize - 1;
        var vectorEnd = utf8.Length - Vector256<Byte>.Count;
        var offset = 0;

        while (offset <= wideEnd)
        {
            // Stage one: is there a '<' anywhere in these 128 bytes? Most discarded text is
            // blocks where the answer is no, and they pay only this - four compares, three ORs
            // and a single vpmovmskb.
            var matches0 = Avx2.CompareEqual(Vector256.LoadUnsafe(ref source, (UIntPtr)offset), lessThan);
            var matches1 = Avx2.CompareEqual(Vector256.LoadUnsafe(ref source, (UIntPtr)(offset + 32)), lessThan);
            var matches2 = Avx2.CompareEqual(Vector256.LoadUnsafe(ref source, (UIntPtr)(offset + 64)), lessThan);
            var matches3 = Avx2.CompareEqual(Vector256.LoadUnsafe(ref source, (UIntPtr)(offset + 96)), lessThan);

            if (Avx2.MoveMask(Avx2.Or(Avx2.Or(matches0, matches1), Avx2.Or(matches2, matches3))) == 0)
            {
                offset += BlockSize;
                continue;
            }

            // Stage two: the block holds candidates, so re-read it one byte later and keep only
            // the lanes whose follower could start a stop. This is where 'x<y' dies - in vector
            // registers, without a branch, however many of them there are.
            var stops0 = Avx2.And(matches0, TStop.FollowerMatches(ref source, offset));
            var stops1 = Avx2.And(matches1, TStop.FollowerMatches(ref source, offset + 32));
            var stops2 = Avx2.And(matches2, TStop.FollowerMatches(ref source, offset + 64));
            var stops3 = Avx2.And(matches3, TStop.FollowerMatches(ref source, offset + 96));

            var low = Mask(stops0, stops1);
            var high = Mask(stops2, stops3);
            if ((low | high) == 0)
            {
                offset += BlockSize;
                continue;
            }

            if (TStop.ConfirmSurvivors)
            {
                var stop = WalkCandidates<TStop>(utf8, offset, low);
                if (stop >= 0)
                {
                    return stop;
                }
                stop = WalkCandidates<TStop>(utf8, offset + 64, high);
                if (stop >= 0)
                {
                    return stop;
                }
                offset += BlockSize;
                continue;
            }

            return low != 0
                ? offset + BitOperations.TrailingZeroCount(low)
                : offset + 64 + BitOperations.TrailingZeroCount(high);
        }

        while (offset <= vectorEnd)
        {
            var candidates = (UInt64)
                (UInt32)Avx2.MoveMask(Avx2.CompareEqual(Vector256.LoadUnsafe(ref source, (UIntPtr)offset), lessThan));
            var stop = WalkCandidates<TStop>(utf8, offset, candidates);
            if (stop >= 0)
            {
                return stop;
            }
            offset += Vector256<Byte>.Count;
        }

        return IndexOfStopScalar<TStop>(utf8, offset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static UInt64 Mask(Vector256<Byte> low, Vector256<Byte> high) =>
        (UInt32)Avx2.MoveMask(low) | ((UInt64)(UInt32)Avx2.MoveMask(high) << 32);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Int32 WalkCandidates<TStop>(ReadOnlySpan<Byte> utf8, Int32 baseOffset, UInt64 candidates)
        where TStop : struct, IStopPolicy
    {
        while (candidates != 0)
        {
            var position = baseOffset + BitOperations.TrailingZeroCount(candidates);
            if (TStop.IsStop(utf8, position))
            {
                return position;
            }
            candidates &= candidates - 1;
        }
        return -1;
    }

    private static Int32 IndexOfStopScalar<TStop>(ReadOnlySpan<Byte> utf8, Int32 offset)
        where TStop : struct, IStopPolicy
    {
        // Single-byte IndexOf keeps the memchr-speed kernel; the follower check resolves
        // lone '<' bytes locally instead of surfacing each one to the dispatch loop.
        while (true)
        {
            var found = utf8[offset..].IndexOf((Byte)'<');
            if (found < 0)
            {
                return -1;
            }
            var position = offset + found;
            if (TStop.IsStop(utf8, position))
            {
                return position;
            }
            offset = TStop.ResumeOffset(utf8, position);
        }
    }
}
