internal sealed record DependencyViolation(DependencyDeclaration Subject, string Description);

internal static class DependencyIndexRules
{
    public static DependencyViolation[] ForbiddenTypes(DependencyIndex index,
        IEnumerable<DependencyDeclaration> subjects, Func<DependencyType, bool> forbidden) =>
        WalkTypes(subjects, target => index.DefinedTypes.Contains(target) && forbidden(target), "forbidden types");

    public static DependencyViolation[] WalkTypes(IEnumerable<DependencyDeclaration> subjects,
        Func<DependencyType, bool> forbidden, string targetDescription)
    {
        var failures = new List<DependencyViolation>();
        foreach (var subject in subjects)
        {
            var targets = subject.References.Where(forbidden).Select(target => target.FullName)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (targets.Length != 0)
                failures.Add(new(subject, $"{subject.FullName} depends on {targetDescription}: {string.Join(", ", targets)}"));
        }
        return failures.ToArray();
    }

    public static DependencyViolation[] ForbiddenCalls(IEnumerable<DependencyDeclaration> subjects,
        Func<DependencyMember, bool> forbidden, Func<DependencyDeclaration, string> describe) =>
        subjects.Where(subject => subject.Methods.Any(method => method.Calls.Any(forbidden)))
            .Select(subject => new DependencyViolation(subject, describe(subject))).ToArray();

    public static string ToErrorMessage(this IEnumerable<DependencyViolation> failures) =>
        string.Join(Environment.NewLine, failures.Select(failure => failure.Description));
}
