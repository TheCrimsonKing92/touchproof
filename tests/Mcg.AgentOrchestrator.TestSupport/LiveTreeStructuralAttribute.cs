// Marks tests whose input is the candidate's repository topology rather than a symbol dependency.
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class LiveTreeStructuralAttribute : Attribute
{
}
