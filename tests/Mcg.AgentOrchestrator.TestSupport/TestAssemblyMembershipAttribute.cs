// Marks tests whose input includes the set of types in their declaring test assembly.
// This is separate from LiveTreeStructural, whose input is repository topology.
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class TestAssemblyMembershipAttribute : Attribute
{
}
