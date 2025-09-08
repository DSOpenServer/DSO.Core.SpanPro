# SpanPro / ReadOnlySpanPro

> **Span kullanmadan** yüksek performanslı, güvenli ve esnek bir dilimleme (slicing) kütüphanesi. Tek tip ile **Array / String(RO) / Unmanaged** bellek arka planlarını kapsar; arama, kopyalama, biçimleme ve metin üretiminde **düşük GC** ve **yüksek hız** hedefler.

---

## İçindekiler
- [Neden SpanPro?](#neden-spanpro)
- [Öne Çıkan Özellikler](#öne-çıkan-özellikler)
- [Mimari](#mimari)
- [Semantik ve Güvenlik](#semantik-ve-güvenlik)
- [ToString — Karakter Bazlı Semantik](#tostring--karakter-bazlı-semantik)
- [Hızlı Başlangıç](#hızlı-başlangıç)
- [Arama Ailesi](#arama-ailesi)
- [Kopyalama & Büyütme](#kopyalama--büyütme)
- [Performans Notları](#performans-notları)
- [Memory/Span ile Farklar](#memoryspan-ile-farklar)
- [Mini API Kılavuzu](#mini-api-kılavuzu)
- [İlgili Projeler](#ilgili-projeler)
- [Lisans](#lisans)

---

## Neden SpanPro?
- **Span’sız performans**: `.NET Span<T>` kullanmadan benzer ergonomi ve hız yaklaşımı.
- **Çoklu arka plan, tek tip**: `Array`, **`String` (salt okunur)**, `Unmanaged` bellek — hepsi **owner + start + length + kind** modeliyle tek yapı altında.
- **Özel algoritmalar**: `char/byte` için **BMH (Boyer–Moore–Horspool)** ile uzun pattern aramalarında ciddi hızlanma.
- **GC-dostu**: `ArrayPool`, `stackalloc` ve iki-geçişli yazım; ara tahsisatlardan kaçınır.
- **Deterministik kimlik**: `Equals`/`GetHashCode` → *owner + start + length + kind*.

---

## Öne Çıkan Özellikler
- **Birleşik model**: `SpanPro<T>` (yazılabilir), `ReadOnlySpanPro<T>` (salt okunur)
- **Arama ailesi**: `IndexOf`, `LastIndexOf`, `FindFirst`, `FindLast`, `FindAll` (+ alt dizi aramalarında BMH hızlandırma)
- **Kopyalama**: `CopyTo`, `TryCopyTo`, `DsMemoryCopySP` (overlap güvenli, `Array.Copy` / `Buffer.MemoryCopy` hızlı yollar)
- **Dilimleme**: `Slice(start,len)` ve güvenli `Slice(start)`
- **Doldurma & Temizleme**: `Fill`, `Clear` — write-barrier kurallarına uygun
- **Dizi Üretimi**: `ToArray(start,len,isRemoveNull)` — `nullable/ref` türlerde boşları atabilme
- **Metin Üretimi**: `ToString(start,len)` **tüm T’lerde karakter bazlı**; sayısal/DateTime türleri **CurrentCulture** ile biçimlenir
- **Sayı Biçimleme Modülü**: `NumberFormatting` ile **GC-free** yazım (tek buffer, `TryFormat`), doğrudan `SpanPro<char>` üzerine

---

## Mimari
- **Model**: `owner + start + length + kind` (kind ∈ { **Array**, **StringRO**, **Unmanaged** })
- **Erişim**: `ItemRef` / `ItemRefRO` → kalıcı pointer tutmadan, **erişim anında** ref/pointer hesaplanır
- **Backings**:
  - **Array**: yazılabilir/salt okunur
  - **StringRO**: **sadece okunur**, yazmaya kalkılırsa exception
  - **Unmanaged**: `IRawMemoryOwner<T>` üzerinden; yalnızca blittable türlere yazım güvenli

---

## Semantik ve Güvenlik
- `String` arkasına yazma **yasak** (StringRO). Yazmaya kalkışılırsa `InvalidOperationException`.
- `Unmanaged` + **referans içeren `T`** → **yasak** (GC taramaz). `NotSupportedException`.
- Out of range/overlap senaryolarında uygun `ArgumentOutOfRangeException`.
- Eşitlik: aynı *owner + start + length + kind* → **eşit**.

---

## ToString — Karakter Bazlı Semantik
`ToString(int start, int length)` **tüm türlerde karakter bazlıdır**.

- **`T == char`**: `start/length` zaten karakterdir. Array arka planında `new string(char[], start, length)` hızlı yolu kullanılır.
- **`T == string`**: *[start,length]*, dizideki string parçaları **tek bir metinmiş** gibi ele alınarak uygulanır.
- **Diğer tüm `T` (int, long, decimal, DateTime, …)**: Her eleman yazıya çevrilir (önce `ISpanFormattable.TryFormat`, olmazsa `ToString()`), ardından **toplam karakter akışında** *[start,length]* uygulanır.
- **Kültür**: Varsayılan **`CultureInfo.CurrentCulture`**.

> Örnek: `long[] { 1, 23, 456 }` → `ToString(0,6)` ⇒ **"123456"**.  
> Örnek: `string[] { "abc", "def", "g" }` → `ToString(2,4)` ⇒ **"cdef"**.

---

## Hızlı Başlangıç
```csharp
using DSO.Core.SpanPro;

// 1) Dizi ile başla
var arr = new int[] { 10, 20, 30, 40, 50 };
var sp  = SpanPro<int>.FromArray(arr);            // yazılabilir
var ro  = ReadOnlySpanPro<int>.FromArray(arr);    // salt okunur

// Dilimleme & yazma
var mid = sp.Slice(1, 3); // [20,30,40]
mid.Fill(99);             // arr => {10, 99, 99, 99, 50}

// Arama
int ix = ro.IndexOf(99, 0);   // ilk 99
int lx = ro.LastIndexOf(99);  // son 99
var all = ro.FindAll(99, 0);  // {1,2,3}
```

**String (RO) üzerinde karakter bazlı `ToString`**
```csharp
var s  = "lorem ipsum lorem";
var rs = ReadOnlySpanPro<char>.FromString(s);
var pat = ReadOnlySpanPro<char>.FromString("ipsum");
int where = rs.IndexOf(pat, 0);           // alt dizi başı (BMH destekli)
string cut = rs.Slice(where, 5).ToString(0, 5); // "ipsum"
```

**String dizi → tek metin gibi**
```csharp
string[] parts = { "abc", "def", "g", "hi" };
var spS = SpanPro<string>.FromArray(parts);
spS.ToString(0, 3); // "abc"
spS.ToString(2, 4); // "cdef"
```

**Value type dizileri → karakter bazlı**
```csharp
var longs = new long[] { 1, 23, 456, 7891 };
var spL   = SpanPro<long>.FromArray(longs);
spL.ToString(0, 6); // "123456"
```

**Biçimleme modülü ile GC-free yazım**
```csharp
var buf = new char[64];
var txt = SpanPro<char>.FromArray(buf);
int w = 0;
w += NumberFormatting.FormatInt64(ref txt, w, -12345, NumberFormatSlim.FromSeparators(',', '-'));
w += NumberFormatting.FormatDouble(ref txt, w, 3.14159, NumberFormatSlim.Invariant, "F3");
string sOut = txt.ToString(0, w); // "-12,3453.142"
```

**Unmanaged örneği**
```csharp
using var um = new UnmanagedOwner<byte>(1024);
var ub = SpanPro<byte>.FromUnmanagedOwner(um, 0, 1024);
ub.Fill(0x20);
ub.ItemRef(100) = 0x2A;
int pos = ub.IndexOf((byte)0x2A, 0);
var dst = new byte[1024];
ub.CopyTo(dst, 0); // Buffer.MemoryCopy hızlı yolu
```

---

## Arama Ailesi
- **Tek değer**: `IndexOf(value, start)`, `LastIndexOf(value)`
- **Alt dizi**: `IndexOf(pattern, start)`, `LastIndexOf(pattern, end)`
- **İlk/Son**: `FindFirst(value|pattern)`, `FindLast(value|pattern)`
- **Çoklu eşleşme**: `FindAll(value, start)` → `int[]` (büyütmeli, `ArrayPool<int>`)
- **BMH**: `char/byte` için uzun patternlerde agresif hızlanma

> Sıcak döngülerde `_kind` dallanması **dışarı alınmıştır**; Array/Unmanaged yollarında doğrudan ref/pointer ilerler.

---

## Kopyalama & Büyütme
- **CopyTo**: aynı owner’da `Array.Copy` / `Buffer.MemoryCopy` hızlı yollar
- **DsMemoryCopySP**: overlap güvenli, WB-uyumlu genel yol
- **Grow**: `ArrayPool<T>` ile kapasiteyi artırır; `SpanPro.Length` yeni kapasiteyi yansıtır ("mantıksal uzunluk vs kapasite" notunu dokümantasyonda belirtin)

---

## Performans Notları
- **Uzun pattern araması (char/byte)**: BMH ile **çok güçlü**
- **Tek değer arama / sıkı döngü**: dallanma azaltıldı; Array/Unmanaged’ta düz ref/pointer ile **yakın** performans
- **Kopyalama**: hızlı yollarla BCL’e **başa baş**
- **Biçimleme**: `NumberFormatting` ile ara string oluşturmadan **GC-free** yazım

> Net değerlendirme için `BenchmarkDotNet` ile aynı veri üzerinde SpanPro ve referans implementasyonları karşılaştırın.

---

## Memory/Span ile Farklar
- `Memory<T>`/`ReadOnlyMemory<T>` BCL ekosistemiyle (Pipelines, `IBufferWriter<T>`, `MemoryExtensions`) entegredir; SpanPro **Span kullanmadan** aynı problemi çözer.
- `ReadOnlySpanPro<char>` string’i doğal kapsar; `SpanPro<char>` string’e yazma **yasak**.
- Arama/kopyalama algoritmaları kütüphanenin içinde özelleştirilmiştir (BMH, overlap-safe copy).

---

## Mini API Kılavuzu
```csharp
// Oluşturma
SpanPro<T>.FromArray(T[] arr, int start = 0, int length = arr.Length)
ReadOnlySpanPro<T>.FromArray(T[] arr, int start = 0, int length = arr.Length)
ReadOnlySpanPro<char>.FromString(string s, int start = 0, int length = s.Length)
SpanPro<T>.FromUnmanagedOwner(IRawMemoryOwner<T> owner, int start, int length)

// Erişim
ref T       ItemRef(int index);      // yazılabilir (StringRO’da exception)
ref readonly T ItemRefRO(int index); // salt okunur

// Kullanışlılar
Slice(int start, int length)
ToArray(int start, int length, bool isRemoveNull = false)
CopyTo(T[] destination, int destStart)
Fill(value) / Clear()
IndexOf / LastIndexOf / FindAll / (BMH) IndexOf(pattern)
ToString(int start, int length) // tüm türlerde karakter bazlı
```