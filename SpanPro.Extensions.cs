using DSO.Core.SpanPro;
#nullable enable
using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace DSO.Core.SpanPro
{
    public static class SpanProExtensions
    {

        // ========== MUTABLE STRING GÖRÜNÜMÜ ==========
        /// <summary>
        /// String içeriğini char[] kopyasına aktarır ve yazılabilir SpanPro<char> döner.
        /// Orijinal string immutable kalır.
        /// </summary>
        public static SpanPro<char> AsSpanProMutable(this string s)
        {
            if (s is null) throw new ArgumentNullException(nameof(s));
            var arr = s.ToCharArray(); // tek tahsis, güvenli
            return SpanPro<char>.FromArray(arr);
        }

        /// <summary>
        /// String'in [start,length] aralığını char[] kopyasına aktarır; yazılabilir SpanPro<char> döner.
        /// </summary>
        public static SpanPro<char> AsSpanProMutable(this string s, int start, int length)
        {
            if (s is null) throw new ArgumentNullException(nameof(s));
            if ((uint)start > (uint)s.Length || (uint)length > (uint)(s.Length - start))
                throw new ArgumentOutOfRangeException();
            var arr = new char[length];
            s.CopyTo(start, arr, 0, length); // ara substring oluşturmadan kopya
            return SpanPro<char>.FromArray(arr);
        }

        public static SpanPro<char> Trim(this SpanPro<char> span)
        {
            int start = 0;
            for (; start < span.Length; start++)
            {
                if (!char.IsWhiteSpace(span[start]))
                {
                    break;
                }
            }

            int end = span.Length - 1;
            for (; end > start; end--)
            {
                if (!char.IsWhiteSpace(span[end]))
                {
                    break;
                }
            }

            return span.Slice(start, end - start + 1);
        }
        public static SpanPro<T> Trim<T>(this SpanPro<T> sp)
        {
            return sp.Trim(default);
        }
        public static SpanPro<T> Trim<T>(this SpanPro<T> sp, T value)
        {
            return sp.TrimStart(value).TrimEnd(value);
        }

        public static SpanPro<T> TrimStart<T>(this SpanPro<T> sp)
        {
            return sp.TrimStart(default);
        }
        public static SpanPro<T> TrimStart<T>(this SpanPro<T> sp, T value)
        {
            var cmp = EqualityComparer<T>.Default;
            int i = 0;
            while (i < sp.Length && cmp.Equals(sp.ItemRefRO(i), value)) i++;
            return sp.Slice(i, sp.Length - i);
        }

        public static SpanPro<T> TrimEnd<T>(this SpanPro<T> sp)
        {
            return sp.TrimEnd(default);
        }
        public static SpanPro<T> TrimEnd<T>(this SpanPro<T> sp, T value)
        {
            var cmp = EqualityComparer<T>.Default;
            int i = sp.Length - 1;
            while (i >= 0 && cmp.Equals(sp.ItemRefRO(i), value)) i--;
            return sp.Slice(0, i + 1);
        }

        public static int IndexOf<T>(this SpanPro<T> sp, T value) => sp.IndexOf(value, 0);
        /// <summary>sp içinde, startIndex'ten itibaren value değerinin ilk indexini döndürür; yoksa -1.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int IndexOf<T>(this SpanPro<T> sp, T value, int startIndex)
        {
            int n = sp.Length;
            if ((uint)startIndex > (uint)n) throw new ArgumentOutOfRangeException(nameof(startIndex));
            if (startIndex == n) return -1;

            int len = n - startIndex;
            var cmp = EqualityComparer<T>.Default;

            // Tek seferlik backing ayrımı: döngü içinde switch yok.
            if (sp.BackingKind == SpanProKind.Array && sp.Owner is T[] arr)
            {
                ref T baseRef = ref MemoryMarshal.GetArrayDataReference(arr);
                ref T r = ref Unsafe.Add(ref baseRef, sp.Start + startIndex);
                for (int i = 0; i < len; i++)
                {
                    if (cmp.Equals(Unsafe.Add(ref r, i), value))
                        return startIndex + i;
                }
                return -1;
            }
            else if (sp.BackingKind == SpanProKind.Unmanaged && sp.Owner is IRawMemoryOwner<T> u)
            {
                nint elemSize = Unsafe.SizeOf<T>();
                nint addr = u.BaseAddress + (nint)(sp.Start + startIndex) * elemSize;
                unsafe
                {
                    for (int i = 0; i < len; i++)
                    {
                        ref T cur = ref Unsafe.AsRef<T>((void*)(addr + (nint)i * elemSize));
                        if (cmp.Equals(cur, value))
                            return startIndex + i;
                    }
                }
                return -1;
            }

            // Diğer/backing belirsiz: güvenli genel yol
            for (int i = startIndex; i < n; i++)
            {
                if (cmp.Equals(sp.ItemRefRO(i), value))
                    return i;
            }
            return -1;
        }

        /// <summary>sp içinde, startIndex'ten itibaren value (alt dizi) ilk geçtiği index; yoksa -1. Boş pattern =&gt; startIndex.</summary>
        public static int IndexOf<T>(this SpanPro<T> sp, SpanPro<T> value, int startIndex)
        {
            int n = sp.Length, m = value.Length;
            if ((uint)startIndex > (uint)n) throw new ArgumentOutOfRangeException(nameof(startIndex));
            if (m == 0) return startIndex;
            if (m > n || startIndex > n - m) return -1;

            // Hızlandırılmış yollar
            if (typeof(T) == typeof(char))
                return IndexOf_BMH_Char(sp, value, startIndex);
            if (typeof(T) == typeof(byte))
                return IndexOf_BMH_Byte(sp, value, startIndex);

            // Naif (genel) arama
            return IndexOf_Naive(sp, value, startIndex);
        }

        /// <summary>sp value içeriyor mu?</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Contains<T>(this SpanPro<T> sp, T value)
            => sp.IndexOf(value, 0) >= 0;

        /// <summary>sp içinde value'nun SON görüldüğü index; yoksa -1.</summary>
        public static int LastIndexOf<T>(this SpanPro<T> sp, T value)
        {
            int n = sp.Length;
            if (n == 0) return -1;
            var cmp = EqualityComparer<T>.Default;

            if (sp.BackingKind == SpanProKind.Array && sp.Owner is T[] arr)
            {
                ref T baseRef = ref MemoryMarshal.GetArrayDataReference(arr);
                ref T r0 = ref Unsafe.Add(ref baseRef, sp.Start);
                for (int i = n - 1; i >= 0; i--)
                {
                    if (cmp.Equals(Unsafe.Add(ref r0, i), value))
                        return i;
                }
                return -1;
            }
            else if (sp.BackingKind == SpanProKind.Unmanaged && sp.Owner is IRawMemoryOwner<T> u)
            {
                nint elemSize = Unsafe.SizeOf<T>();
                nint baseAddr = u.BaseAddress + (nint)sp.Start * elemSize;
                unsafe
                {
                    for (int i = n - 1; i >= 0; i--)
                    {
                        ref T cur = ref Unsafe.AsRef<T>((void*)(baseAddr + (nint)i * elemSize));
                        if (cmp.Equals(cur, value))
                            return i;
                    }
                }
                return -1;
            }

            for (int i = n - 1; i >= 0; i--)
            {
                if (cmp.Equals(sp.ItemRefRO(i), value))
                    return i;
            }
            return -1;
        }

        /// <summary>sp içinde value (alt dizi) SON görüldüğü başlangıç indexi; yoksa -1.</summary>
        public static int LastIndexOf<T>(this SpanPro<T> sp, SpanPro<T> value)
        {
            int n = sp.Length, m = value.Length;
            if (m == 0) return n;
            if (m > n) return -1;

            // Hızlandırılmış yollar
            if (typeof(T) == typeof(char))
                return LastIndexOf_BMH_Char(sp, value, n - 1);
            if (typeof(T) == typeof(byte))
                return LastIndexOf_BMH_Byte(sp, value, n - 1);

            // Naif geriye arama
            return LastIndexOf_Naive(sp, value, n - 1);
        }

        /// <summary>sp içinde, endIndex (dahil) noktasına kadar geriye doğru value'nun SON indexi; yoksa -1.</summary>
        public static int LastIndexOf<T>(this SpanPro<T> sp, T value, int endIndex)
        {
            int n = sp.Length;
            if (n == 0)
            {
                if (endIndex != 0) throw new ArgumentOutOfRangeException(nameof(endIndex));
                return -1;
            }
            if ((uint)endIndex >= (uint)n) throw new ArgumentOutOfRangeException(nameof(endIndex));

            var cmp = EqualityComparer<T>.Default;
            for (int i = endIndex; i >= 0; i--)
            {
                if (cmp.Equals(sp.ItemRefRO(i), value))
                    return i;
            }
            return -1;
        }

        /// <summary>sp içinde, endIndex (dahil) noktasına kadar geriye doğru value (alt dizi) SON görüldüğü index; yoksa -1. Boş pattern =&gt; endIndex.</summary>
        public static int LastIndexOf<T>(this SpanPro<T> sp, SpanPro<T> value, int endIndex)
        {
            int n = sp.Length, m = value.Length;
            if (n == 0)
            {
                if (endIndex != 0) throw new ArgumentOutOfRangeException(nameof(endIndex));
                return m == 0 ? 0 : -1;
            }
            if ((uint)endIndex >= (uint)n) throw new ArgumentOutOfRangeException(nameof(endIndex));

            if (m == 0) return endIndex;
            if (m > n) return -1;

            // Hızlandırılmış yollar
            if (typeof(T) == typeof(char))
                return LastIndexOf_BMH_Char(sp, value, endIndex);
            if (typeof(T) == typeof(byte))
                return LastIndexOf_BMH_Byte(sp, value, endIndex);

            // Naif geriye arama
            return LastIndexOf_Naive(sp, value, endIndex);
        }

        /// <summary>
        /// source içinde, startIndex'ten itibaren value ile eşleşen TÜM indeksleri döndürür.
        /// Bulunamazsa boş dizi döner. EqualityComparer&lt;T&gt;.Default kullanır.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int[] FindAll<T>(this SpanPro<T> source, T value, int startIndex)
        {
            int n = source.Length;
            if ((uint)startIndex > (uint)n)
                throw new ArgumentOutOfRangeException(nameof(startIndex), "startIndex aralık dışında.");

            if (startIndex == n || n == 0)
                return Array.Empty<int>();

            var cmp = EqualityComparer<T>.Default;

            // Küçük bir tahminle başlayıp (n-start)/8 + 4, gerektiğinde ikiye katlayarak büyütüyoruz.
            int estimate = ((n - startIndex) >> 3) + 4;
            if (estimate < 4) estimate = 4;

            int[] buf = ArrayPool<int>.Shared.Rent(estimate);
            int count = 0;

            try
            {
                for (int i = startIndex; i < n; i++)
                {
                    if (cmp.Equals(source.ItemRefRO(i), value))
                    {
                        if (count == buf.Length)
                        {
                            // Büyüt
                            int[] bigger = ArrayPool<int>.Shared.Rent(buf.Length << 1);
                            Array.Copy(buf, 0, bigger, 0, count);
                            ArrayPool<int>.Shared.Return(buf);
                            buf = bigger;
                        }
                        buf[count++] = i;
                    }
                }

                if (count == 0)
                    return Array.Empty<int>();

                var result = new int[count];
                Array.Copy(buf, 0, result, 0, count);
                return result;
            }
            finally
            {
                ArrayPool<int>.Shared.Return(buf);
            }
        }

        // Kalite-of-life: byte için int değer kabul eden overload (örn. 32)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int[] FindAll(this SpanPro<byte> source, int value, int startIndex)
        {
            if ((uint)value > 255)
                throw new ArgumentOutOfRangeException(nameof(value), "byte aralığında olmalı (0..255).");
            return FindAll(source, (byte)value, startIndex);
        }

        /// <summary>
        /// source içinde, value değerinin İLK görüldüğü indexi döndürür.
        /// Bulunamazsa -1 döner.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int FindFirst<T>(this SpanPro<T> source, T value)
        {
            int n = source.Length;
            if (n == 0) return -1;

            var cmp = EqualityComparer<T>.Default;
            for (int i = 0; i < n; i++)
            {
                if (cmp.Equals(source.ItemRefRO(i), value))
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Naif ama overlap-dostu arama (KMP’siz). T için EqualityComparer kullanır.
        /// </summary>
        public static int FindFirst<T>(this SpanPro<T> source, SpanPro<T> value)
        {
            int n = source.Length, m = value.Length;
            if (m == 0) return 0;
            if (m > n) return -1;

            var cmp = EqualityComparer<T>.Default;
            int i = 0, j = 0;

            // n-m+1 pencereden öteye geçme
            while (i < n)
            {
                if (cmp.Equals(source.ItemRefRO(i), value.ItemRefRO(j)))
                {
                    i++; j++;
                    if (j == m) return i - m;
                    // eşleşme uzuyor, devam
                }
                else
                {
                    // pencereyi kaydır: i önceki pencerenin başının 1 ötesine
                    i = i - j + 1;
                    j = 0;

                    // artık kalan uzunluk m’den kısa ise çık
                    if (i + m > n) break;
                }
            }
            return -1;
        }

        public static int FindLast<T>(this SpanPro<T> source, SpanPro<T> value)
        {
            int n = source.Length, m = value.Length;
            if (m == 0) return n;
            if (m > n) return -1;

            var cmp = EqualityComparer<T>.Default;
            // Son başlangıç indexinden geriye doğru ara
            for (int start = n - m; start >= 0; start--)
            {
                int j = 0;
                while (j < m && cmp.Equals(source.ItemRefRO(start + j), value.ItemRefRO(j))) j++;
                if (j == m) return start;
            }
            return -1;
        }

        /// <summary>source içinde value'nun SON görüldüğü index; yoksa -1. (kısa ad)</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int FindLast<T>(this SpanPro<T> source, T value)
            => source.LastIndexOf(value);

        // Basit kopya: 0’dan 0’a kadar kopyalar (eski CopyTo’nun yerini alır)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void CopyTo<T>(this SpanPro<T> src, SpanPro<T> dst)
            => DsMemoryCopySP(src, 0, dst, 0, Math.Min(src.Length, dst.Length));

        public static void CopyTo<T>(this SpanPro<T> src, T[] destination, int destStart)
            => src.CopyTo(destination, destStart, src.Length);

        public static void CopyTo<T>(this SpanPro<T> src, T[] destination, int destStart, int len)
        {
            if (destination is null) throw new ArgumentNullException(nameof(destination));
            if ((uint)len > (uint)src.Length) throw new ArgumentOutOfRangeException(nameof(len));
            if ((uint)destStart > (uint)(destination.Length - len)) throw new ArgumentOutOfRangeException(nameof(destStart));

            // FAST: Array backing -> Array.Copy
            if (src.BackingKind == SpanProKind.Array && src.Owner is T[] arr)
            {
                Array.Copy(arr, src.Start, destination, destStart, len);
                return;
            }

            // FAST: Unmanaged backing + blittable -> Buffer.MemoryCopy
            if (src.BackingKind == SpanProKind.Unmanaged && src.Owner is IRawMemoryOwner<T> u
            && !RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                unsafe
                {
                    nint elemSize = Unsafe.SizeOf<T>();
                    nint bytes = (nint)len * elemSize;
                    nint srcAddr = u.BaseAddress + (nint)src.Start * elemSize;
                    fixed (T* pDst = &destination[destStart])
                    {
                        Buffer.MemoryCopy((void*)srcAddr, pDst, bytes, bytes);
                    }
                }
                return;
            }

            // Genel yol
            for (int i = 0; i < len; i++)
                destination[destStart + i] = src.ItemRefRO(i);
        }

        public static T[] ToArray<T>(this SpanPro<T> src)
            => src.ToArray(0, src.Length);

        public static T[] ToArray<T>(this SpanPro<T> src, int start, int length)
        {
            if ((uint)start > (uint)src.Length || (uint)length > (uint)(src.Length - start))
                throw new ArgumentOutOfRangeException();

            // FAST: Array backing -> Array.Copy
            if (src.BackingKind == SpanProKind.Array && src.Owner is T[] arrSrc)
            {
                var dst = new T[length];
                Array.Copy(arrSrc, src.Start + start, dst, 0, length);
                return dst;
            }

            // FAST: Unmanaged + blittable -> MemoryCopy
            if (src.BackingKind == SpanProKind.Unmanaged && src.Owner is IRawMemoryOwner<T> u
            && !RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                var dst = new T[length];
                unsafe
                {
                    nint elemSize = Unsafe.SizeOf<T>();
                    nint bytes = (nint)length * elemSize;
                    nint srcAddr = u.BaseAddress + (nint)(src.Start + start) * elemSize;
                    fixed (T* pDst = dst)
                    {
                        Buffer.MemoryCopy((void*)srcAddr, pDst, bytes, bytes);
                    }
                }
                return dst;
            }

            // Genel yol
            var result = new T[length];
            var slice = src.Slice(start, length);
            for (int i = 0; i < length; i++)
                result[i] = slice.ItemRefRO(i);
            return result;
        }

        public static bool TryCopyTo<T>(this SpanPro<T> src, SpanPro<T> dst)
        {
            if (dst.Length < src.Length) return false;
            for (int i = 0; i < src.Length; i++)
                dst.ItemRef(i) = src.ItemRefRO(i);
            return true;
        }

        public static SpanPro<char> AsSpanPro(this string arr, int start = 0, int length = -1)
            => SpanPro<char>.FromString(arr, start, length);

        public static SpanPro<T> AsSpanPro<T>(this T[] arr, int start = 0, int length = -1)
            => SpanPro<T>.FromArray(arr, start, length);

        /// <summary>
        /// Referans içermeyen türlerde hızlı clear (memset).
        /// Referans içerenlerde GC write barrier güvenli clear.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Clear<T>(this SpanPro<T> dst, int start, int length)
        {
            if ((uint)start > (uint)dst.Length || (uint)length > (uint)(dst.Length - start))
                throw new ArgumentOutOfRangeException();

            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                for (int i = 0; i < length; i++)
                    dst.ItemRef(start + i) = default!;
                return;
            }

            // Değer türleri: hızlı yol
            // Basit döngü (JIT vectorize edebilir)
            for (int i = 0; i < length; i++)
                dst.ItemRef(start + i) = default!;
        }
        // ========================= SLOW PATHS (genel) =========================

        private static int IndexOf_Naive<T>(SpanPro<T> sp, SpanPro<T> pat, int startIndex)
        {
            int n = sp.Length, m = pat.Length;
            var cmp = EqualityComparer<T>.Default;
            int i = startIndex, j = 0;

            while (i < n)
            {
                if (cmp.Equals(sp.ItemRefRO(i), pat.ItemRefRO(j)))
                {
                    i++; j++;
                    if (j == m) return i - m;
                }
                else
                {
                    i = i - j + 1;
                    j = 0;
                    if (i > n - m) break;
                }
            }
            return -1;
        }

        private static int LastIndexOf_Naive<T>(SpanPro<T> sp, SpanPro<T> pat, int endIndex /* inclusive */)
        {
            int n = sp.Length, m = pat.Length;
            int latestStart = Math.Min(endIndex, n - m);
            var cmp = EqualityComparer<T>.Default;

            for (int start = latestStart; start >= 0; start--)
            {
                int j = 0;
                while (j < m && cmp.Equals(sp.ItemRefRO(start + j), pat.ItemRefRO(j))) j++;
                if (j == m) return start;
            }
            return -1;
        }

        // ========================= FAST PATHS (char) =========================

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int IndexOf_BMH_Char<T>(SpanPro<T> sp, SpanPro<T> pat, int startIndex)
        {
            // Bu fonksiyon yalnızca typeof(T)==typeof(char) olduğunda çağrılır.
            int n = sp.Length, m = pat.Length;

            // 65536 elemanlı kaydırma tablosu
            int[] shift = new int[char.MaxValue + 1];
            for (int c = 0; c < shift.Length; c++) shift[c] = m;

            for (int i = 0; i < m - 1; i++)
            {
                char pc = AsChar(pat.ItemRefRO(i));
                shift[pc] = m - i - 1;
            }

            int idx = startIndex;
            while (idx <= n - m)
            {
                int j = m - 1;
                while (j >= 0 && AsChar(sp.ItemRefRO(idx + j)) == AsChar(pat.ItemRefRO(j)))
                    j--;

                if (j < 0) return idx;

                char bad = AsChar(sp.ItemRefRO(idx + m - 1));
                idx += shift[bad];
            }
            return -1;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static char AsChar(in T v) => Unsafe.As<T, char>(ref Unsafe.AsRef(v));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int LastIndexOf_BMH_Char<T>(SpanPro<T> sp, SpanPro<T> pat, int endIndex /* inclusive */)
        {
            // Basit yaklaşım: ileri doğru BMH ile bul, endIndex'e kadar son eşleşmeyi takip et.
            int n = sp.Length, m = pat.Length;
            if (m == 0) return endIndex;

            int last = -1;
            int idx = 0;
            while (true)
            {
                int found = IndexOf_BMH_Char(sp, pat, idx);
                if (found == -1 || found > endIndex - m + 1) break;
                last = found;
                idx = found + 1;
            }
            return last;
        }

        // ========================= FAST PATHS (byte) =========================

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int IndexOf_BMH_Byte<T>(SpanPro<T> sp, SpanPro<T> pat, int startIndex)
        {
            int n = sp.Length, m = pat.Length;

            Span<int> shift = stackalloc int[256];
            for (int i = 0; i < 256; i++) shift[i] = m;

            for (int i = 0; i < m - 1; i++)
            {
                byte pb = AsByte(pat.ItemRefRO(i));
                shift[pb] = m - i - 1;
            }

            int idx = startIndex;
            while (idx <= n - m)
            {
                int j = m - 1;
                while (j >= 0 && AsByte(sp.ItemRefRO(idx + j)) == AsByte(pat.ItemRefRO(j)))
                    j--;

                if (j < 0) return idx;

                byte bad = AsByte(sp.ItemRefRO(idx + m - 1));
                idx += shift[bad];
            }
            return -1;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static byte AsByte(in T v) => Unsafe.As<T, byte>(ref Unsafe.AsRef(v));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int LastIndexOf_BMH_Byte<T>(SpanPro<T> sp, SpanPro<T> pat, int endIndex /* inclusive */)
        {
            int n = sp.Length, m = pat.Length;
            if (m == 0) return endIndex;

            int last = -1;
            int idx = 0;
            while (true)
            {
                int found = IndexOf_BMH_Byte(sp, pat, idx);
                if (found == -1 || found > endIndex - m + 1) break;
                last = found;
                idx = found + 1;
            }
            return last;
        }

        /// <summary>
        /// Genelleştirilmiş, overlap-güvenli kopya. Referans içeren T’de write-barrier uyumlu.
        /// Değer türlerinde ve aynı backing’te hızlı memmove/Array.Copy kullanır.
        /// </summary>
        public static void DsMemoryCopySP<T>(SpanPro<T> src, int srcIndex, SpanPro<T> dst, int dstIndex, int count)
        {
            // Parametre guard’ları
            if ((uint)count > (uint)src.Length || (uint)count > (uint)dst.Length)
                throw new ArgumentOutOfRangeException(nameof(count));
            if ((uint)srcIndex > (uint)(src.Length - count))
                throw new ArgumentOutOfRangeException(nameof(srcIndex));
            if ((uint)dstIndex > (uint)(dst.Length - count))
                throw new ArgumentOutOfRangeException(nameof(dstIndex));

            if (count == 0) return;

            // === FAST PATH A: Aynı Array backing ===
            if (src.BackingKind == SpanProKind.Array &&
                dst.BackingKind == SpanProKind.Array &&
                ReferenceEquals(src.Owner, dst.Owner))
            {
                // Array.Copy overlap güvenlidir ve write-barrier uyumludur.
                var arr = (T[])src.Owner!;
                Array.Copy(arr, src.Start + srcIndex, arr, dst.Start + dstIndex, count);
                return;
            }

            // === FAST PATH B: Aynı Unmanaged backing (yalnızca değer türleri) ===
            if (src.BackingKind == SpanProKind.Unmanaged &&
                dst.BackingKind == SpanProKind.Unmanaged &&
                ReferenceEquals(src.Owner, dst.Owner))
            {
                if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                    throw new NotSupportedException("Reference-containing T unmanaged backinge yazılamaz.");

                var owner = (IRawMemoryOwner<T>)src.Owner!;
                nint size = (nint)Unsafe.SizeOf<T>() * count;
                nint srcAddr = owner.BaseAddress + (nint)(src.Start + srcIndex) * Unsafe.SizeOf<T>();
                nint dstAddr = owner.BaseAddress + (nint)(dst.Start + dstIndex) * Unsafe.SizeOf<T>();

                unsafe { Buffer.MemoryCopy((void*)srcAddr, (void*)dstAddr, size, size); }
                return;
            }

            // === SLOW PATH: Farklı backing veya karışık durumlar ===
            // Referans içeren T => write-barrier güvencesi için eleman bazlı kopya.
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                // Overlap ihtimali varsa yön seçimi (aynı owner ise)
                bool sameOwner = ReferenceEquals(src.Owner, dst.Owner) && src.BackingKind == dst.BackingKind;
                if (sameOwner && (dst.Start + dstIndex) > (src.Start + srcIndex))
                {
                    for (int i = count - 1; i >= 0; i--)
                        dst.ItemRef(dstIndex + i) = src.ItemRefRO(srcIndex + i);
                }
                else
                {
                    for (int i = 0; i < count; i++)
                        dst.ItemRef(dstIndex + i) = src.ItemRefRO(srcIndex + i);
                }
                return;
            }

            // Değer türleri: eleman döngüsü (farklı backing’te genel ve güvenli çözüm).
            if ((dst.Start + dstIndex) > (src.Start + srcIndex) && ReferenceEquals(src.Owner, dst.Owner))
            {
                for (int i = count - 1; i >= 0; i--)
                    dst.ItemRef(dstIndex + i) = src.ItemRefRO(srcIndex + i);
            }
            else
            {
                for (int i = 0; i < count; i++)
                    dst.ItemRef(dstIndex + i) = src.ItemRefRO(srcIndex + i);
            }
        }

        public static void Grow<T>(ref ArrayPool<T> arrayPool, ref T[]? arrayToReturnToPool, ref SpanPro<T> spanPro, int _length, int additionalCapacity)
        {
            int poolArrayLength = _length + (additionalCapacity * 2);

            T[] poolArray = arrayPool.Rent(poolArrayLength);

            //SpanPro.Slice(0, _length).CopyTo(poolArray);
            spanPro.CopyTo(poolArray, 0, _length);

            T[]? toReturn = arrayToReturnToPool;

            spanPro = arrayToReturnToPool = poolArray;

            if (toReturn != null)
            {
                arrayPool.Return(toReturn);
            }
        }

        public static int ClampStart<T>(SpanPro<T> span, T? value) /*where T : IEquatable<T>*/
        {
            int start = 0;

            if (value != null)
            {
                for (; start < span.Length; start++)
                {
                    if (!value.Equals(span[start]))
                    {
                        break;
                    }
                }
            }
            else
            {
                for (; start < span.Length; start++)
                {
                    if (span[start] != null)
                    {
                        break;
                    }
                }
            }

            return start;
        }
        public static int ClampEnd<T>(SpanPro<T> span, int start, T? value) /*where T : IEquatable<T>*/
        {
            int end = span.Length - 1;

            if (value != null)
            {
                for (; end >= start; end--)
                {
                    if (!value.Equals(span[end]))
                    {
                        break;
                    }
                }
            }
            else
            {
                for (; end >= start; end--)
                {
                    if (span[end] != null)
                    {
                        break;
                    }
                }
            }

            return end - start + 1;
        }

        //public static void Grow<T>(ref T[]? arrayToReturnToPool, ref SpanPro<T> spanPro, int _length, int additionalCapacity)
        //{
        //    int poolArrayLength = _length + (additionalCapacity * 2);

        //    T[] poolArray = ArrayPool<T>.Shared.Rent(poolArrayLength);

        //    if (arrayToReturnToPool == null)
        //    {
        //        spanPro.CopyTo(poolArray, 0, _length);
        //    }
        //    else
        //    {
        //        DsMemoryCopy<T>(arrayToReturnToPool, 0, poolArray, _length);
        //    }


        //    T[]? toReturn = arrayToReturnToPool;

        //    spanPro = arrayToReturnToPool = poolArray;

        //    if (toReturn != null)
        //    {
        //        ArrayPool<T>.Shared.Return(toReturn);
        //    }
        //}

    }

    public static class ReadOnlySpanProExtensions
    {
        public static ReadOnlySpanPro<char> Trim(this ReadOnlySpanPro<char> span)
        {
            int start = 0;
            for (; start < span.Length; start++)
            {
                if (!char.IsWhiteSpace(span[start]))
                {
                    break;
                }
            }

            int end = span.Length - 1;
            for (; end > start; end--)
            {
                if (!char.IsWhiteSpace(span[end]))
                {
                    break;
                }
            }

            return span.Slice(start, end - start + 1);
        }
        public static ReadOnlySpanPro<T> Trim<T>(this ReadOnlySpanPro<T> sp)
        {
            return sp.Trim(default);
        }
        public static ReadOnlySpanPro<T> Trim<T>(this ReadOnlySpanPro<T> sp, T value)
        {
            return sp.TrimStart(value).TrimEnd(value);
        }

        public static ReadOnlySpanPro<T> TrimStart<T>(this ReadOnlySpanPro<T> sp)
        {
            return sp.TrimStart(default);
        }
        public static ReadOnlySpanPro<T> TrimStart<T>(this ReadOnlySpanPro<T> sp, T value)
        {
            var cmp = EqualityComparer<T>.Default;
            int i = 0;
            while (i < sp.Length && cmp.Equals(sp.ItemRefRO(i), value)) i++;
            return sp.Slice(i, sp.Length - i);
        }

        public static ReadOnlySpanPro<T> TrimEnd<T>(this ReadOnlySpanPro<T> sp)
        {
            return sp.TrimEnd(default);
        }
        public static ReadOnlySpanPro<T> TrimEnd<T>(this ReadOnlySpanPro<T> sp, T value)
        {
            var cmp = EqualityComparer<T>.Default;
            int i = sp.Length - 1;
            while (i >= 0 && cmp.Equals(sp.ItemRefRO(i), value)) i--;
            return sp.Slice(0, i + 1);
        }

        public static int IndexOf<T>(this ReadOnlySpanPro<T> sp, T value) => sp.IndexOf(value, 0);
        /// <summary>sp içinde, startIndex'ten itibaren value değerinin ilk indexini döndürür; yoksa -1.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int IndexOf<T>(this ReadOnlySpanPro<T> sp, T value, int startIndex)
        {
            int n = sp.Length;
            if ((uint)startIndex > (uint)n) throw new ArgumentOutOfRangeException(nameof(startIndex));
            if (startIndex == n) return -1;

            int len = n - startIndex;
            var cmp = EqualityComparer<T>.Default;

            if (sp.BackingKind == SpanProKind.Array && sp.Owner is T[] arr)
            {
                ref T baseRef = ref MemoryMarshal.GetArrayDataReference(arr);
                ref T r = ref Unsafe.Add(ref baseRef, sp.Start + startIndex);
                for (int i = 0; i < len; i++)
                {
                    if (cmp.Equals(Unsafe.Add(ref r, i), value))
                        return startIndex + i;
                }
                return -1;
            }
            else if (sp.BackingKind == SpanProKind.Unmanaged && sp.Owner is IRawMemoryOwner<T> u)
            {
                nint elemSize = Unsafe.SizeOf<T>();
                nint addr = u.BaseAddress + (nint)(sp.Start + startIndex) * elemSize;
                unsafe
                {
                    for (int i = 0; i < len; i++)
                    {
                        ref T cur = ref Unsafe.AsRef<T>((void*)(addr + (nint)i * elemSize));
                        if (cmp.Equals(cur, value))
                            return startIndex + i;
                    }
                }
                return -1;
            }
            else if (sp.BackingKind == SpanProKind.StringRO && typeof(T) == typeof(char) && sp.Owner is string s)
            {
                // .NET 8 varsa kullan; değilse geri dönüş (AsSpan + GetReference)
#if NET8_0_OR_GREATER
    ref char c0 = ref MemoryMarshal.GetStringDataReference(s);
#else
                ref char c0 = ref MemoryMarshal.GetReference(s.AsSpan());
                // Alternatif: ref char c0 = ref s.GetPinnableReference();
#endif
                ref char r = ref Unsafe.Add(ref c0, sp.Start + startIndex);
                char target = Unsafe.As<T, char>(ref value);
                for (int i = 0; i < len; i++)
                {
                    if (Unsafe.Add(ref r, i) == target)
                        return startIndex + i;
                }
                return -1;
            }

            for (int i = startIndex; i < n; i++)
            {
                if (cmp.Equals(sp.ItemRefRO(i), value))
                    return i;
            }
            return -1;
        }

        /// <summary>sp içinde, startIndex'ten itibaren value (alt dizi) ilk geçtiği index; yoksa -1. Boş pattern =&gt; startIndex.</summary>
        public static int IndexOf<T>(this ReadOnlySpanPro<T> sp, ReadOnlySpanPro<T> value, int startIndex)
        {
            int n = sp.Length, m = value.Length;
            if ((uint)startIndex > (uint)n) throw new ArgumentOutOfRangeException(nameof(startIndex));
            if (m == 0) return startIndex;
            if (m > n || startIndex > n - m) return -1;

            // Hızlandırılmış yollar
            if (typeof(T) == typeof(char))
                return IndexOf_BMH_Char(sp, value, startIndex);
            if (typeof(T) == typeof(byte))
                return IndexOf_BMH_Byte(sp, value, startIndex);

            // Naif (genel) arama
            return IndexOf_Naive(sp, value, startIndex);
        }

        /// <summary>sp value içeriyor mu?</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Contains<T>(this ReadOnlySpanPro<T> sp, T value)
            => sp.IndexOf(value, 0) >= 0;

        /// <summary>sp içinde value'nun SON görüldüğü index; yoksa -1.</summary>
        public static int LastIndexOf<T>(this ReadOnlySpanPro<T> sp, T value)
        {
            int n = sp.Length;
            if (n == 0) return -1;

            var cmp = EqualityComparer<T>.Default;

            if (sp.BackingKind == SpanProKind.Array && sp.Owner is T[] arr)
            {
                ref T baseRef = ref MemoryMarshal.GetArrayDataReference(arr);
                ref T r0 = ref Unsafe.Add(ref baseRef, sp.Start);
                for (int i = n - 1; i >= 0; i--)
                {
                    if (cmp.Equals(Unsafe.Add(ref r0, i), value))
                        return i;
                }
                return -1;
            }
            else if (sp.BackingKind == SpanProKind.Unmanaged && sp.Owner is IRawMemoryOwner<T> u)
            {
                nint elemSize = Unsafe.SizeOf<T>();
                nint baseAddr = u.BaseAddress + (nint)sp.Start * elemSize;
                unsafe
                {
                    for (int i = n - 1; i >= 0; i--)
                    {
                        ref T cur = ref Unsafe.AsRef<T>((void*)(baseAddr + (nint)i * elemSize));
                        if (cmp.Equals(cur, value))
                            return i;
                    }
                }
                return -1;
            }
            else if (sp.BackingKind == SpanProKind.StringRO && typeof(T) == typeof(char) && sp.Owner is string s)
            {
                // .NET 8 varsa kullan; değilse geri dönüş (AsSpan + GetReference)
#if NET8_0_OR_GREATER
    ref char c0 = ref MemoryMarshal.GetStringDataReference(s);
#else
                ref char c0 = ref MemoryMarshal.GetReference(s.AsSpan());
                // Alternatif: ref char c0 = ref s.GetPinnableReference();
#endif
                ref char r0 = ref Unsafe.Add(ref c0, sp.Start);
                char target = Unsafe.As<T, char>(ref value);
                for (int i = n - 1; i >= 0; i--)
                {
                    if (Unsafe.Add(ref r0, i) == target)
                        return i;
                }
                return -1;
            }

            for (int i = n - 1; i >= 0; i--)
            {
                if (cmp.Equals(sp.ItemRefRO(i), value))
                    return i;
            }
            return -1;
        }

        /// <summary>sp içinde value (alt dizi) SON görüldüğü başlangıç indexi; yoksa -1.</summary>
        public static int LastIndexOf<T>(this ReadOnlySpanPro<T> sp, ReadOnlySpanPro<T> value)
        {
            int n = sp.Length, m = value.Length;
            if (m == 0) return n;
            if (m > n) return -1;

            // Hızlandırılmış yollar
            if (typeof(T) == typeof(char))
                return LastIndexOf_BMH_Char(sp, value, n - 1);
            if (typeof(T) == typeof(byte))
                return LastIndexOf_BMH_Byte(sp, value, n - 1);

            // Naif geriye arama
            return LastIndexOf_Naive(sp, value, n - 1);
        }

        /// <summary>sp içinde, endIndex (dahil) noktasına kadar geriye doğru value'nun SON indexi; yoksa -1.</summary>
        public static int LastIndexOf<T>(this ReadOnlySpanPro<T> sp, T value, int endIndex)
        {
            int n = sp.Length;
            if (n == 0)
            {
                if (endIndex != 0) throw new ArgumentOutOfRangeException(nameof(endIndex));
                return -1;
            }
            if ((uint)endIndex >= (uint)n) throw new ArgumentOutOfRangeException(nameof(endIndex));

            var cmp = EqualityComparer<T>.Default;
            for (int i = endIndex; i >= 0; i--)
            {
                if (cmp.Equals(sp.ItemRefRO(i), value))
                    return i;
            }
            return -1;
        }

        /// <summary>sp içinde, endIndex (dahil) noktasına kadar geriye doğru value (alt dizi) SON görüldüğü index; yoksa -1. Boş pattern =&gt; endIndex.</summary>
        public static int LastIndexOf<T>(this ReadOnlySpanPro<T> sp, ReadOnlySpanPro<T> value, int endIndex)
        {
            int n = sp.Length, m = value.Length;
            if (n == 0)
            {
                if (endIndex != 0) throw new ArgumentOutOfRangeException(nameof(endIndex));
                return m == 0 ? 0 : -1;
            }
            if ((uint)endIndex >= (uint)n) throw new ArgumentOutOfRangeException(nameof(endIndex));

            if (m == 0) return endIndex;
            if (m > n) return -1;

            // Hızlandırılmış yollar
            if (typeof(T) == typeof(char))
                return LastIndexOf_BMH_Char(sp, value, endIndex);
            if (typeof(T) == typeof(byte))
                return LastIndexOf_BMH_Byte(sp, value, endIndex);

            // Naif geriye arama
            return LastIndexOf_Naive(sp, value, endIndex);
        }


        /// <summary>
        /// source içinde, startIndex'ten itibaren value ile eşleşen TÜM indeksleri döndürür.
        /// Bulunamazsa boş dizi döner. EqualityComparer&lt;T&gt;.Default kullanır.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int[] FindAll<T>(this ReadOnlySpanPro<T> source, T value, int startIndex)
        {
            int n = source.Length;
            if ((uint)startIndex > (uint)n)
                throw new ArgumentOutOfRangeException(nameof(startIndex), "startIndex aralık dışında.");

            if (startIndex == n || n == 0)
                return Array.Empty<int>();

            var cmp = EqualityComparer<T>.Default;

            // Küçük bir tahminle başlayıp (n-start)/8 + 4, gerektiğinde ikiye katlayarak büyütüyoruz.
            int estimate = ((n - startIndex) >> 3) + 4;
            if (estimate < 4) estimate = 4;

            int[] buf = ArrayPool<int>.Shared.Rent(estimate);
            int count = 0;

            try
            {
                for (int i = startIndex; i < n; i++)
                {
                    if (cmp.Equals(source.ItemRefRO(i), value))
                    {
                        if (count == buf.Length)
                        {
                            // Büyüt
                            int[] bigger = ArrayPool<int>.Shared.Rent(buf.Length << 1);
                            Array.Copy(buf, 0, bigger, 0, count);
                            ArrayPool<int>.Shared.Return(buf);
                            buf = bigger;
                        }
                        buf[count++] = i;
                    }
                }

                if (count == 0)
                    return Array.Empty<int>();

                var result = new int[count];
                Array.Copy(buf, 0, result, 0, count);
                return result;
            }
            finally
            {
                ArrayPool<int>.Shared.Return(buf);
            }
        }

        // Kalite-of-life: byte için int değer kabul eden overload (örn. 32)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int[] FindAll(this ReadOnlySpanPro<byte> source, int value, int startIndex)
        {
            if ((uint)value > 255)
                throw new ArgumentOutOfRangeException(nameof(value), "byte aralığında olmalı (0..255).");
            return FindAll(source, (byte)value, startIndex);
        }

        /// <summary>
        /// source içinde, value değerinin İLK görüldüğü indexi döndürür.
        /// Bulunamazsa -1 döner.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int FindFirst<T>(this ReadOnlySpanPro<T> source, T value)
        {
            int n = source.Length;
            if (n == 0) return -1;

            var cmp = EqualityComparer<T>.Default;
            for (int i = 0; i < n; i++)
            {
                if (cmp.Equals(source.ItemRefRO(i), value))
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Naif ama overlap-dostu arama (KMP’siz). T için EqualityComparer kullanır.
        /// </summary>
        public static int FindFirst<T>(this ReadOnlySpanPro<T> source, ReadOnlySpanPro<T> value)
        {
            int n = source.Length, m = value.Length;
            if (m == 0) return 0;
            if (m > n) return -1;

            var cmp = EqualityComparer<T>.Default;
            int i = 0, j = 0;

            // n-m+1 pencereden öteye geçme
            while (i < n)
            {
                if (cmp.Equals(source.ItemRefRO(i), value.ItemRefRO(j)))
                {
                    i++; j++;
                    if (j == m) return i - m;
                    // eşleşme uzuyor, devam
                }
                else
                {
                    // pencereyi kaydır: i önceki pencerenin başının 1 ötesine
                    i = i - j + 1;
                    j = 0;

                    // artık kalan uzunluk m’den kısa ise çık
                    if (i + m > n) break;
                }
            }
            return -1;
        }

        public static int FindLast<T>(this ReadOnlySpanPro<T> source, ReadOnlySpanPro<T> value)
        {
            int n = source.Length, m = value.Length;
            if (m == 0) return n;
            if (m > n) return -1;

            var cmp = EqualityComparer<T>.Default;
            // Son başlangıç indexinden geriye doğru ara
            for (int start = n - m; start >= 0; start--)
            {
                int j = 0;
                while (j < m && cmp.Equals(source.ItemRefRO(start + j), value.ItemRefRO(j))) j++;
                if (j == m) return start;
            }
            return -1;
        }

        /// <summary>source içinde value'nun SON görüldüğü index; yoksa -1. (kısa ad)</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int FindLast<T>(this ReadOnlySpanPro<T> source, T value)
            => source.LastIndexOf(value);

        // Basit kopya: 0’dan 0’a kadar kopyalar (eski CopyTo’nun yerini alır)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void CopyTo<T>(this ReadOnlySpanPro<T> src, SpanPro<T> dst)
            => DsMemoryCopySP(src, 0, dst, 0, Math.Min(src.Length, dst.Length));

        public static void CopyTo<T>(this ReadOnlySpanPro<T> src, T[] destination, int destStart)
            => src.CopyTo(destination, destStart, src.Length);

        public static void CopyTo<T>(this ReadOnlySpanPro<T> src, T[] destination, int destStart, int len)
        {
            if (destination is null) throw new ArgumentNullException(nameof(destination));
            if ((uint)len > (uint)src.Length) throw new ArgumentOutOfRangeException(nameof(len));
            if ((uint)destStart > (uint)(destination.Length - len)) throw new ArgumentOutOfRangeException(nameof(destStart));

            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                for (int i = 0; i < len; i++)
                    destination[destStart + i] = src.ItemRefRO(i);
            }
            else
            {
                for (int i = 0; i < len; i++)
                    destination[destStart + i] = src.ItemRefRO(i);
            }
        }

        public static T[] ToArray<T>(this ReadOnlySpanPro<T> src)
            => src.ToArray(0, src.Length);

        public static T[] ToArray<T>(this ReadOnlySpanPro<T> src, int start, int length)
        {
            if ((uint)start > (uint)src.Length || (uint)length > (uint)(src.Length - start))
                throw new ArgumentOutOfRangeException();

            // FAST: Array backing -> Array.Copy
            if (src.BackingKind == SpanProKind.Array && src.Owner is T[] arrSrc)
            {
                var dst = new T[length];
                Array.Copy(arrSrc, src.Start + start, dst, 0, length);
                return dst;
            }

            // FAST: StringRO(char)
            if (src.BackingKind == SpanProKind.StringRO && typeof(T) == typeof(char) && src.Owner is string s)
            {
                var dst = new char[length];
                s.CopyTo(src.Start + start, dst, 0, length);
                return (T[])(object)dst;
            }

            // FAST: Unmanaged + blittable -> MemoryCopy
            if (src.BackingKind == SpanProKind.Unmanaged && src.Owner is IRawMemoryOwner<T> u
            && !RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                var dst = new T[length];
                unsafe
                {
                    nint elemSize = Unsafe.SizeOf<T>();
                    nint bytes = (nint)length * elemSize;
                    nint srcAddr = u.BaseAddress + (nint)(src.Start + start) * elemSize;
                    fixed (T* pDst = dst)
                    {
                        Buffer.MemoryCopy((void*)srcAddr, pDst, bytes, bytes);
                    }
                }
                return dst;
            }

            // Genel yol
            var arr = new T[length];
            var slice = src.Slice(start, length);
            for (int i = 0; i < length; i++)
                arr[i] = slice.ItemRefRO(i);
            return arr;
        }

        public static bool TryCopyTo<T>(this ReadOnlySpanPro<T> src, SpanPro<T> dst)
        {
            if (dst.Length < src.Length) return false;
            for (int i = 0; i < src.Length; i++)
                dst.ItemRef(i) = src.ItemRefRO(i);
            return true;
        }


        public static ReadOnlySpanPro<char> AsReadOnlySpanPro(this string s, int start = 0, int length = -1)
            => ReadOnlySpanPro<char>.FromString(s, start, length);

        public static ReadOnlySpanPro<T> AsReadOnlySpanPro<T>(this T[] arr, int start = 0, int length = -1)
            => ReadOnlySpanPro<T>.FromArray(arr, start, length);

        /// <summary>
        /// Referans içermeyen türlerde hızlı clear (memset).
        /// Referans içerenlerde GC write barrier güvenli clear.
        /// </summary>
        public static void Clear<T>(this ReadOnlySpanPro<T> ro, int start, int length)
        {
            if ((uint)start > (uint)ro.Length || (uint)length > (uint)(ro.Length - start))
                throw new ArgumentOutOfRangeException();

            if (ro.BackingKind == SpanProKind.StringRO)
                throw new InvalidOperationException("StringRO temizlenemez.");

            // Array veya Unmanaged: yazılabilir görünüme sar ve temizle
            var w = new SpanPro<T>(ro.Owner!, ro.Start, ro.Length, ro.BackingKind);
            w.Fill(start, length, default!);
        }

        public static bool TryAsWritable<T>(this ReadOnlySpanPro<T> ro, out SpanPro<T> sp)
        {
            if (ro.BackingKind == SpanProKind.StringRO)
            {
                sp = default;
                return false; // string arka plan yazılamaz
            }
            // internal ctor'u kullanıyoruz (aynı assembly)
            sp = new SpanPro<T>(ro.Owner!, ro.Start, ro.Length, ro.BackingKind);
            return true;
        }

        // ========================= SLOW PATHS (genel) =========================

        private static int IndexOf_Naive<T>(ReadOnlySpanPro<T> sp, ReadOnlySpanPro<T> pat, int startIndex)
        {
            int n = sp.Length, m = pat.Length;
            var cmp = EqualityComparer<T>.Default;
            int i = startIndex, j = 0;

            while (i < n)
            {
                if (cmp.Equals(sp.ItemRefRO(i), pat.ItemRefRO(j)))
                {
                    i++; j++;
                    if (j == m) return i - m;
                }
                else
                {
                    i = i - j + 1;
                    j = 0;
                    if (i > n - m) break;
                }
            }
            return -1;
        }

        private static int LastIndexOf_Naive<T>(ReadOnlySpanPro<T> sp, ReadOnlySpanPro<T> pat, int endIndex /* inclusive */)
        {
            int n = sp.Length, m = pat.Length;
            int latestStart = Math.Min(endIndex, n - m);
            var cmp = EqualityComparer<T>.Default;

            for (int start = latestStart; start >= 0; start--)
            {
                int j = 0;
                while (j < m && cmp.Equals(sp.ItemRefRO(start + j), pat.ItemRefRO(j))) j++;
                if (j == m) return start;
            }
            return -1;
        }

        // ========================= FAST PATHS (char) =========================

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int IndexOf_BMH_Char<T>(ReadOnlySpanPro<T> sp, ReadOnlySpanPro<T> pat, int startIndex)
        {
            // Bu fonksiyon yalnızca typeof(T)==typeof(char) olduğunda çağrılır.
            int n = sp.Length, m = pat.Length;

            // 65536 elemanlı kaydırma tablosu
            int[] shift = new int[char.MaxValue + 1];
            for (int c = 0; c < shift.Length; c++) shift[c] = m;

            for (int i = 0; i < m - 1; i++)
            {
                char pc = AsChar(pat.ItemRefRO(i));
                shift[pc] = m - i - 1;
            }

            int idx = startIndex;
            while (idx <= n - m)
            {
                int j = m - 1;
                while (j >= 0 && AsChar(sp.ItemRefRO(idx + j)) == AsChar(pat.ItemRefRO(j)))
                    j--;

                if (j < 0) return idx;

                char bad = AsChar(sp.ItemRefRO(idx + m - 1));
                idx += shift[bad];
            }
            return -1;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static char AsChar(in T v) => Unsafe.As<T, char>(ref Unsafe.AsRef(v));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int LastIndexOf_BMH_Char<T>(ReadOnlySpanPro<T> sp, ReadOnlySpanPro<T> pat, int endIndex /* inclusive */)
        {
            // Basit yaklaşım: ileri doğru BMH ile bul, endIndex'e kadar son eşleşmeyi takip et.
            int n = sp.Length, m = pat.Length;
            if (m == 0) return endIndex;

            int last = -1;
            int idx = 0;
            while (true)
            {
                int found = IndexOf_BMH_Char(sp, pat, idx);
                if (found == -1 || found > endIndex - m + 1) break;
                last = found;
                idx = found + 1;
            }
            return last;
        }

        // ========================= FAST PATHS (byte) =========================

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int IndexOf_BMH_Byte<T>(ReadOnlySpanPro<T> sp, ReadOnlySpanPro<T> pat, int startIndex)
        {
            int n = sp.Length, m = pat.Length;

            Span<int> shift = stackalloc int[256];
            for (int i = 0; i < 256; i++) shift[i] = m;

            for (int i = 0; i < m - 1; i++)
            {
                byte pb = AsByte(pat.ItemRefRO(i));
                shift[pb] = m - i - 1;
            }

            int idx = startIndex;
            while (idx <= n - m)
            {
                int j = m - 1;
                while (j >= 0 && AsByte(sp.ItemRefRO(idx + j)) == AsByte(pat.ItemRefRO(j)))
                    j--;

                if (j < 0) return idx;

                byte bad = AsByte(sp.ItemRefRO(idx + m - 1));
                idx += shift[bad];
            }
            return -1;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static byte AsByte(in T v) => Unsafe.As<T, byte>(ref Unsafe.AsRef(v));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int LastIndexOf_BMH_Byte<T>(ReadOnlySpanPro<T> sp, ReadOnlySpanPro<T> pat, int endIndex /* inclusive */)
        {
            int n = sp.Length, m = pat.Length;
            if (m == 0) return endIndex;

            int last = -1;
            int idx = 0;
            while (true)
            {
                int found = IndexOf_BMH_Byte(sp, pat, idx);
                if (found == -1 || found > endIndex - m + 1) break;
                last = found;
                idx = found + 1;
            }
            return last;
        }

        /// <summary>
        /// Genelleştirilmiş, overlap-güvenli kopya. Referans içeren T’de write-barrier uyumlu.
        /// Değer türlerinde ve aynı backing’te hızlı memmove/Array.Copy kullanır.
        /// </summary>
        public static void DsMemoryCopySP<T>(ReadOnlySpanPro<T> src, int srcIndex, SpanPro<T> dst, int dstIndex, int count)
        {
            // Parametre guard’ları
            if ((uint)count > (uint)src.Length || (uint)count > (uint)dst.Length)
                throw new ArgumentOutOfRangeException(nameof(count));
            if ((uint)srcIndex > (uint)(src.Length - count))
                throw new ArgumentOutOfRangeException(nameof(srcIndex));
            if ((uint)dstIndex > (uint)(dst.Length - count))
                throw new ArgumentOutOfRangeException(nameof(dstIndex));

            if (count == 0) return;

            // === FAST PATH A: Aynı Array backing ===
            if (src.BackingKind == SpanProKind.Array &&
                dst.BackingKind == SpanProKind.Array &&
                ReferenceEquals(src.Owner, dst.Owner))
            {
                // Array.Copy overlap güvenlidir ve write-barrier uyumludur.
                var arr = (T[])src.Owner!;
                Array.Copy(arr, src.Start + srcIndex, arr, dst.Start + dstIndex, count);
                return;
            }

            // === FAST PATH B: Aynı Unmanaged backing (yalnızca değer türleri) ===
            if (src.BackingKind == SpanProKind.Unmanaged &&
                dst.BackingKind == SpanProKind.Unmanaged &&
                ReferenceEquals(src.Owner, dst.Owner))
            {
                if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                    throw new NotSupportedException("Reference-containing T unmanaged backinge yazılamaz.");

                var owner = (IRawMemoryOwner<T>)src.Owner!;
                nint size = (nint)Unsafe.SizeOf<T>() * count;
                nint srcAddr = owner.BaseAddress + (nint)(src.Start + srcIndex) * Unsafe.SizeOf<T>();
                nint dstAddr = owner.BaseAddress + (nint)(dst.Start + dstIndex) * Unsafe.SizeOf<T>();

                unsafe { Buffer.MemoryCopy((void*)srcAddr, (void*)dstAddr, size, size); }
                return;
            }

            // === SLOW PATH: Farklı backing veya karışık durumlar ===
            // Referans içeren T => write-barrier güvencesi için eleman bazlı kopya.
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                // Overlap ihtimali varsa yön seçimi (aynı owner ise)
                bool sameOwner = ReferenceEquals(src.Owner, dst.Owner) && src.BackingKind == dst.BackingKind;
                if (sameOwner && (dst.Start + dstIndex) > (src.Start + srcIndex))
                {
                    for (int i = count - 1; i >= 0; i--)
                        dst.ItemRef(dstIndex + i) = src.ItemRefRO(srcIndex + i);
                }
                else
                {
                    for (int i = 0; i < count; i++)
                        dst.ItemRef(dstIndex + i) = src.ItemRefRO(srcIndex + i);
                }
                return;
            }

            // Değer türleri: eleman döngüsü (farklı backing’te genel ve güvenli çözüm).
            if ((dst.Start + dstIndex) > (src.Start + srcIndex) && ReferenceEquals(src.Owner, dst.Owner))
            {
                for (int i = count - 1; i >= 0; i--)
                    dst.ItemRef(dstIndex + i) = src.ItemRefRO(srcIndex + i);
            }
            else
            {
                for (int i = 0; i < count; i++)
                    dst.ItemRef(dstIndex + i) = src.ItemRefRO(srcIndex + i);
            }
        }

    }
}
