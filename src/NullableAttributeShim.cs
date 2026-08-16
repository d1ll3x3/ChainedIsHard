// CS0656 fix, the same one ChaosIsHard and FIHMapEditor carry for the same reason: a
// referenced interop assembly (Il2Cppmscorlib) defines
// System.Runtime.CompilerServices.NullableAttribute WITHOUT the byte constructor the C#
// compiler emits for lambda and closure metadata, and net6.0 does not ship the attribute
// either. Defining it in source wins the well-known-member lookup. Metadata only, no runtime
// behaviour.
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.All, Inherited = false)]
    internal sealed class NullableAttribute : Attribute
    {
        public readonly byte[] NullableFlags;

        public NullableAttribute(byte flag) { NullableFlags = new[] { flag }; }
        public NullableAttribute(byte[] flags) { NullableFlags = flags; }
    }

    [AttributeUsage(AttributeTargets.Module | AttributeTargets.Class | AttributeTargets.Delegate
        | AttributeTargets.Interface | AttributeTargets.Method | AttributeTargets.Struct, Inherited = false)]
    internal sealed class NullableContextAttribute : Attribute
    {
        public readonly byte Flag;

        public NullableContextAttribute(byte flag) { Flag = flag; }
    }
}
