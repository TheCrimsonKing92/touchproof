public sealed class DependencyIndexNegativeControlTests
{
    private static readonly Lazy<DependencyIndex> Fixture = new(() => DependencyIndexCache.Process.GetOrBuildImages(
        DependencyIndexFixtureAssembly.Compile("""
            #nullable enable
            using System;
            using System.Threading.Tasks;
            namespace Forbidden
            {
                public sealed class Target
                {
                    public static void Launch() { }
                }
                public interface Constraint { }
            }
            namespace Subject
            {
                public sealed class FieldOwner
                {
                    public Forbidden.Target? Value;
                }
                public sealed class ConstraintOwner<T> where T : Forbidden.Constraint { }
                public static class Caller
                {
                    public static Action MethodGroup() => Forbidden.Target.Launch;
                }
                public sealed class GeneratedBodies<T>
                {
                    public static Action Closure(int capture) => () =>
                    {
                        void Local() { if (capture != 0) Forbidden.Target.Launch(); }
                        Local();
                    };
                    public static async Task Asynchronous()
                    {
                        await Task.Yield();
                        Forbidden.Target.Launch();
                    }
                }
            }
            """)));

    [Fact]
    public void Forbidden_type_rule_reports_the_fixture_field_owner()
    {
        var index = Fixture.Value;
        var subjects = ApplicationLayerArchitectureTests.Subjects(index, "Subject")
            .Where(type => type.FullName == "Subject.FieldOwner").ToArray();

        var violation = Assert.Single(ApplicationLayerArchitectureTests.NoDependencyRule(index, subjects, "Forbidden"));

        Assert.Equal("Subject.FieldOwner", violation.Subject.FullName);
        Assert.Contains("Forbidden.Target", violation.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Manual_type_walk_reports_the_fixture_generic_constraint_owner()
    {
        var subjects = Fixture.Value.Types.Where(type => type.FullName == "Subject.ConstraintOwner`1");
        var violation = Assert.Single(DependencyIndexRules.WalkTypes(subjects,
            target => target.FullName == "Forbidden.Constraint", "fixture types"));

        Assert.Equal("Subject.ConstraintOwner`1", violation.Subject.FullName);
        Assert.Contains("Forbidden.Constraint", violation.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Call_site_rule_reports_the_fixture_method_group()
    {
        var subjects = Fixture.Value.Types.Where(type => type.FullName == "Subject.Caller");
        var violation = Assert.Single(DependencyIndexRules.ForbiddenCalls(subjects, IsForbiddenCall,
            type => $"{type.FullName} calls Forbidden.Target.Launch"));

        Assert.Equal("Subject.Caller", violation.Subject.FullName);
        Assert.Contains("Forbidden.Target.Launch", violation.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_closure_and_state_machine_calls_belong_to_the_original_methods()
    {
        var subject = Assert.Single(Fixture.Value.Types, type => type.FullName == "Subject.GeneratedBodies`1");
        Assert.Contains(subject.References, target => target.FullName == "Forbidden.Target");
        foreach (var name in new[] { "Closure", "Asynchronous" })
        {
            var method = Assert.Single(subject.Methods, method => method.Source.Name == name);
            Assert.Contains(method.Calls, IsForbiddenCall);
        }
        Assert.DoesNotContain(Fixture.Value.Types, type => type.FullName.Contains("+<", StringComparison.Ordinal));
    }

    private static bool IsForbiddenCall(DependencyMember call) =>
        call.DeclaringType.FullName == "Forbidden.Target" && call.Name == "Launch";
}
