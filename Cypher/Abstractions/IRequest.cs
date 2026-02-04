namespace Cypher;

public interface IRequest<out TResponse> : IBaseRequest;

public interface IRequest : IRequest<Unit>;

public interface IBaseRequest;

public readonly struct Unit : IEquatable<Unit>, IComparable<Unit>, IComparable
{
    public static readonly Unit Value = new();

    public int CompareTo(Unit other) => 0;
    public int CompareTo(object? obj) => 0;
    public bool Equals(Unit other) => true;
    public override bool Equals(object? obj) => obj is Unit;
    public override int GetHashCode() => 0;
    public override string ToString() => "()";

    public static bool operator ==(Unit left, Unit right) => true;
    public static bool operator !=(Unit left, Unit right) => false;
}
