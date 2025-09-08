#nullable enable
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DSO.Core.SpanPro
{
    public enum SpanProKind : byte { None, Array, StringRO, Unmanaged }

    /// <summary>
    /// Unmanaged/pinli bloklar için basit sahip modeli.
    /// System.Buffers.IMemoryOwner<T> ile karışmaması için farklı ad.
    /// </summary>
    public interface IRawMemoryOwner<T> : IDisposable
    {
        nint BaseAddress { get; }
        int Length { get; }
        bool IsPinned { get; }
    }

    /// <summary>GCHandle ile dizi pinleyen sahip.</summary>
    public sealed class PinnedArrayOwner<T> : IRawMemoryOwner<T>
    {
        private GCHandle _h;
        private readonly T[] _arr;
        public PinnedArrayOwner(T[] arr)
        {
            _arr = arr ?? throw new ArgumentNullException(nameof(arr));
            _h = GCHandle.Alloc(arr, GCHandleType.Pinned);
        }
        public nint BaseAddress => _h.AddrOfPinnedObject();
        public int Length => _arr.Length;
        public bool IsPinned => true;
        public void Dispose() { if (_h.IsAllocated) _h.Free(); }
    }

    /// <summary>NativeMemory ile unmanaged blok tahsis eden sahip.</summary>
    unsafe public sealed class UnmanagedOwner<T> : IRawMemoryOwner<T>
    {
        public nint BaseAddress { get; private set; }
        public int Length { get; }
        public bool IsPinned => false;

        public UnmanagedOwner(int length)
        {
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                throw new NotSupportedException("UnmanagedOwner<T> sadece referans içermeyen T'yi destekler.");

            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            Length = length;
            BaseAddress = (nint)NativeMemory.Alloc((nuint)(length * Unsafe.SizeOf<T>()));
        }

        public void Dispose()
        {
            var p = BaseAddress;
            BaseAddress = 0;
            if (p != 0) NativeMemory.Free((void*)p);
        }
    }

    /// <summary>Salt okunur, property-dostu, GC güvenli 'span benzeri' yapı.</summary>
    [DebuggerTypeProxy(typeof(ReadOnlySpanProDebugView<>))]
    [DebuggerDisplay("{ToString()} - Length = {Length}")]
    public readonly struct ReadOnlySpanPro<T> : IEquatable<ReadOnlySpanPro<T>>
    {
        private readonly object? _owner;   // T[] | string | IRawMemoryOwner<T>
        private readonly int _start;       // eleman offset
        public int Length { get; }
        private readonly SpanProKind _kind;

        public bool IsEmpty => Length == 0;
        public SpanProKind Kind => _kind;

        internal object? Owner => _owner;
        internal int Start => _start;
        internal SpanProKind BackingKind => _kind;

        internal ReadOnlySpanPro(object owner, int start, int length, SpanProKind kind)
        {
            _owner = owner;
            _start = start;
            Length = length;
            _kind = kind;
        }

        public T this[int index]
        {
            get
            {
                return ItemRefRO(index);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        unsafe public ref readonly T ItemRefRO(int index)
        {
            if ((uint)index >= (uint)Length) throw new ArgumentOutOfRangeException(nameof(index));

            switch (_kind)
            {
                case SpanProKind.Array:
                    {
                        var arr = Unsafe.As<object, T[]?>(ref Unsafe.AsRef(_owner));
                        if (arr is null) throw new NullReferenceException();
                        ref T r0 = ref MemoryMarshal.GetArrayDataReference(arr);
                        return ref Unsafe.Add(ref r0, _start + index);
                    }
                case SpanProKind.StringRO:
                    {
                        // Yalnızca T == char destekli
                        if (typeof(T) != typeof(char))
                            throw new InvalidOperationException("StringRO yalnızca char ile kullanılabilir.");

                        var s = Unsafe.As<object, string?>(ref Unsafe.AsRef(_owner));
                        if (s is null) throw new NullReferenceException();

                        // .NET 8 varsa kullan; değilse geri dönüş (AsSpan + GetReference)
#if NET8_0_OR_GREATER
    ref char c0 = ref MemoryMarshal.GetStringDataReference(s);
#else
                        ref char c0 = ref MemoryMarshal.GetReference(s.AsSpan());
                        // Alternatif: ref char c0 = ref s.GetPinnableReference();
#endif

                        ref char cr = ref Unsafe.Add(ref c0, _start + index);
                        return ref Unsafe.As<char, T>(ref cr);
                    }

                case SpanProKind.Unmanaged:
                    {
                        var u = Unsafe.As<object, IRawMemoryOwner<T>?>(ref Unsafe.AsRef(_owner));
                        if (u is null) throw new NullReferenceException();
                        nint addr = u.BaseAddress + (nint)(_start + index) * Unsafe.SizeOf<T>();
                        return ref Unsafe.AsRef<T>((void*)addr);
                    }
                default:
                    throw new InvalidOperationException();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpanPro<T> Slice(int start) => Slice(start, Length - start);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpanPro<T> Slice(int start, int length)
        {
            if ((uint)start > (uint)Length || (uint)length > (uint)(Length - start))
                throw new ArgumentOutOfRangeException();
            return new ReadOnlySpanPro<T>(_owner!, _start + start, length, _kind);
        }

        // Fabrika yardımcıları
        public static ReadOnlySpanPro<T> FromArray(T[] arr, int start = 0, int length = -1)
        {
            if (arr is null) throw new ArgumentNullException(nameof(arr));
            if (length < 0) length = arr.Length - start;
            if ((uint)start > (uint)arr.Length || (uint)length > (uint)(arr.Length - start))
                throw new ArgumentOutOfRangeException();
            return new ReadOnlySpanPro<T>(arr, start, length, SpanProKind.Array);
        }

        public static ReadOnlySpanPro<char> FromString(string s, int start = 0, int length = -1)
        {
            if (s is null) throw new ArgumentNullException(nameof(s));
            if (length < 0) length = s.Length - start;
            if ((uint)start > (uint)s.Length || (uint)length > (uint)(s.Length - start))
                throw new ArgumentOutOfRangeException();
            return new ReadOnlySpanPro<char>(s, start, length, SpanProKind.StringRO);
        }

        public static ReadOnlySpanPro<T> Pin(T[] arr)
        {
            if (arr is null) throw new ArgumentNullException(nameof(arr));
            var owner = new PinnedArrayOwner<T>(arr);
            return new ReadOnlySpanPro<T>(owner, 0, owner.Length, SpanProKind.Unmanaged);
        }

        public static ReadOnlySpanPro<T> FromUnmanagedOwner(IRawMemoryOwner<T> owner, int start = 0, int length = -1)
        {
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                throw new NotSupportedException("UnmanagedOwner<T> sadece referans içermeyen T'yi destekler.");

            if (owner is null) throw new ArgumentNullException(nameof(owner));
            if (length < 0) length = owner.Length - start;
            if ((uint)start > (uint)owner.Length || (uint)length > (uint)(owner.Length - start))
                throw new ArgumentOutOfRangeException();
            return new ReadOnlySpanPro<T>(owner, start, length, SpanProKind.Unmanaged);
        }

        /// <summary>Span'ın tamamını verilen değerle doldurur.</summary>
        public void Fill(T value) => Fill(0, Length, value);

        /// <summary>Span'ın [start, start+length) aralığını verilen değerle doldurur.</summary>
        public void Fill(int start, int length, T value)
        {
            if ((uint)start > (uint)Length || (uint)length > (uint)(Length - start))
                throw new ArgumentOutOfRangeException();

            // Referans içeren T'yi unmanaged backinge yazmak tehlikelidir (GC taramaz)
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>() && _kind == SpanProKind.Unmanaged)
                throw new NotSupportedException("Reference-containing T cannot be stored in unmanaged backing.");

            if (!this.TryAsWritable(out var w))
                throw new InvalidOperationException("ReadOnlySpanPro yazılabilir değil (StringRO).");

            // Asıl temizlik SpanPro tarafında yapılır (write-barrier kontrolleri dâhil)
            w.Fill(start, length, value!);
        }

        // --------- Dönüşümler ve eşitlik ---------

        /// <summary>Null ise default, değilse diziden span yaratır.</summary>
        public static implicit operator ReadOnlySpanPro<T>(T[]? array) => array is null ? default : FromArray(array);

        public static bool operator ==(ReadOnlySpanPro<T> left, ReadOnlySpanPro<T> right) => left.Equals(right);
        public static bool operator !=(ReadOnlySpanPro<T> left, ReadOnlySpanPro<T> right) => !left.Equals(right);

        public bool Equals(ReadOnlySpanPro<T> other)
        => ReferenceEquals(_owner, other._owner)
           && _start == other._start
           && Length == other.Length
           && _kind == other._kind;

        public override bool Equals(object? obj)
            => obj is ReadOnlySpanPro<T> sp && Equals(sp);

        public override int GetHashCode()
        {
            // _owner referans hash'i + alanlar
            unchecked
            {
                int h = 17;
                h = h * 31 + (_owner?.GetHashCode() ?? 0);
                h = h * 31 + _start;
                h = h * 31 + Length;
                h = h * 31 + (int)_kind;
                return h;
            }
        }

        // --------- ToArray varyantları ---------

        /// <summary>İçeriği yeni bir diziye kopyalar. isRemoveNull: yalnızca referans veya Nullable tiplerde 'boş'ları atar.</summary>
        public T[] ToArray(bool isRemoveNull = false)
            => ToArray(0, Length, isRemoveNull);

        public T[] ToArray(int start, int length, bool isRemoveNull = false)
        {
            if ((uint)start > (uint)Length || (uint)length > (uint)(Length - start))
                throw new ArgumentOutOfRangeException();

            bool isRef = !typeof(T).IsValueType;
            bool isNullable = Nullable.GetUnderlyingType(typeof(T)) is not null;
            bool removeEmpty = isRemoveNull && (isRef || isNullable);

            if (!removeEmpty)
            {
                var arr = new T[length];
                // Slice'ı döngü dışında bir kez oluşturup aynı görünüme erişelim
                var slice = Slice(start, length);
                for (int i = 0; i < length; i++)
                    arr[i] = slice.ItemRefRO(i);
                return arr;
            }
            else
            {
                // İlk geçiş: say
                int count = 0;
                var slice = Slice(start, length);
                var cmp = EqualityComparer<T>.Default;
                for (int i = 0; i < length; i++)
                {
                    if (!cmp.Equals(slice.ItemRefRO(i), default!))
                        count++;
                }
                var arr = new T[count];
                int w = 0;
                for (int i = 0; i < length; i++)
                {
                    var v = slice.ItemRefRO(i);
                    if (!EqualityComparer<T>.Default.Equals(v, default!))
                        arr[w++] = v;
                }
                return arr;
            }
        }

        // --------- ToString varyantları ---------

        /// <summary>Debug dostu temsil. T==char ise gerçek metin üretir.</summary>
        public override string ToString()
        {
            if (typeof(T) == typeof(char))
            {
                return ToString(0, Length);
            }
            return $"SpanPro<{typeof(T).Name}>[{Length}]";
        }

        /// <summary>T==char için [start,length] aralığını string olarak döndürür.</summary>
        public string ToString(int start, int length)
        {
            if ((uint)start > (uint)Length || (uint)length > (uint)(Length - start))
                throw new ArgumentOutOfRangeException();
            if (typeof(T) != typeof(char))
                throw new InvalidOperationException("ToString(start,len) yalnızca SpanPro<char> için desteklenir.");

            var slice = Slice(start, length);

            return string.Create(length, slice, static (dst, s) =>
            {
                for (int i = 0; i < dst.Length; i++)
                {
                    // ref readonly T -> ref readonly char
                    ref readonly char ch = ref Unsafe.As<T, char>(ref Unsafe.AsRef(s.ItemRefRO(i)));
                    dst[i] = ch;
                }
            });
        }

        public Enumerator GetEnumerator() => new Enumerator(this);

        public ref struct Enumerator
        {
            private readonly ReadOnlySpanPro<T> _spanpro;
            private int _index;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal Enumerator(ReadOnlySpanPro<T> spanpro)
            {
                _spanpro = spanpro;
                _index = -1;
            }

            public bool MoveNext()
            {
                int index = _index + 1;
                if (index < _spanpro.Length)
                {
                    _index = index;
                    return true;
                }

                return false;
            }

            public T Current
            {
                get => _spanpro[_index];
            }
        }
    }

    /// <summary>Yazılabilir varyant. StringRO hariç (array/unmanaged için yazılabilir).</summary>
    [DebuggerTypeProxy(typeof(SpanProDebugView<>))]
    [DebuggerDisplay("{ToString()} - Length = {Length}")]
    public struct SpanPro<T> : IEquatable<SpanPro<T>>
    {
        private object? _owner;   // T[] | IRawMemoryOwner<T>
        private int _start;
        public int Length { get; private set; }
        private SpanProKind _kind;

        public bool IsEmpty => Length == 0;
        public SpanProKind Kind => _kind;

        internal object? Owner => _owner;
        internal int Start => _start;
        internal SpanProKind BackingKind => _kind;

        internal SpanPro(object owner, int start, int length, SpanProKind kind)
        {
            _owner = owner;
            _start = start;
            Length = length;
            _kind = kind;
        }

        public T this[int index]
        {
            get
            {
                return ItemRef(index);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        unsafe public ref T ItemRef(int index)
        {
            if ((uint)index >= (uint)Length) throw new ArgumentOutOfRangeException(nameof(index));

            switch (_kind)
            {
                case SpanProKind.Array:
                    {
                        var arr = Unsafe.As<object, T[]?>(ref Unsafe.AsRef(_owner));
                        if (arr is null) throw new NullReferenceException();
                        ref T r0 = ref MemoryMarshal.GetArrayDataReference(arr);
                        return ref Unsafe.Add(ref r0, _start + index);
                    }
                case SpanProKind.Unmanaged:
                    {
                        var u = Unsafe.As<object, IRawMemoryOwner<T>?>(ref Unsafe.AsRef(_owner));
                        if (u is null) throw new NullReferenceException();
                        nint addr = u.BaseAddress + (nint)(_start + index) * Unsafe.SizeOf<T>();
                        return ref Unsafe.AsRef<T>((void*)addr);
                    }
                default:
                    throw new InvalidOperationException("Writable access not supported for this backing.");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        unsafe public ref readonly T ItemRefRO(int index)
        {
            // Yazılabilir span’da da RO erişim gerekir.
            if ((uint)index >= (uint)Length) throw new ArgumentOutOfRangeException(nameof(index));

            switch (_kind)
            {
                case SpanProKind.Array:
                    {
                        var arr = Unsafe.As<object, T[]?>(ref Unsafe.AsRef(_owner));
                        if (arr is null) throw new NullReferenceException();
                        ref T r0 = ref MemoryMarshal.GetArrayDataReference(arr);
                        return ref Unsafe.Add(ref r0, _start + index);
                    }
                case SpanProKind.Unmanaged:
                    {
                        var u = Unsafe.As<object, IRawMemoryOwner<T>?>(ref Unsafe.AsRef(_owner));
                        if (u is null) throw new NullReferenceException();
                        nint addr = u.BaseAddress + (nint)(_start + index) * Unsafe.SizeOf<T>();
                        return ref Unsafe.AsRef<T>((void*)addr);
                    }
                default:
                    throw new InvalidOperationException();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SpanPro<T> Slice(int start) => Slice(start, Length - start);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SpanPro<T> Slice(int start, int length)
        {
            if ((uint)start > (uint)Length || (uint)length > (uint)(Length - start))
                throw new ArgumentOutOfRangeException();
            return new SpanPro<T>(_owner!, _start + start, length, _kind);
        }

        // Fabrikalar
        public static SpanPro<T> FromArray(T[] arr, int start = 0, int length = -1)
        {
            if (arr is null) throw new ArgumentNullException(nameof(arr));
            if (length < 0) length = arr.Length - start;
            if ((uint)start > (uint)arr.Length || (uint)length > (uint)(arr.Length - start))
                throw new ArgumentOutOfRangeException();
            return new SpanPro<T>(arr, start, length, SpanProKind.Array);
        }

        public static SpanPro<char> FromString(string s, int start = 0, int length = -1)
        {
            if (s is null) throw new ArgumentNullException(nameof(s));
            if (length < 0) length = s.Length - start;
            if ((uint)start > (uint)s.Length || (uint)length > (uint)(s.Length - start))
                throw new ArgumentOutOfRangeException();
            return new SpanPro<char>(s, start, length, SpanProKind.StringRO);
        }

        public static SpanPro<T> Pin(T[] arr)
        {
            if (arr is null) throw new ArgumentNullException(nameof(arr));
            var owner = new PinnedArrayOwner<T>(arr);
            return new SpanPro<T>(owner, 0, owner.Length, SpanProKind.Unmanaged);
        }

        public static SpanPro<T> FromUnmanagedOwner(IRawMemoryOwner<T> owner, int start = 0, int length = -1)
        {
            if (owner is null) throw new ArgumentNullException(nameof(owner));
            if (length < 0) length = owner.Length - start;
            if ((uint)start > (uint)owner.Length || (uint)length > (uint)(owner.Length - start))
                throw new ArgumentOutOfRangeException();
            return new SpanPro<T>(owner, start, length, SpanProKind.Unmanaged);
        }

        /// <summary>Span'ın tamamını verilen değerle doldurur.</summary>
        public void Fill(T value) => Fill(0, Length, value);

        /// <summary>Span'ın [start, start+length) aralığını verilen değerle doldurur.</summary>
        public void Fill(int start, int length, T value)
        {
            if ((uint)start > (uint)Length || (uint)length > (uint)(Length - start))
                throw new ArgumentOutOfRangeException();

            // Referans içeren T'yi unmanaged backinge yazmak tehlikelidir (GC taramaz)
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>() && _kind == SpanProKind.Unmanaged)
                throw new NotSupportedException("Reference-containing T cannot be stored in unmanaged backing.");

            // Hedefin ilk elemanına 'ref' al
            ref T r = ref ItemRef(start);

            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                // Write-barrier uyumlu yol: tek tek ata
                for (int i = 0; i < length; i++)
                    Unsafe.Add(ref r, i) = value;
                return;
            }

            // Değer türleri: hızlı yol
            FillRaw(ref r, length, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void FillRaw(ref T r, int length, T value)
        {
            if (length <= 0) return;

            // 1 byte'lık türler: doğrudan memset
            if (Unsafe.SizeOf<T>() == 1)
            {
                var tmp = value; // tek yükleme
                Unsafe.InitBlockUnaligned(
                    ref Unsafe.As<T, byte>(ref r),
                    Unsafe.As<T, byte>(ref tmp),
                    (uint)length
                );
                return;
            }

            // Genel yol: 8'li / 4'lü unroll + kuyruk
            nuint len = (uint)length;
            nuint es = (uint)Unsafe.SizeOf<T>();
            nuint i = 0;

            for (; i < (len & ~(nuint)7); i += 8)
            {
                Unsafe.AddByteOffset(ref r, (i + 0) * es) = value;
                Unsafe.AddByteOffset(ref r, (i + 1) * es) = value;
                Unsafe.AddByteOffset(ref r, (i + 2) * es) = value;
                Unsafe.AddByteOffset(ref r, (i + 3) * es) = value;
                Unsafe.AddByteOffset(ref r, (i + 4) * es) = value;
                Unsafe.AddByteOffset(ref r, (i + 5) * es) = value;
                Unsafe.AddByteOffset(ref r, (i + 6) * es) = value;
                Unsafe.AddByteOffset(ref r, (i + 7) * es) = value;
            }

            if (i < (len & ~(nuint)3))
            {
                Unsafe.AddByteOffset(ref r, (i + 0) * es) = value;
                Unsafe.AddByteOffset(ref r, (i + 1) * es) = value;
                Unsafe.AddByteOffset(ref r, (i + 2) * es) = value;
                Unsafe.AddByteOffset(ref r, (i + 3) * es) = value;
                i += 4;
            }

            for (; i < len; i++)
                Unsafe.AddByteOffset(ref r, i * es) = value;
        }

        // --------- Dönüşümler ve eşitlik ---------
        /// <summary>Null ise default, değilse diziden span yaratır.</summary>
        public static implicit operator SpanPro<T>(T[]? array) => array is null ? default : FromArray(array);

        public static bool operator ==(SpanPro<T> left, SpanPro<T> right) => left.Equals(right);
        public static bool operator !=(SpanPro<T> left, SpanPro<T> right) => !left.Equals(right);

        public bool Equals(SpanPro<T> other)
            => ReferenceEquals(_owner, other._owner)
               && _start == other._start
               && Length == other.Length
               && _kind == other._kind;

        public override bool Equals(object? obj)
            => obj is SpanPro<T> sp && Equals(sp);

        public override int GetHashCode()
        {
            // _owner referans hash'i + alanlar
            unchecked
            {
                int h = 17;
                h = h * 31 + (_owner?.GetHashCode() ?? 0);
                h = h * 31 + _start;
                h = h * 31 + Length;
                h = h * 31 + (int)_kind;
                return h;
            }
        }

        // --------- ToArray varyantları ---------

        /// <summary>İçeriği yeni bir diziye kopyalar. isRemoveNull: yalnızca referans veya Nullable tiplerde 'boş'ları atar.</summary>
        public T[] ToArray(bool isRemoveNull = false)
            => ToArray(0, Length, isRemoveNull);

        public T[] ToArray(int start, int length, bool isRemoveNull = false)
        {
            if ((uint)start > (uint)Length || (uint)length > (uint)(Length - start))
                throw new ArgumentOutOfRangeException();

            bool isRef = !typeof(T).IsValueType;
            bool isNullable = Nullable.GetUnderlyingType(typeof(T)) is not null;
            bool removeEmpty = isRemoveNull && (isRef || isNullable);

            if (!removeEmpty)
            {
                var arr = new T[length];
                // Slice'ı döngü dışında bir kez oluşturup aynı görünüme erişelim
                var slice = Slice(start, length);
                for (int i = 0; i < length; i++)
                    arr[i] = slice.ItemRefRO(i);
                return arr;
            }
            else
            {
                // İlk geçiş: say
                int count = 0;
                var slice = Slice(start, length);
                var cmp = EqualityComparer<T>.Default;
                for (int i = 0; i < length; i++)
                {
                    if (!cmp.Equals(slice.ItemRefRO(i), default!))
                        count++;
                }
                var arr = new T[count];
                int w = 0;
                for (int i = 0; i < length; i++)
                {
                    var v = slice.ItemRefRO(i);
                    if (!EqualityComparer<T>.Default.Equals(v, default!))
                        arr[w++] = v;
                }
                return arr;
            }
        }

        // --------- ToString varyantları ---------

        /// <summary>Debug dostu temsil. T==char ise gerçek metin üretir.</summary>
        public override string ToString()
        {
            if (typeof(T) == typeof(char))
                return ToString(0, Length, null);
            if (typeof(T) == typeof(string))
            {
                // T==string: Tüm dilimdeki toplam karakteri hesapla ve tamamını döndür
                int total = 0;
                for (int i = 0; i < Length; i++)
                {
                    var str = Unsafe.As<T, string>(ref Unsafe.AsRef(ItemRefRO(i)));
                    if (!string.IsNullOrEmpty(str)) total += str.Length;
                }
                if (total == 0) return string.Empty;
                return ToString(0, total, null);
            }

            return $"SpanPro<{typeof(T).Name}>[{Length}]";
        }

        /// <summary>T==char için [start,length] aralığını string olarak döndürür.</summary>
        /// 
        public string ToString(int start) => ToString(start, 0, null);
        public string ToString(int start, IFormatProvider? provider) => ToString(start, 0, provider);

        public string ToString(int start, int length) => ToString(start, length, null);
        public string ToString(int start, int length, IFormatProvider? provider)
        {
            var fmt = provider ?? CultureInfo.CurrentCulture;

            // 1) T == char → karakter bazlı (provider kullanılmaz)
            if (typeof(T) == typeof(char))
            {
                if (length == 0) length = Length - start;
                if ((uint)start > (uint)Length || (uint)length > (uint)(Length - start))
                    throw new ArgumentOutOfRangeException();

                if (BackingKind == SpanProKind.Array)
                {
                    var carr = Unsafe.As<object, char[]?>(ref Unsafe.AsRef(Owner));
                    if (carr is null) throw new NullReferenceException();
                    return new string(carr, Start + start, length);
                }

                var slice = Slice(start, length);
                return string.Create(length, slice, static (dst, s) =>
                {
                    for (int i = 0; i < dst.Length; i++)
                    {
                        ref readonly char ch = ref Unsafe.As<T, char>(ref Unsafe.AsRef(s.ItemRefRO(i)));
                        dst[i] = ch;
                    }
                });
            }

            // 2) T == string → karakter bazlı (provider kullanılmaz)
            if (typeof(T) == typeof(string))
            {
                int totalChars = 0;
                if (BackingKind == SpanProKind.Array)
                {
                    var arr = Unsafe.As<object, string[]?>(ref Unsafe.AsRef(Owner));
                    if (arr is null) throw new NullReferenceException();
                    checked
                    {
                        for (int i = 0; i < Length; i++)
                        {
                            var si = arr[Start + i];
                            if (!string.IsNullOrEmpty(si)) totalChars += si.Length;
                        }
                    }
                }
                else
                {
                    checked
                    {
                        for (int i = 0; i < Length; i++)
                        {
                            var si = Unsafe.As<T, string>(ref Unsafe.AsRef(ItemRefRO(i)));
                            if (!string.IsNullOrEmpty(si)) totalChars += si.Length;
                        }
                    }
                }

                if ((uint)start >= (uint)totalChars) return string.Empty;
                if (length == 0 || (uint)length > (uint)(totalChars - start)) length = totalChars - start;

                if (BackingKind == SpanProKind.Array)
                {
                    var arr = Unsafe.As<object, string[]?>(ref Unsafe.AsRef(Owner));
                    if (arr is null) throw new NullReferenceException();
                    int baseIndex = Start, spanLen = Length;

                    return string.Create(length, (arr, baseIndex, spanLen, start, length), static (dst, st) =>
                    {
                        var (a, bidx, len, stStart, stLen) = st;
                        int remStart = stStart, rem = stLen, pos = 0;

                        for (int i = 0; i < len && rem > 0; i++)
                        {
                            var seg = a[bidx + i];
                            if (string.IsNullOrEmpty(seg)) continue;

                            var span = seg.AsSpan();
                            int segLen = span.Length;
                            if (remStart >= segLen) { remStart -= segLen; continue; }

                            int copyFrom = remStart;
                            int copyLen = Math.Min(rem, segLen - copyFrom);
                            span.Slice(copyFrom, copyLen).CopyTo(dst.Slice(pos, copyLen));

                            pos += copyLen; rem -= copyLen; remStart = 0;
                        }
                    });
                }
                else
                {
                    return string.Create(length, (self: this, start, length), static (dst, st) =>
                    {
                        var self = st.self; int remStart = st.start, rem = st.length, pos = 0;

                        for (int i = 0; i < self.Length && rem > 0; i++)
                        {
                            var seg = Unsafe.As<T, string>(ref Unsafe.AsRef(self.ItemRefRO(i)));
                            if (string.IsNullOrEmpty(seg)) continue;

                            var span = seg.AsSpan();
                            int segLen = span.Length;
                            if (remStart >= segLen) { remStart -= segLen; continue; }

                            int copyFrom = remStart;
                            int copyLen = Math.Min(rem, segLen - copyFrom);
                            span.Slice(copyFrom, copyLen).CopyTo(dst.Slice(pos, copyLen));

                            pos += copyLen; rem -= copyLen; remStart = 0;
                        }
                    });
                }
            }
            else // 3) Diğer T'ler → KARAKTER bazlı (elemanlar biçimlenip zincirlenir)
            {

                // Toplam karakter sayısı (2 geçiş stratejisi)
                int totalChars = 0;
                checked
                {
                    for (int i = 0; i < Length; i++)
                    {
                        T tmp = ItemRefRO(i);

                        if (tmp is ISpanFormattable sf)
                        {
                            Span<char> buf = stackalloc char[128];
                            if (sf.TryFormat(buf, out int w, format: default, provider: fmt))
                                totalChars += w;
                            else
                            {
                                string s = sf.ToString(null, fmt) ?? string.Empty;
                                totalChars += s.Length;
                            }
                        }
                        else
                        {
                            string? s = tmp?.ToString();
                            if (!string.IsNullOrEmpty(s)) totalChars += s.Length;
                        }
                    }
                }

                if ((uint)start >= (uint)totalChars) return string.Empty;
                if (length == 0 || (uint)length > (uint)(totalChars - start)) length = totalChars - start;

                return string.Create(length, (self: this, start, length, fmt), static (dst, st) =>
                {
                    var self = st.self; var provider = (IFormatProvider?)st.fmt;
                    int remStart = st.start, rem = st.length, pos = 0;

                    for (int i = 0; i < self.Length && rem > 0; i++)
                    {
                        T tmp = self.ItemRefRO(i);

                        if (tmp is ISpanFormattable sf)
                        {
                            Span<char> buf = stackalloc char[128];
                            if (!sf.TryFormat(buf, out int w, format: default, provider: provider))
                            {
                                string s2 = sf.ToString(null, provider) ?? string.Empty;
                                var span = s2.AsSpan();
                                int segLen = span.Length;
                                if (segLen == 0) continue;

                                if (remStart >= segLen) { remStart -= segLen; continue; }
                                int copyFrom = remStart;
                                int copyLen = Math.Min(rem, segLen - copyFrom);
                                span.Slice(copyFrom, copyLen).CopyTo(dst.Slice(pos, copyLen));
                                pos += copyLen; rem -= copyLen; remStart = 0;
                            }
                            else
                            {
                                var span = buf.Slice(0, w);
                                int segLen = span.Length;
                                if (segLen == 0) continue;

                                if (remStart >= segLen) { remStart -= segLen; continue; }
                                int copyFrom = remStart;
                                int copyLen = Math.Min(rem, segLen - copyFrom);
                                span.Slice(copyFrom, copyLen).CopyTo(dst.Slice(pos, copyLen));
                                pos += copyLen; rem -= copyLen; remStart = 0;
                            }
                        }
                        else
                        {
                            string? s2 = tmp?.ToString();
                            if (string.IsNullOrEmpty(s2)) continue;

                            var span = s2.AsSpan();
                            int segLen = span.Length;
                            if (remStart >= segLen) { remStart -= segLen; continue; }
                            int copyFrom = remStart;
                            int copyLen = Math.Min(rem, segLen - copyFrom);
                            span.Slice(copyFrom, copyLen).CopyTo(dst.Slice(pos, copyLen));
                            pos += copyLen; rem -= copyLen; remStart = 0;
                        }
                    }
                });
            }
        }

        public Enumerator GetEnumerator() => new Enumerator(this);

        public ref struct Enumerator
        {
            private readonly SpanPro<T> _spanpro;
            private int _index;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal Enumerator(SpanPro<T> spanpro)
            {
                _spanpro = spanpro;
                _index = -1;
            }

            public bool MoveNext()
            {
                int index = _index + 1;
                if (index < _spanpro.Length)
                {
                    _index = index;
                    return true;
                }

                return false;
            }

            public T Current
            {
                get => _spanpro[_index];
            }
        }
    }

    internal sealed class SpanProDebugView<T>
    {
        private readonly T[] _array;

        public SpanProDebugView(SpanPro<T> span)
        {
            _array = span.ToArray();

        }

        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        public T[] Items => _array;
    }

    internal sealed class ReadOnlySpanProDebugView<T>
    {
        private readonly T[] _array;

        public ReadOnlySpanProDebugView(ReadOnlySpanPro<T> span)
        {
            _array = span.ToArray();

        }

        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        public T[] Items => _array;
    }
}
