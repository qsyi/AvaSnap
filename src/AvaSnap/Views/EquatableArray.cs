namespace AvaSnap.Views;

/// <summary>要素ごとの構造比較をする不変配列ラッパー。record の自動生成 Equals は
/// 配列フィールドを参照比較してしまうため、<see cref="CompositeSnapshot"/> に
/// 生 <c>T[]</c> を持たせると毎回の Capture が別インスタンスになり
/// 「変化なし(before == after)」判定が壊れる。IEquatable を実装したこの struct を
/// 挟むと record 側の自動等価がそのまま正しく動く(Roslyn ジェネレータ等で定番の型)。</summary>
public readonly struct EquatableArray<T>(T[]? array) : IEquatable<EquatableArray<T>> where T : IEquatable<T>
{
    private readonly T[]? _array = array;

    public T[] AsArray() => _array ?? Array.Empty<T>();

    public bool Equals(EquatableArray<T> other) =>
        ((ReadOnlySpan<T>)AsArray()).SequenceEqual(other.AsArray());

    public override bool Equals(object? obj) => obj is EquatableArray<T> o && Equals(o);

    public override int GetHashCode()
    {
        var hc = new HashCode();
        foreach (var item in AsArray()) hc.Add(item);
        return hc.ToHashCode();
    }
}
