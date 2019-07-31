using System;
using System.Linq;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using System.Threading.Tasks;
using Omnix.Base;
using Omnix.Base.Extensions;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ReedSolomonBenchmarks")]

namespace Omnix.Algorithms.Correction
{
    // Reed-Solomon Erasure Coding (8-bit values).
    public sealed class ReedSolomon8
    {
        private readonly ReedSolomon8.Math _math;
        private readonly int _k;
        private readonly int _n;
        private readonly BufferPool _bufferPool;
        private readonly byte[] _encMatrix;

        public ReedSolomon8(int k, int n, BufferPool bufferPool)
        {
            _math = new Math();
            _k = k;
            _n = n;
            _bufferPool = bufferPool;
            _encMatrix = _math.CreateEncodeMatrix(k, n);
        }

        internal bool UseSimd
        {
            get => _math.UseSimd;
            set => _math.UseSimd = value;
        }

        public async Task Encode(ReadOnlyMemory<byte>[] sources, int[] index, Memory<byte>[] repairs, int packetLength, int concurrency = 1, CancellationToken token = default)
        {
            if (sources == null)
            {
                throw new ArgumentNullException(nameof(sources));
            }

            if (repairs == null)
            {
                throw new ArgumentNullException(nameof(repairs));
            }

            if (index == null)
            {
                throw new ArgumentNullException(nameof(index));
            }

            await Enumerable.Range(0, repairs.Length).ForEachAsync(row =>
            {
                return Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();

                    // *remember* indices start at 0, k starts at 1.
                    if (index[row] < _k)
                    {
                        // < k, systematic so direct copy.
                        BytesOperations.Copy(sources[index[row]].Span, repairs[row].Span, packetLength);
                    }
                    else
                    {
                        // index[row] >= k && index[row] < n
                        int pos = index[row] * _k;
                        BytesOperations.Zero(repairs[row].Span.Slice(packetLength));

                        for (int col = 0; col < _k; col++)
                        {
                            token.ThrowIfCancellationRequested();

                            _math.AddMul(sources[col].Span, repairs[row].Span, _encMatrix[pos + col], packetLength);
                        }
                    }
                });
            }, concurrency, token, false);
        }

        public async Task Decode(Memory<byte>[] packets, int[] index, int packetLength, int concurrency = 1, CancellationToken token = default)
        {
            if (packets == null)
            {
                throw new ArgumentNullException(nameof(packets));
            }

            if (index == null)
            {
                throw new ArgumentNullException(nameof(index));
            }

            Shuffle(packets, index, _k);

            var decMatrix = _math.CreateDecodeMatrix(_encMatrix, index, _k, _n);

            // do the actual decoding..
            var tempPackets = new byte[_k][];

            await Enumerable.Range(0, _k).ForEachAsync(row =>
            {
                return Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();

                    if (index[row] >= _k)
                    {
                        tempPackets[row] = _bufferPool.GetArrayPool().Rent(packetLength);
                        BytesOperations.Zero(tempPackets[row].AsSpan(0, packetLength));

                        for (int col = 0; col < _k; col++)
                        {
                            token.ThrowIfCancellationRequested();

                            _math.AddMul(packets[col].Span, tempPackets[row], decMatrix[row * _k + col], packetLength);
                        }
                    }
                });
            }, concurrency, token, false);

            token.ThrowIfCancellationRequested();

            // move pkts to their final destination
            for (int row = 0; row < _k; row++)
            {
                if (index[row] >= _k)
                {
                    // only copy those actually decoded.
                    BytesOperations.Copy(tempPackets[row], packets[row].Span, packetLength);
                    index[row] = row;
                    _bufferPool.GetArrayPool().Return(tempPackets[row]);
                }
            }
        }

        private static void Shuffle(Memory<byte>[] packets, int[] index, int k)
        {
            for (int i = 0; i < k;)
            {
                if (index[i] >= k || index[i] == i)
                {
                    i++;
                }
                else
                {
                    // put pkts in the right position (first check for conflicts).
                    int c = index[i];

                    if (index[c] == c)
                    {
                        throw new ArgumentException("Shuffle error at " + i);
                    }

                    // swap(index[c],index[i])
                    int tmp = index[i];
                    index[i] = index[c];
                    index[c] = tmp;

                    // swap(pkts[c],pkts[i])
                    var tmp2 = packets[i];
                    packets[i] = packets[c];
                    packets[c] = tmp2;
                }
            }
        }

        private unsafe class Math
        {
            private readonly int _gfBits = 8;
            private readonly int _gfSize;

            /*
             * To speed up computations, we have tables for logarithm, exponent
             * and inverse of a number. If gfBits <= 8, we use a table for
             * multiplication as well (it takes 64K, no big deal even on a PDA,
             * especially because it can be pre-initialized an put into a ROM!),
             * otherwhise we use a table of logarithms.
             */

            // index->poly form conversion table
            private readonly byte[] _gf_exp;
            // Poly->index form conversion table
            private readonly int[] _gf_log;
            // inverse of field elem.
            private readonly byte[] _inverse;

            /*
             * gf_mul(x,y) multiplies two numbers. If gfBits <= 8, it is much
             * faster to use a multiplication table.
             *
             * USE_GF_MULC, GF_MULC0(c) and GF_ADDMULC(x) can be used when multiplying
             * many numbers by the same constant. In this case the first
             * call sets the constant, and others perform the multiplications.
             * A value related to the multiplication is held in a local variable
             * declared with USE_GF_MULC . See usage in addMul1().
             */
            private byte[][]? _gf_mul_table;

            public Math()
            {
                _gfSize = ((1 << _gfBits) - 1);

                _gf_exp = new byte[2 * _gfSize];
                _gf_log = new int[_gfSize + 1];
                _inverse = new byte[_gfSize + 1];

                this.GenerateGF();
                this.InitMulTable();
            }

            public bool UseSimd { get; set; } = true;

            public void GenerateGF()
            {
                const string primPoly = "101110001";

                byte mask = 1;	// x ** 0 = 1
                _gf_exp[_gfBits] = 0; // will be updated at the end of the 1st loop

                /*
                 * first, generate the (polynomial representation of) powers of \alpha,
                 * which are stored in gf_exp[i] = \alpha ** i .
                 * At the same time build gf_log[gf_exp[i]] = i .
                 * The first gfBits powers are simply bits shifted to the left.
                 */
                for (int i = 0; i < _gfBits; i++, mask <<= 1)
                {
                    _gf_exp[i] = mask;
                    _gf_log[_gf_exp[i]] = i;

                    /*
                     * If primPoly[i] == 1 then \alpha ** i occurs in poly-repr
                     * gf_exp[gfBits] = \alpha ** gfBits
                     */
                    if (primPoly[i] == '1')
                    {
                        _gf_exp[_gfBits] ^= mask;
                    }
                }

                /*
                 * now gf_exp[gfBits] = \alpha ** gfBits is complete, so can als
                 * compute its inverse.
                 */
                _gf_log[_gf_exp[_gfBits]] = _gfBits;

                /*
                 * Poly-repr of \alpha ** (i+1) is given by poly-repr of
                 * \alpha ** i shifted left one-bit and accounting for any
                 * \alpha ** gfBits term that may occur when poly-repr of
                 * \alpha ** i is shifted.
                 */
                mask = (byte)(1 << (_gfBits - 1));

                for (int i = _gfBits + 1; i < _gfSize; i++)
                {
                    if (_gf_exp[i - 1] >= mask)
                    {
                        _gf_exp[i] = (byte)(_gf_exp[_gfBits] ^ ((_gf_exp[i - 1] ^ mask) << 1));
                    }
                    else
                    {
                        _gf_exp[i] = (byte)(_gf_exp[i - 1] << 1);
                    }
                    _gf_log[_gf_exp[i]] = i;
                }

                /*
                 * log(0) is not defined, so use a special value
                 */
                _gf_log[0] = _gfSize;

                // set the extended gf_exp values for fast multiply
                for (int i = 0; i < _gfSize; i++)
                {
                    _gf_exp[i + _gfSize] = _gf_exp[i];
                }

                /*
                 * again special cases. 0 has no inverse. This used to
                 * be initialized to gfSize, but it should make no difference
                 * since noone is supposed to read from here.
                 */
                _inverse[0] = 0;
                _inverse[1] = 1;

                for (int i = 2; i <= _gfSize; i++)
                {
                    _inverse[i] = _gf_exp[_gfSize - _gf_log[i]];
                }
            }

            public void InitMulTable()
            {
                if (_gfBits <= 8)
                {
                    _gf_mul_table = new byte[_gfSize + 1][];

                    for (int i = 0; i < _gfSize + 1; i++)
                    {
                        _gf_mul_table[i] = new byte[_gfSize + 1];
                    }

                    for (int i = 0; i < _gfSize + 1; i++)
                    {
                        for (int j = 0; j < _gfSize + 1; j++)
                        {
                            _gf_mul_table[i][j] = _gf_exp[this.Modnn(_gf_log[i] + _gf_log[j])];
                        }
                    }

                    for (int i = 0; i < _gfSize + 1; i++)
                    {
                        _gf_mul_table[0][i] = _gf_mul_table[i][0] = 0;
                    }
                }
            }

            public byte Modnn(int x)
            {
                while (x >= _gfSize)
                {
                    x -= _gfSize;
                    x = (x >> _gfBits) + (x & _gfSize);
                }

                return (byte)x;
            }

            public byte Mul(byte x, byte y)
            {
                if (_gfBits <= 8)
                {
                    return _gf_mul_table![x][y];
                }
                else
                {
                    if (x == 0 || y == 0)
                    {
                        return 0;
                    }

                    return _gf_exp[_gf_log[x] + _gf_log[y]];
                }
            }

            public static byte[] CreateGFMatrix(int rows, int cols)
            {
                return new byte[rows * cols];
            }

            public void AddMul(ReadOnlySpan<byte> src, Span<byte> dst, byte c, int len)
            {
                // nop, optimize
                if (c == 0)
                {
                    return;
                }

                int i = 0;
                int lim = len;

                // use our multiplication table.
                // Instead of doing gf_mul_table[c,x] for multiply, we'll save
                // the gf_mul_table[c] to a local variable since it is going to
                // be used many times.
                byte[] gf_mulc = _gf_mul_table![c];

                if (this.UseSimd)
                {
                    /*if (Avx2.IsSupported)
                    {
                        const int Unroll = 32;

                        byte* buffer = stackalloc byte[256 / 8];

                        fixed (byte* ptr_dst = dst, ptr_src = src, ptr_gf_mulc = gf_mulc)
                        {
                            for (; i < lim && (lim - i) > Unroll; i += Unroll)
                            {
                                var v1 = Avx.LoadVector256(ptr_dst + i);

                                buffer[0] = ptr_gf_mulc[ptr_src[i + 0]];
                                buffer[1] = ptr_gf_mulc[ptr_src[i + 1]];
                                buffer[2] = ptr_gf_mulc[ptr_src[i + 2]];
                                buffer[3] = ptr_gf_mulc[ptr_src[i + 3]];
                                buffer[4] = ptr_gf_mulc[ptr_src[i + 4]];
                                buffer[5] = ptr_gf_mulc[ptr_src[i + 5]];
                                buffer[6] = ptr_gf_mulc[ptr_src[i + 6]];
                                buffer[7] = ptr_gf_mulc[ptr_src[i + 7]];
                                buffer[8] = ptr_gf_mulc[ptr_src[i + 8]];
                                buffer[9] = ptr_gf_mulc[ptr_src[i + 9]];
                                buffer[10] = ptr_gf_mulc[ptr_src[i + 10]];
                                buffer[11] = ptr_gf_mulc[ptr_src[i + 11]];
                                buffer[12] = ptr_gf_mulc[ptr_src[i + 12]];
                                buffer[13] = ptr_gf_mulc[ptr_src[i + 13]];
                                buffer[14] = ptr_gf_mulc[ptr_src[i + 14]];
                                buffer[15] = ptr_gf_mulc[ptr_src[i + 15]];
                                buffer[16] = ptr_gf_mulc[ptr_src[i + 16]];
                                buffer[17] = ptr_gf_mulc[ptr_src[i + 17]];
                                buffer[18] = ptr_gf_mulc[ptr_src[i + 18]];
                                buffer[19] = ptr_gf_mulc[ptr_src[i + 19]];
                                buffer[20] = ptr_gf_mulc[ptr_src[i + 20]];
                                buffer[21] = ptr_gf_mulc[ptr_src[i + 21]];
                                buffer[22] = ptr_gf_mulc[ptr_src[i + 22]];
                                buffer[23] = ptr_gf_mulc[ptr_src[i + 23]];
                                buffer[24] = ptr_gf_mulc[ptr_src[i + 24]];
                                buffer[25] = ptr_gf_mulc[ptr_src[i + 25]];
                                buffer[26] = ptr_gf_mulc[ptr_src[i + 26]];
                                buffer[27] = ptr_gf_mulc[ptr_src[i + 27]];
                                buffer[28] = ptr_gf_mulc[ptr_src[i + 28]];
                                buffer[29] = ptr_gf_mulc[ptr_src[i + 29]];
                                buffer[30] = ptr_gf_mulc[ptr_src[i + 30]];
                                buffer[31] = ptr_gf_mulc[ptr_src[i + 31]];
                                var v2 = Avx.LoadVector256(buffer);

                                var res = Avx2.Xor(v1, v2);

                                Avx.Store(ptr_dst + i, res);
                            }
                        }
                    }
                    else */
                    if (Sse2.IsSupported)
                    {
                        const int Unroll = 16;

                        byte* buffer = stackalloc byte[128 / 8];

                        fixed (byte* ptr_dst = dst, ptr_src = src, ptr_gf_mulc = gf_mulc)
                        {
                            for (; i < lim && (lim - i) > Unroll; i += Unroll)
                            {
                                var v1 = Sse2.LoadVector128(ptr_dst + i);

                                buffer[0] = ptr_gf_mulc[ptr_src[i + 0]];
                                buffer[1] = ptr_gf_mulc[ptr_src[i + 1]];
                                buffer[2] = ptr_gf_mulc[ptr_src[i + 2]];
                                buffer[3] = ptr_gf_mulc[ptr_src[i + 3]];
                                buffer[4] = ptr_gf_mulc[ptr_src[i + 4]];
                                buffer[5] = ptr_gf_mulc[ptr_src[i + 5]];
                                buffer[6] = ptr_gf_mulc[ptr_src[i + 6]];
                                buffer[7] = ptr_gf_mulc[ptr_src[i + 7]];
                                buffer[8] = ptr_gf_mulc[ptr_src[i + 8]];
                                buffer[9] = ptr_gf_mulc[ptr_src[i + 9]];
                                buffer[10] = ptr_gf_mulc[ptr_src[i + 10]];
                                buffer[11] = ptr_gf_mulc[ptr_src[i + 11]];
                                buffer[12] = ptr_gf_mulc[ptr_src[i + 12]];
                                buffer[13] = ptr_gf_mulc[ptr_src[i + 13]];
                                buffer[14] = ptr_gf_mulc[ptr_src[i + 14]];
                                buffer[15] = ptr_gf_mulc[ptr_src[i + 15]];

                                var v2 = Sse2.LoadVector128(buffer);

                                var res = Sse2.Xor(v1, v2);

                                Sse2.Store(ptr_dst + i, res);
                            }
                        }
                    }
                }

                {
                    const int Unroll = 16; // unroll the loop 16 times.

                    // Not sure if loop unrolling has any real benefit in Java, but
                    // what the hey.
                    for (; i < lim && (lim - i) > Unroll; i += Unroll)
                    {
                        // dst ^= gf_mulc[x] is equal to mult then add (xor == add)

                        dst[i] ^= gf_mulc[src[i]];
                        dst[i + 1] ^= gf_mulc[src[i + 1]];
                        dst[i + 2] ^= gf_mulc[src[i + 2]];
                        dst[i + 3] ^= gf_mulc[src[i + 3]];
                        dst[i + 4] ^= gf_mulc[src[i + 4]];
                        dst[i + 5] ^= gf_mulc[src[i + 5]];
                        dst[i + 6] ^= gf_mulc[src[i + 6]];
                        dst[i + 7] ^= gf_mulc[src[i + 7]];
                        dst[i + 8] ^= gf_mulc[src[i + 8]];
                        dst[i + 9] ^= gf_mulc[src[i + 9]];
                        dst[i + 10] ^= gf_mulc[src[i + 10]];
                        dst[i + 11] ^= gf_mulc[src[i + 11]];
                        dst[i + 12] ^= gf_mulc[src[i + 12]];
                        dst[i + 13] ^= gf_mulc[src[i + 13]];
                        dst[i + 14] ^= gf_mulc[src[i + 14]];
                        dst[i + 15] ^= gf_mulc[src[i + 15]];
                    }
                }

                // final components
                for (; i < lim; i++)
                {
                    dst[i] ^= gf_mulc[src[i]];
                }
            }

            public void MatMul(byte[] a, int aStart, byte[] b, int bStart, byte[] c, int cStart, int n, int k, int m)
            {
                for (int row = 0; row < n; row++)
                {
                    for (int col = 0; col < m; col++)
                    {
                        int posA = row * k;
                        int posB = col;
                        byte acc = 0;

                        for (int i = 0; i < k; i++, posA++, posB += m)
                        {
                            acc ^= this.Mul(a[aStart + posA], b[bStart + posB]);
                        }

                        c[cStart + (row * m + col)] = acc;
                    }
                }
            }

            public void InvertMatrix(byte[] src, int k)
            {
                int[] indxc = new int[k];
                int[] indxr = new int[k];

                // ipiv marks elements already used as pivots.
                int[] ipiv = new int[k];

                byte[] id_row = CreateGFMatrix(1, k);
                //byte[] temp_row = CreateGFMatrix(1, k);

                for (int col = 0; col < k; col++)
                {
                    /*
                     * Zeroing column 'col', look for a non-zero element.
                     * First try on the diagonal, if it fails, look elsewhere.
                     */
                    int irow = -1;
                    int icol = -1;
                    bool foundPiv = false;

                    if (ipiv[col] != 1 && src[col * k + col] != 0)
                    {
                        irow = col;
                        icol = col;
                        foundPiv = true;
                    }

                    if (!foundPiv)
                    {
                    loop1:
                        for (int row = 0; row < k; row++)
                        {
                            if (ipiv[row] != 1)
                            {
                                for (int ix = 0; ix < k; ix++)
                                {
                                    if (ipiv[ix] == 0)
                                    {
                                        if (src[row * k + ix] != 0)
                                        {
                                            irow = row;
                                            icol = ix;
                                            foundPiv = true;
                                            goto loop1;
                                        }
                                    }
                                    else if (ipiv[ix] > 1)
                                    {
                                        throw new ArgumentException("singular matrix");
                                    }
                                }
                            }
                        }
                    }

                    // redundant??? I'm too lazy to figure it out -Justin
                    if (!foundPiv && icol == -1)
                    {
                        throw new ArgumentException("XXX pivot not found!");
                    }

                    // Ok, we've found a pivot by this point, so we can set the
                    // foundPiv variable back to false.  The reason that this is
                    // so shittily laid out is that the original code had goto's :(
                    foundPiv = false;

                    ipiv[icol] = ipiv[icol] + 1;

                    /*
                     * swap rows irow and icol, so afterwards the diagonal
                     * element will be correct. Rarely done, not worth
                     * optimizing.
                     */
                    if (irow != icol)
                    {
                        for (int ix = 0; ix < k; ix++)
                        {
                            // swap 'em
                            byte tmp = src[irow * k + ix];
                            src[irow * k + ix] = src[icol * k + ix];
                            src[icol * k + ix] = tmp;
                        }
                    }

                    indxr[col] = irow;
                    indxc[col] = icol;

                    int pivotRowPos = icol * k;
                    byte c = src[pivotRowPos + icol];

                    if (c == 0)
                    {
                        throw new ArgumentException("singular matrix 2");
                    }

                    if (c != 1)
                    {
                        /* otherwhise this is a NOP */
                        /*
                         * this is done often , but optimizing is not so
                         * fruitful, at least in the obvious ways (unrolling)
                         */
                        c = _inverse[c];
                        src[pivotRowPos + icol] = 1;

                        for (int ix = 0; ix < k; ix++)
                        {
                            src[pivotRowPos + ix] = this.Mul(c, src[pivotRowPos + ix]);
                        }
                    }

                    /*
                     * from all rows, remove multiples of the selected row
                     * to zero the relevant entry (in fact, the entry is not zero
                     * because we know it must be zero).
                     * (Here, if we know that the pivotRowPos is the identity,
                     * we can optimize the addMul).
                     */
                    id_row[icol] = 1;

                    if (!BytesOperations.SequenceEqual(src.AsSpan(pivotRowPos), id_row, k))
                    {
                        for (int p = 0, ix = 0; ix < k; ix++, p += k)
                        {
                            if (ix != icol)
                            {
                                c = src[p + icol];
                                src[p + icol] = 0;
                                this.AddMul(src.AsSpan(pivotRowPos), src.AsSpan(p), c, k);
                            }
                        }
                    }

                    id_row[icol] = 0;
                } // done all columns

                for (int col = k - 1; col >= 0; col--)
                {
                    if (indxr[col] < 0 || indxr[col] >= k)
                    {
                        //System.err.println("AARGH, indxr[col] "+indxr[col]);
                    }
                    else if (indxc[col] < 0 || indxc[col] >= k)
                    {
                        //System.err.println("AARGH, indxc[col] "+indxc[col]);
                    }
                    else
                    {
                        if (indxr[col] != indxc[col])
                        {
                            for (int row = 0; row < k; row++)
                            {
                                // swap 'em
                                byte tmp = src[row * k + indxc[col]];
                                src[row * k + indxc[col]] = src[row * k + indxr[col]];
                                src[row * k + indxr[col]] = tmp;
                            }
                        }
                    }
                }
            }

            public void InvertVandermonde(byte[] src, int k)
            {
                if (k == 1)
                {
                    // degenerate case, matrix must be p^0 = 1
                    return;
                }

                /*
                 * c holds the coefficient of P(x) = Prod (x - p_i), i=0..k-1
                 * b holds the coefficient for the matrix inversion
                 */
                byte[] c = CreateGFMatrix(1, k);
                byte[] b = CreateGFMatrix(1, k);
                byte[] p = CreateGFMatrix(1, k);

                for (int j = 1, i = 0; i < k; i++, j += k)
                {
                    c[i] = 0;
                    p[i] = src[j];    /* p[i] */
                }

                /*
                 * construct coeffs. recursively. We know c[k] = 1 (implicit)
                 * and start P_0 = x - p_0, then at each stage multiply by
                 * x - p_i generating P_i = x P_{i-1} - p_i P_{i-1}
                 * After k steps we are done.
                 */
                c[k - 1] = p[0];	/* really -p(0), but x = -x in GF(2^m) */

                for (int i = 1; i < k; i++)
                {
                    byte p_i = p[i]; /* see above comment */

                    for (int j = k - 1 - (i - 1); j < k - 1; j++)
                    {
                        c[j] ^= this.Mul(p_i, c[j + 1]);
                    }

                    c[k - 1] ^= p_i;
                }

                for (int row = 0; row < k; row++)
                {
                    /*
                     * synthetic division etc.
                     */
                    byte xx = p[row];
                    byte t = 1;
                    b[k - 1] = 1; /* this is in fact c[k] */

                    for (int i = k - 2; i >= 0; i--)
                    {
                        b[i] = (byte)(c[i + 1] ^ this.Mul(xx, b[i + 1]));
                        t = (byte)(this.Mul(xx, t) ^ b[i]);
                    }

                    for (int col = 0; col < k; col++)
                    {
                        src[col * k + row] = this.Mul(_inverse[t], b[col]);
                    }
                }
            }

            public byte[] CreateEncodeMatrix(int k, int n)
            {
                if (k > _gfSize + 1 || n > _gfSize + 1 || k > n)
                {
                    throw new ArgumentException("Invalid parameters n=" + n + ",k=" + k + ",gfSize=" + _gfSize);
                }

                byte[] encMatrix = CreateGFMatrix(n, k);

                /*
                 * The encoding matrix is computed starting with a Vandermonde matrix,
                 * and then transforming it into a systematic matrix.
                 *
                 * fill the matrix with powers of field elements, starting from 0.
                 * The first row is special, cannot be computed with exp. table.
                 */
                byte[] tmpMatrix = CreateGFMatrix(n, k);

                tmpMatrix[0] = 1;

                // first row should be 0's, fill in the rest.
                for (int pos = k, row = 0; row < n - 1; row++, pos += k)
                {
                    for (int col = 0; col < k; col++)
                    {
                        tmpMatrix[pos + col] = _gf_exp[this.Modnn(row * col)];
                    }
                }

                /*
                 * quick code to build systematic matrix: invert the top
                 * k*k vandermonde matrix, multiply right the bottom n-k rows
                 * by the inverse, and construct the identity matrix at the top.
                 */
                // much faster than invertMatrix
                this.InvertVandermonde(tmpMatrix, k);
                this.MatMul(tmpMatrix, k * k, tmpMatrix, 0, encMatrix, k * k, n - k, k, k);

                /*
                 * the upper matrix is I so do not bother with a slow multiply
                 */
                Array.Clear(encMatrix, 0, k * k);

                for (int i = 0, col = 0; col < k; col++, i += k + 1)
                {
                    encMatrix[i] = 1;
                }

                return encMatrix;
            }

            public byte[] CreateDecodeMatrix(byte[] encMatrix, int[] index, int k, int n)
            {
                byte[] matrix = CreateGFMatrix(k, k);

                for (int i = 0, pos = 0; i < k; i++, pos += k)
                {
                    Array.Copy(encMatrix, index[i] * k, matrix, pos, k);
                }

                this.InvertMatrix(matrix, k);

                return matrix;
            }
        }
    }
}
