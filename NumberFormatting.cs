using System.Globalization;

namespace DSO.Core.SpanPro
{
    public readonly struct NumberFormatSlim
    {
        public readonly IFormatProvider Provider;
        public NumberFormatSlim(IFormatProvider provider)
            => Provider = provider ?? CultureInfo.InvariantCulture;

        public static NumberFormatSlim Invariant { get; } = new(CultureInfo.InvariantCulture);

        public static NumberFormatSlim FromSeparators(char decimalSeparator = '.', char minusSign = '-')
        {
            var nfi = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
            nfi.NumberDecimalSeparator = new string(decimalSeparator, 1);
            nfi.NegativeSign = new string(minusSign, 1);
            return new NumberFormatSlim(nfi);
        }
    }

    public static class NumberFormatting
    {
        // Dönen değer: yazılan karakter sayısı
        public static int FormatInt64(ref SpanPro<char> dst, int destStart, long value, in NumberFormatSlim fmt)
        {
            Span<char> tmp = stackalloc char[32]; // int64 için yeterli
            if (!value.TryFormat(tmp, out int written, default, fmt.Provider))
                throw new InvalidOperationException("Format failed.");
            if ((uint)destStart > (uint)(dst.Length - written))
                throw new ArgumentOutOfRangeException(nameof(destStart));

            for (int i = 0; i < written; i++)
                dst.ItemRef(destStart + i) = tmp[i];
            return written;
        }

        public static int FormatDouble(ref SpanPro<char> dst, int destStart, double value, in NumberFormatSlim fmt, ReadOnlySpan<char> format = default)
        {
            Span<char> tmp = stackalloc char[64]; // çoğu durumda yeter
            if (!value.TryFormat(tmp, out int written, format, fmt.Provider))
                throw new InvalidOperationException("Format failed.");
            if ((uint)destStart > (uint)(dst.Length - written))
                throw new ArgumentOutOfRangeException(nameof(destStart));

            for (int i = 0; i < written; i++)
                dst.ItemRef(destStart + i) = tmp[i];
            return written;
        }

        public static int FormatSingle(ref SpanPro<char> dst, int destStart, float value, in NumberFormatSlim fmt, ReadOnlySpan<char> format = default)
        {
            Span<char> tmp = stackalloc char[48];
            if (!value.TryFormat(tmp, out int written, format, fmt.Provider))
                throw new InvalidOperationException("Format failed.");
            if ((uint)destStart > (uint)(dst.Length - written))
                throw new ArgumentOutOfRangeException(nameof(destStart));

            for (int i = 0; i < written; i++)
                dst.ItemRef(destStart + i) = tmp[i];
            return written;
        }

        public static int FormatDecimal(ref SpanPro<char> dst, int destStart, decimal value, in NumberFormatSlim fmt, ReadOnlySpan<char> format = default)
        {
            Span<char> tmp = stackalloc char[96]; // decimal için geniş
            if (!value.TryFormat(tmp, out int written, format, fmt.Provider))
                throw new InvalidOperationException("Format failed.");
            if ((uint)destStart > (uint)(dst.Length - written))
                throw new ArgumentOutOfRangeException(nameof(destStart));

            for (int i = 0; i < written; i++)
                dst.ItemRef(destStart + i) = tmp[i];
            return written;
        }
    }
}
