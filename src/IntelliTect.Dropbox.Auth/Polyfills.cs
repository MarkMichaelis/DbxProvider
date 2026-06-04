#if NETSTANDARD2_0
namespace System.Runtime.CompilerServices
{
    using System.ComponentModel;

    /// <summary>
    /// Polyfill enabling C# <c>init</c>-only setters and records on
    /// netstandard2.0, which predates this compiler-required type.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit { }
}
#endif
